using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace HadramoutNET.Core.Licensing;

/// <summary>
/// A stored code is only a candidate for online verification, NEVER a grant.
/// "legacy_read_only" refers exclusively to the original Supabase v1.1.3 file,
/// not the obsolete GitHub/Cloudflare license token.
/// </summary>
public sealed record PaidCodeReadResult(string Status, string Code = "")
{
    public bool HasCode => (Status is "stored" or "legacy_read_only") && Code.Length == 32;
}

/// <summary>
/// Keeps the new .NET paid code in its own per-user, DPAPI-protected file.
/// Can READ the original Python Supabase code as a fallback, but never writes
/// into the original application, registry, trial store or licensing folder.
/// Does not issue, claim or validate paid entitlement.
/// </summary>
public sealed class PaidCodeStore
{
    public const int MaxProtectedBytes = 8192;
    private const int MaxLegacyBytes = 64;
    private readonly string _root;

    public string ProtectedPath => Path.Combine(_root, "HadramoutNET", "licensing", "paid-code.dpapi");
    public string LegacyReadOnlyPath =>
        Path.Combine(_root, "NidiMikroTikCards", "licensing", "supabase-license-id.txt");

    public PaidCodeStore(string localAppDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);
        _root = Path.GetFullPath(localAppDataRoot);
    }

    public static PaidCodeStore ForCurrentUser()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("تعذر تحديد مجلد ملفات المستخدم.");
        return new PaidCodeStore(path);
    }

    /// <summary>
    /// A damaged .NET encrypted file never falls back to an old code: this
    /// prevents silently replacing an intentionally changed license code.
    /// </summary>
    public PaidCodeReadResult Read()
    {
        if (!OperatingSystem.IsWindows())
            return Invalid();

        var ownFile = ReadFile(ProtectedPath, MaxProtectedBytes);
        if (ownFile.Status == "invalid") return Invalid();
        if (ownFile.Status == "ok")
        {
            byte[]? plaintext = null;
            try
            {
                plaintext = ProtectedData.Unprotect(
                    ownFile.Bytes!, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
                string raw = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(plaintext);
                string code = SignedLicenseFeed.NormalizeCode(raw);
                return code.Length == 32 && string.Equals(raw, code, StringComparison.Ordinal) ?
                    new PaidCodeReadResult("stored", code) : Invalid();
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException
                or DecoderFallbackException or PlatformNotSupportedException)
            {
                return Invalid();
            }
            finally
            {
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        // Original Python's new Supabase file is a READ-ONLY compatibility
        // source. No old github-license-id.txt file is read at any point.
        var original = ReadFile(LegacyReadOnlyPath, MaxLegacyBytes);
        if (original.Status == "missing")
            return new PaidCodeReadResult("not_found");
        if (original.Status != "ok")
            return Invalid();
        string candidate = Encoding.ASCII.GetString(original.Bytes!);
        string normalized = SignedLicenseFeed.NormalizeCode(candidate);
        return normalized.Length == 32 ?
            new PaidCodeReadResult("legacy_read_only", normalized) : Invalid();
    }

    /// <summary>
    /// Called explicitly after a user has entered/confirmed a candidate
    /// activation code. Saving it is NOT proof of license validity.
    /// No trial clock or legacy mirror is touched on renewal.
    /// </summary>
    public void SaveEnteredCode(string? code)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("يعمل التخزين المحمي على Windows فقط.");
        string normalized = SignedLicenseFeed.NormalizeCode(code);
        if (normalized.Length != 32)
            throw new ArgumentException("رمز الترخيص يجب أن يتكون من 32 خانة صحيحة.", nameof(code));

        string folder = Path.GetDirectoryName(ProtectedPath)!;
        AssertNoReparse(Path.Combine(_root, "HadramoutNET"));
        AssertNoReparse(folder);
        AssertNoReparse(ProtectedPath);

        Directory.CreateDirectory(folder);
        byte[] plaintext = Encoding.ASCII.GetBytes(normalized);
        byte[] encrypted;
        try
        {
            encrypted = ProtectedData.Protect(
                plaintext, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
        if (encrypted.Length > MaxProtectedBytes)
            throw new InvalidOperationException("حجم سجل الترخيص المحمي غير صالح.");

        string staged = Path.Combine(folder, ".license-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(encrypted);
                file.Flush(flushToDisk: true);
            }
            // Reject a destination that has become a symlink since the
            // initial check; staging stays on the same volume for atomic move.
            AssertNoReparse(ProtectedPath);
            File.Move(staged, ProtectedPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    private static void AssertNoReparse(string path)
    {
        try
        {
            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("رابط ملف أو مجلد غير آمن في بيانات الترخيص.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static (string Status, byte[]? Bytes) ReadFile(string path, int maxBytes)
    {
        try
        {
            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                return ("invalid", null);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (file.Length > maxBytes || file.Length == 0)
                return ("invalid", null);
            using var data = new MemoryStream();
            byte[] buffer = new byte[512];
            int count;
            while ((count = file.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (data.Length + count > maxBytes) return ("invalid", null);
                data.Write(buffer, 0, count);
            }
            return ("ok", data.ToArray());
        }
        catch (FileNotFoundException) { return ("missing", null); }
        catch (DirectoryNotFoundException) { return ("missing", null); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
            or ArgumentException or NotSupportedException)
        {
            return ("invalid", null);
        }
    }

    private static PaidCodeReadResult Invalid() => new("invalid");
}
