using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HadramoutNET.Core.RouterOS;

/// <summary>A saved endpoint. Its password is never included in its display name.</summary>
public sealed record SavedRouterConnection(
    string Host, string ApiPort, string Username, string Password,
    string ConnectionMode, bool UseSsl, bool VerifySsl)
{
    [JsonIgnore]
    public string Display => $"{(ConnectionMode == "remote" ? "عن بُعد" : "محلي")} — {Username} @ {Host}:{ApiPort}";
}

/// <summary>
/// Independent DPAPI CurrentUser history. No original Python file, license
/// file, trial clock, registry key or customer database is ever changed.
/// The WPF caller must invoke SaveAuthenticated only after a real authorized
/// MikroTik login, never after a probe or failed authentication.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConnectionHistoryStore
{
    public const int MaxConnections = 20;
    private const int MaxProtectedBytes = 131072;
    private readonly string _root;

    public ConnectionHistoryStore(string localAppDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);
        _root = Path.GetFullPath(localAppDataRoot);
    }

    public static ConnectionHistoryStore ForCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new ConnectionHistoryStore(root);
    }

    public string ProtectedPath =>
        Path.Combine(_root, "HadramoutNET", "connections", "history.dpapi");

    public IReadOnlyList<SavedRouterConnection> Load()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        CheckPath();
        try
        {
            using var file = new FileStream(ProtectedPath, FileMode.Open, FileAccess.Read,
                FileShare.Read);
            if (file.Length is <= 0 or > MaxProtectedBytes)
                throw new InvalidDataException("حجم سجل الاتصالات المحمي غير صالح.");
            byte[] encrypted = new byte[(int)file.Length];
            file.ReadExactly(encrypted);
            byte[]? plain = null;
            try
            {
                plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                var entries = JsonSerializer.Deserialize<List<SavedRouterConnection>>(plain)
                    ?? throw new InvalidDataException("تنسيق سجل الاتصالات غير صالح.");
                if (entries.Count > MaxConnections)
                    throw new InvalidDataException("تجاوز سجل الاتصالات حجمه المسموح.");
                return entries.Select(Validate).ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
                if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (FileNotFoundException) { return Array.Empty<SavedRouterConnection>(); }
        catch (DirectoryNotFoundException) { return Array.Empty<SavedRouterConnection>(); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or
            ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "تعذر قراءة سجل الاتصالات المحمي. لن يُستبدل تلقائياً.", ex);
        }
    }

    public void SaveAuthenticated(SavedRouterConnection input, bool rememberPassword)
    {
        ArgumentNullException.ThrowIfNull(input);
        var current = Validate(input with
        {
            Password = rememberPassword ? input.Password : string.Empty
        });
        var entries = Load().ToList(); // A corrupt record must NEVER be overwritten.
        entries.RemoveAll(item => Same(item, current));
        entries.Insert(0, current);
        if (entries.Count > MaxConnections)
            entries.RemoveRange(MaxConnections, entries.Count - MaxConnections);
        Write(entries);
    }

    public void Forget(SavedRouterConnection item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var entries = Load().ToList();
        entries.RemoveAll(value => Same(value, item));
        Write(entries);
    }

    private void Write(IReadOnlyList<SavedRouterConnection> entries)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(entries);
        byte[]? cipher = null;
        try
        {
            cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            if (cipher.Length is <= 0 or > MaxProtectedBytes)
                throw new InvalidDataException("حجم سجل الاتصالات أكبر من الحد المسموح.");
            CheckPath();
            string directory = Path.GetDirectoryName(ProtectedPath)!;
            Directory.CreateDirectory(directory);
            string staged = Path.Combine(directory, ".history-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var file = new FileStream(staged, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    file.Write(cipher);
                    file.Flush(flushToDisk: true);
                }
                CheckPath();
                File.Move(staged, ProtectedPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(staged)) File.Delete(staged);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (cipher is not null) CryptographicOperations.ZeroMemory(cipher);
        }
    }

    private static bool Same(SavedRouterConnection left, SavedRouterConnection right) =>
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.ApiPort == right.ApiPort &&
        left.Username == right.Username &&
        left.ConnectionMode == right.ConnectionMode &&
        left.UseSsl == right.UseSsl;

    private static SavedRouterConnection Validate(SavedRouterConnection item)
    {
        if (item is null || item.Username is null || item.Password is null ||
            item.Username.Length is 0 or > 128 || item.Username != item.Username.Trim() ||
            item.Username.Any(c => c < 32 || c > 126) ||
            item.Password.Length > 2048 ||
            item.ConnectionMode is not ("local" or "remote"))
            throw new InvalidDataException("بيانات الاتصال المحفوظة غير صالحة.");
        try
        {
            RouterEndpoint endpoint = RouterAddress.Normalize(
                item.Host, item.ApiPort, item.ConnectionMode);
            return item with
            {
                Host = endpoint.Host,
                ApiPort = endpoint.Port.ToString(CultureInfo.InvariantCulture)
            };
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("عنوان الاتصال المحفوظ غير صالح.", ex);
        }
    }

    private void CheckPath()
    {
        foreach (string path in new[]
        {
            Path.Combine(_root, "HadramoutNET"),
            Path.Combine(_root, "HadramoutNET", "connections"),
            ProtectedPath
        })
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("المسار المحمي يحوي رابطاً غير آمن.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
