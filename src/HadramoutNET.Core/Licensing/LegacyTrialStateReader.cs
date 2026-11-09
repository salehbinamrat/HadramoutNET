using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HadramoutNET.Core.Licensing;

/// <summary>
/// Read-only reconciliation of the Python app's DPAPI-protected trial records.
/// A corrupt/unavailable record must never look like a fresh, unstarted trial.
/// </summary>
public sealed record LegacyTrialReadResult(
    string Status, bool HasHistory, bool NeedsRepair, TrialSnapshot? Snapshot);

public static class LegacyTrialStateReader
{
    public const int MaxStateBytes = 8192;
    private const string RegistryPath = @"Software\Nidi\MikroTikCards\Licensing";
    private const string RegistryValue = "ActivationStateV1";
    private static readonly byte[] Invalid = [0];

    /// <summary>
    /// Reads original state locations (not the new application's own folder).
    /// Never writes the original registry, ProgramData, LOCALAPPDATA or DPAPI
    /// mirror. The caller MUST NOT create a new trial if any mirror is invalid.
    /// </summary>
    public static LegacyTrialReadResult ReadOriginal(DateTimeOffset nowUtc)
    {
        if (!OperatingSystem.IsWindows())
            return Fail(hasHistory: true);
        try
        {
            string fingerprint = WindowsMachineFingerprint();
            string? local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(local))
                local = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrWhiteSpace(local))
                return Fail(hasHistory: true);

