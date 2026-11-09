using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HadramoutNET.Core.Licensing;

/// <summary>
/// Safe .NET continuation of the original 480-hour free trial.
/// Reads original Hadramout trial mirrors but NEVER modifies them.
/// A new, independent machine-scoped DPAPI record is committed to both
/// the current user's LOCALAPPDATA and shared ProgramData after successful
/// RouterOS authentication. Existing original starts always take precedence.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrialStateManager
{
    private readonly string _userFile;
    private readonly string _sharedFile;
    private readonly string _machineId;
    private readonly Func<DateTimeOffset, LegacyTrialReadResult> _original;
    private readonly object _gate = new();

    private TrialStateManager(
        string userRoot, string sharedRoot, string machineId,
        Func<DateTimeOffset, LegacyTrialReadResult> original)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        _userFile = Path.Combine(Path.GetFullPath(userRoot),
            "HadramoutNET", "licensing", "activation-state.bin");
        _sharedFile = Path.Combine(Path.GetFullPath(sharedRoot),
            "HadramoutNET", "licensing", "trial-machine.bin");
        _machineId = machineId;
        _original = original ?? throw new ArgumentNullException(nameof(original));
    }

    public static TrialStateManager ForCurrentWindowsUser()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("يتطلب سجل التجربة Windows.");
        string user = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(common))
            throw new InvalidOperationException("تعذر تحديد مجلدات حماية التجربة.");
        return new TrialStateManager(user, common,
            WindowsLicenseIdentity.ReadCurrent().TrialMachineId, LegacyTrialStateReader.ReadOriginal);
    }

    // Only synthetic tests can inject the old read-only source/machine ID.
    internal static TrialStateManager CreateForTests(
        string userRoot, string sharedRoot, string machineId,
        Func<DateTimeOffset, LegacyTrialReadResult> legacy) =>
        new(userRoot, sharedRoot, machineId, legacy);

    /// <summary>
    /// Safe to run on app startup. It never creates a trial or updates clocks.
    /// A missing original AND missing .NET record means a free trial is READY,
    /// not started. A damaged original/new record is fail-closed.
    /// </summary>
    public TrialAccess Preview(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var merged = Load(nowUtc);
            if (merged.Invalid)
                return Fail();
            return TrialAccessPolicy.Evaluate(merged.Snapshot, nowUtc, _machineId);
        }
    }

    /// <summary>
    /// MUST be called ONLY after the live RouterOsApiClient successfully
    /// authenticated and read /system/identity/print. A connection test alone
    /// is NOT login and never calls this function.
    /// Paid users must also record their first successful RouterOS login,
    /// so license revocation cannot reserve another 480-hour period.
    /// </summary>
    public TrialAccess ConfirmAfterAuthenticatedRouterLogin(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var merged = Load(nowUtc);
            if (merged.Invalid)
                return Fail();

            TrialSnapshot record = merged.Snapshot ??
                new TrialSnapshot(nowUtc.ToUniversalTime(), nowUtc.ToUniversalTime(), _machineId);
            var prewrite = TrialAccessPolicy.Evaluate(record, nowUtc, _machineId);
            if (prewrite.Reason is "clock_rollback" or "clock_changed" or "state_invalid")
                return prewrite;

            // Persist the first start even for paid-license holders. Do not
            // rewrite otherwise unless a known older record needs adoption.
            if (merged.Snapshot is null || merged.NewRecordMissing ||
                nowUtc.ToUniversalTime() > record.Last.AddSeconds(60))
            {
                var updated = record with
                {
                    Last = nowUtc.ToUniversalTime() > record.Last ?
                        nowUtc.ToUniversalTime() : record.Last
                };
                if (!WriteToIndependentMirrors(updated))
                    return Fail();
                record = updated;
            }

            return TrialAccessPolicy.Evaluate(record, nowUtc, _machineId);
        }
    }

    /// <summary>
    /// Called during an already authorized session, not at app startup.
    /// The start is immutable; the verified-clock watermark increases only.
    /// </summary>
    public TrialAccess AdvanceVerifiedClock(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var state = Load(nowUtc);
            if (state.Invalid)
                return Fail();
            var result = TrialAccessPolicy.Evaluate(state.Snapshot, nowUtc, _machineId);
            if (state.Snapshot is null || !result.Permitted)
                return result;
            if (nowUtc.ToUniversalTime() > state.Snapshot.Last.AddSeconds(60))
            {
                var update = state.Snapshot with { Last = nowUtc.ToUniversalTime() };
                if (!WriteToIndependentMirrors(update))
                    return Fail();
                return TrialAccessPolicy.Evaluate(update, nowUtc, _machineId);
            }
            return result;
        }
    }

    internal TrialSnapshot? ReadSnapshot(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var state = Load(nowUtc);
            return state.Invalid ? null : state.Snapshot;
        }
    }

    private (TrialSnapshot? Snapshot, bool NewRecordMissing, bool Invalid) Load(DateTimeOffset nowUtc)
    {
        LegacyTrialReadResult old;
        LegacyTrialReadResult current;
        try
        {
            old = _original(nowUtc);
            current = LegacyTrialStateReader.ReadFromPaths(
                _userFile, _sharedFile, null, _machineId, nowUtc);
        }
        catch (Exception)
        {
            return (null, false, true);
        }

        if (old.Status == "state_invalid" || current.Status == "state_invalid" ||
            old.Status is not ("ok" or "not_started") ||
            current.Status is not ("ok" or "not_started"))
            return (null, false, true);

        var valid = new List<TrialSnapshot>(2);
        if (old.Snapshot is not null) valid.Add(old.Snapshot);
        if (current.Snapshot is not null) valid.Add(current.Snapshot);
        if (valid.Count == 0)
            return (null, true, false);

        // The oldest known start always wins. Even partially corrupted or
        // divergent valid mirrors cannot "renew" the original free period.
        var merged = new TrialSnapshot(
            valid.Min(v => v.First),
            valid.Max(v => v.Last),
            _machineId,
            valid.Max(v => v.ActivationRevision));

        if (valid.Any(v => v.MachineId != _machineId) || merged.First > merged.Last)
            return (null, false, true);
        return (merged, current.Status == "not_started", false);
    }

    private bool WriteToIndependentMirrors(TrialSnapshot state)
    {
        if (state.MachineId != _machineId || state.First > state.Last)
            return false;

        byte[]? plain = null;
        byte[]? encrypted = null;
        try
        {
            byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(new
            {
                activation_rev = state.ActivationRevision,
                first = state.First.ToUniversalTime().ToString("O"),
                last = state.Last.ToUniversalTime().ToString("O"),
                license_id = "",
                machine_id = _machineId,
                schema = 2
            });
            plain = serialized;
            encrypted = ProtectedData.Protect(
                serialized, optionalEntropy: null, scope: DataProtectionScope.LocalMachine);
            if (encrypted.Length > LegacyTrialStateReader.MaxStateBytes)
                return false;

            // Mandatory shared mirror prevents a trial from resetting merely
            // by using another local Windows account on the same installation.
            // If common storage is unavailable, deny instead of silently
            // falling back to an isolated user-only trial.
            SaveAtomically(_sharedFile, encrypted);
            SaveAtomically(_userFile, encrypted);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or CryptographicException or ArgumentException or InvalidOperationException
            or NotSupportedException)
        {
            return false;
        }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private static void SaveAtomically(string target, byte[] bytes)
    {
        string directory = Path.GetDirectoryName(target)!;
        string root = Path.GetDirectoryName(directory)!;
        RefuseSymlinks(root);
        RefuseSymlinks(directory);
        RefuseSymlinks(target);
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".activation-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            RefuseSymlinks(target);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RefuseSymlinks(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("مسار تخزين التجربة غير آمن.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static TrialAccess Fail() =>
        new("locked", "state_invalid",
            "تعذر التأكد من سجل التجربة المحمي؛ استخدم ترخيصا صالحا.", 0);
}
