using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// Per-Windows-user encrypted history of authenticated MikroTik connections.
/// Opt-in password storage; no unprotected legacy data is imported.
/// </summary>
public sealed record RouterConnectionProfile(
    string Host, int Port, string Username, bool UseSsl, bool VerifySsl,
    bool RemoteMode, string Password = "")
{
    public string DisplayName => $"{Host}:{Port} — {Username}";
    public string Key => $"{(RemoteMode ? "remote" : "local")}|{Host.ToLowerInvariant()}|{Port}|{Username}";
    public RouterConnectionProfile WithoutPassword() => this with { Password = "" };
}

public interface IRouterHistoryProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public sealed class WindowsRouterHistoryProtector : IRouterHistoryProtector
{
    private static readonly byte[] Entropy = "HadramoutNET.RouterConnectionHistory.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("محفوظات الاتصال المحمية تتطلب Windows.");
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("محفوظات الاتصال المحمية تتطلب Windows.");
        return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
    }
}

public sealed class RouterConnectionHistoryStore
{
    public const int MaxProfiles = 30;
    public const int MaxEncryptedBytes = 256 * 1024;
    private static readonly Regex IpOrHost = new(@"^[a-zA-Z0-9.\-:\[\]]{1,255}$",
        RegexOptions.CultureInvariant);

    private readonly string _path;
    private readonly IRouterHistoryProtector _protector;

    public RouterConnectionHistoryStore(string localAppDataRoot,
        IRouterHistoryProtector? protector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);
        _path = Path.Combine(Path.GetFullPath(localAppDataRoot), "HadramoutNET", "connections", "history.dpapi");
        _protector = protector ?? new WindowsRouterHistoryProtector();
    }

    public static RouterConnectionHistoryStore ForCurrentUser() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public IReadOnlyList<RouterConnectionProfile> Read()
    {
        VerifyProtectedPath();
        if (!File.Exists(_path)) return [];
        long size = new FileInfo(_path).Length;
        if (size is 0 or > MaxEncryptedBytes)
            throw new InvalidDataException("حجم محفوظات الاتصالات غير صالح.");
        byte[] ciphertext = File.ReadAllBytes(_path);
        if (ciphertext.Length is 0 or > MaxEncryptedBytes)
            throw new InvalidDataException("حجم محفوظات الاتصالات غير صالح.");
        byte[] plaintext = _protector.Unprotect(ciphertext);
        try
        {
            var profiles = JsonSerializer.Deserialize<RouterConnectionProfile[]>(plaintext)
                ?? throw new InvalidDataException("تنسيق محفوظات الاتصالات غير صالح.");
            if (profiles.Length > MaxProfiles || profiles.Any(p => !Valid(p)))
                throw new InvalidDataException("المحفوظات تحتوي على اتصال غير صالح.");
            if (profiles.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != profiles.Length)
                throw new InvalidDataException("المحفوظات تحتوي على اتصالات مكررة.");
            return profiles;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void Upsert(RouterConnectionProfile profile, bool rememberPassword)
    {
        if (!Valid(profile))
            throw new ArgumentException("بيانات اتصال MikroTik غير صالحة.", nameof(profile));
        List<RouterConnectionProfile> profiles = Read().ToList();
        profiles.RemoveAll(p => p.Key == profile.Key);
        profiles.Insert(0, rememberPassword ? profile : profile.WithoutPassword());
        if (profiles.Count > MaxProfiles)
            profiles.RemoveRange(MaxProfiles, profiles.Count - MaxProfiles);
        Write(profiles);
    }

    public void Delete(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Write(Read().Where(p => !string.Equals(p.Key, key, StringComparison.Ordinal)).ToArray());
    }

    private void Write(IReadOnlyList<RouterConnectionProfile> profiles)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(profiles);
        try
        {
            byte[] ciphertext = _protector.Protect(plaintext);
            try
            {
                if (ciphertext.Length is 0 or > MaxEncryptedBytes)
                    throw new InvalidDataException("حجم البيانات المحمية غير صالح.");
                VerifyProtectedPath();
                string directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                VerifyProtectedPath();
                string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var file = new FileStream(temp, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None))
                    {
                        file.Write(ciphertext);
                        file.Flush(flushToDisk: true);
                    }
                    VerifyProtectedPath();
                    File.Move(temp, _path, overwrite: true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            finally { CryptographicOperations.ZeroMemory(ciphertext); }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private void VerifyProtectedPath()
    {
        string directory = Path.GetDirectoryName(_path)!;
        string parent = Path.GetDirectoryName(directory)!;
        foreach (string path in new[] { parent, directory, _path })
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        "محفوظات الاتصالات تحوي رابط ملفات غير آمن؛ لن تُحفظ أو تُقرأ.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static bool Valid(RouterConnectionProfile? p) =>
        p is not null && IpOrHost.IsMatch(p.Host) && p.Port is >= 1 and <= 65535 &&
        !string.IsNullOrWhiteSpace(p.Username) && p.Username.Length <= 128 &&
        !p.Username.Any(char.IsControl) && p.Password is not null &&
        p.Password.Length <= 1024;
}