            string userPath = Path.Combine(local, "NidiMikroTikCards", "licensing", "activation-state.bin");
            string? common = Environment.GetEnvironmentVariable("PROGRAMDATA");
            string? machinePath = string.IsNullOrWhiteSpace(common) ? null :
                Path.Combine(common, "NidiMikroTikCards", "licensing", "trial-machine.bin");
            byte[]? registry = ReadRegistryMirror();
            return ReadFromPaths(userPath, machinePath, registry, fingerprint, nowUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or CryptographicException)
        {
            return Fail(hasHistory: true);
        }
    }

    /// <summary>
    /// The original Python trial fingerprint uses the exact prefix below and
    /// a lower-cased 64-bit Windows MachineGuid (same on x86/x64).
    /// </summary>
    public static string HashTrialMachineIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        string normalized = identifier.Trim().ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "NIDI-MIKROTIK-CARDS-TRIAL-V2|" + normalized)));
    }

    [SupportedOSPlatform("windows")]
    private static string WindowsMachineFingerprint()
    {
        RegistryView view = Environment.Is64BitOperatingSystem ?
            RegistryView.Registry64 : RegistryView.Registry32;
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
        if (key is null || key.GetValueKind("MachineGuid") != RegistryValueKind.String ||
            key.GetValue("MachineGuid") is not string guid ||
            string.IsNullOrWhiteSpace(guid))
            throw new InvalidOperationException("Windows MachineGuid is unavailable.");
        // Never substitute a new random ID: this must match the old Python
        // installation or the existing 480-hour trial would be reset.
        return HashTrialMachineIdentifier(guid);
    }

    [SupportedOSPlatform("windows")]
    private static byte[]? ReadRegistryMirror()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            if (key is null) return null;
            if (!key.GetValueNames().Contains(RegistryValue, StringComparer.OrdinalIgnoreCase))
                return null;
            if (key.GetValueKind(RegistryValue) != RegistryValueKind.String ||
                key.GetValue(RegistryValue) is not string encoded ||
                encoded.Length > ((MaxStateBytes + 2) / 3) * 4 ||
                !Regex.IsMatch(encoded, @"\A[A-Za-z0-9+/]*={0,2}\z", RegexOptions.CultureInvariant))
                return Invalid;
            byte[] bytes = Convert.FromBase64String(encoded);
            return bytes.Length <= MaxStateBytes ? bytes : Invalid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or FormatException or InvalidOperationException)
        {
            return Invalid;
        }
    }

    internal static LegacyTrialReadResult ReadFromPaths(
        string userFile, string? machineFile, byte[]? registryMirror,
        string fingerprint, DateTimeOffset nowUtc) =>
        ReadMirrors(ReadFileMirror(userFile), registryMirror,
            machineFile is null ? null : ReadFileMirror(machineFile), fingerprint, nowUtc);

    private static byte[]? ReadFileMirror(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) != 0)
                return Invalid;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > MaxStateBytes) return Invalid;
            using var buffer = new MemoryStream();
            byte[] block = new byte[1024];
            while (true)
            {
                int size = file.Read(block, 0, block.Length);
                if (size == 0) break;
                if (buffer.Length + size > MaxStateBytes) return Invalid;
                buffer.Write(block, 0, size);
            }
            return buffer.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return Invalid;
        }
    }

    /// <summary>
    /// Python's StateStore.load() merges the oldest first, newest last and
    /// highest activation revision across all valid mirrors. We read all
    /// mirrors and preserve those exact semantics but intentionally NEVER
    /// repair/write a mirror in this new program.
    /// </summary>
    internal static LegacyTrialReadResult ReadMirrors(
        byte[]? userFile, byte[]? registry, byte[]? machineFile,
        string fingerprint, DateTimeOffset nowUtc)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(fingerprint))
            return Fail(hasHistory: true);

        byte[]?[] copies = [userFile, registry, machineFile];
        if (copies.All(copy => copy is null))
            return new LegacyTrialReadResult("not_started", false, false, null);

        var valid = new List<TrialSnapshot>();
        foreach (byte[]? copy in copies)
        {
            if (copy is null || copy.Length == 0 || copy.Length > MaxStateBytes)
                continue;
            TrialSnapshot? decoded = Decode(copy, fingerprint, nowUtc);
            if (decoded is not null) valid.Add(decoded);
        }
        if (valid.Count == 0)
            return Fail(hasHistory: true);

        DateTimeOffset earliest = valid.Min(item => item.First);
        DateTimeOffset latest = valid.Max(item => item.Last);
        int revision = valid.Max(item => item.ActivationRevision);
        // Last (verified clock) and first (trial start) come from distinct
        // mirrors if necessary; the trial is NEVER extended by a renewal.
        var merged = new TrialSnapshot(earliest, latest, fingerprint, revision);
        bool needsRepair = valid.Count != copies.Length ||
            valid.Any(item => item.First != earliest || item.Last != latest ||
                item.ActivationRevision != revision);
        return new LegacyTrialReadResult("ok", true, needsRepair, merged);
    }

    [SupportedOSPlatform("windows")]
    private static TrialSnapshot? Decode(
        byte[] encrypted, string fingerprint, DateTimeOffset nowUtc)
    {
        try
        {
            byte[] plaintext = ProtectedData.Unprotect(
                encrypted, optionalEntropy: null, scope: DataProtectionScope.LocalMachine);
            string json = new UTF8Encoding(false, true).GetString(plaintext);
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 64
            });
            if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
                DuplicateProperty(parsed.RootElement))
                return null;

            JsonElement root = parsed.RootElement;
            if (!root.TryGetProperty("schema", out JsonElement schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) ||
                version is not (1 or 2) ||
                !root.TryGetProperty("license_id", out JsonElement legacyCode) ||
                legacyCode.ValueKind != JsonValueKind.String)
                return null;

            string legacyLicenseId = legacyCode.GetString() ?? "";
            if (legacyLicenseId.Length != 0 &&
                (legacyLicenseId.Length != 16 ||
                 !legacyLicenseId.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')))
                return null;

            if (version == 2 &&
                (!root.TryGetProperty("machine_id", out JsonElement sourceMachine) ||
                 sourceMachine.ValueKind != JsonValueKind.String ||
                 sourceMachine.GetString() != fingerprint))
                return null;

            if (!root.TryGetProperty("first", out JsonElement firstValue) ||
                firstValue.ValueKind != JsonValueKind.String ||
                !TryOriginalTimestamp(firstValue.GetString(), out DateTimeOffset first) ||
                !root.TryGetProperty("last", out JsonElement lastValue) ||
                lastValue.ValueKind != JsonValueKind.String ||
                !TryOriginalTimestamp(lastValue.GetString(), out DateTimeOffset last) ||
                first > last || last > nowUtc.ToUniversalTime().AddDays(36500))
                return null;

            int revision = 0;
            if (root.TryGetProperty("activation_rev", out JsonElement revValue) &&
                (revValue.ValueKind != JsonValueKind.Number ||
                 !revValue.TryGetInt32(out revision) || revision < 0))
                return null;
            return new TrialSnapshot(first, last, fingerprint, revision);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException
            or FormatException or ArgumentException or DecoderFallbackException
            or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    private static bool TryOriginalTimestamp(string? text, out DateTimeOffset result)
    {
        result = default;
        return !string.IsNullOrEmpty(text) &&
            Regex.IsMatch(text, @"(?:Z|[+-][0-9]{2}:[0-9]{2})\z",
                RegexOptions.CultureInvariant) &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out result);
    }

    private static bool DuplicateProperty(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().Any(DuplicateProperty);
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
            if (!seen.Add(property.Name) || DuplicateProperty(property.Value))
                return true;
        return false;
    }

    private static LegacyTrialReadResult Fail(bool hasHistory) =>
        new("state_invalid", hasHistory, false, null);
}
