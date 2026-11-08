using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HadramoutNET.Core.Data;

/// <summary>
/// A separate .NET package-settings store. This is NOT the original Python
/// SQLite v8 schema and must never open/mutate a Python application database.
/// </summary>
public sealed record PackagePreset(
    long Id, string Name, IReadOnlyDictionary<string, JsonElement> Settings, string UpdatedAt);

public sealed class PackageSettingsStore
{
    private const int StorageVersion = 1;
    private readonly string _path;
    private static readonly string[] PresetPrefixes = ["card.", "create.", "print."];

    public static string DefaultPath(string localAppDataRoot) =>
        Path.Combine(Path.GetFullPath(localAppDataRoot), "HadramoutNET", "data", "hadramout-net.db");

    public PackageSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        if (string.Equals(Path.GetFileName(_path), "nidi_cards.db", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("رفض فتح قاعدة حضرموت Python الأصلية للكتابة.");

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var connection = OpenConnection();
        if (TableExists(connection, "meta") || TableExists(connection, "routers") ||
            TableExists(connection, "cards") || TableExists(connection, "batches"))
            throw new InvalidOperationException("قاعدة Python أو قاعدة أخرى غير معروفة؛ يجب إبقاء النسختين مستقلتين.");

        if (TableExists(connection, "app_metadata"))
        {
            using var versionCmd = connection.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM app_metadata WHERE key='storage_version'";
            var versionText = versionCmd.ExecuteScalar() as string;
            if (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out int version) ||
                version != StorageVersion)
                throw new InvalidOperationException("إصدار قاعدة HadramoutNET غير مدعوم؛ لم تُغير البيانات.");
        }

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS app_metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS packages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                settings_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            INSERT OR IGNORE INTO app_metadata(key, value) VALUES ('storage_version','1');
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name)";
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public JsonElement? GetSetting(string key)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        var raw = command.ExecuteScalar() as string;
        if (raw is null) return null;
        using var parsed = JsonDocument.Parse(raw);
        return parsed.RootElement.Clone();
    }

    public void SetSetting(string key, JsonElement value) =>
        SetSettings(new Dictionary<string, JsonElement> { [key] = value });

    public void SetSettings(IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        WriteSettings(connection, transaction, values);
        transaction.Commit();
    }

    private static void WriteSettings(
        SqliteConnection connection, SqliteTransaction tx,
        IReadOnlyDictionary<string, JsonElement> settings)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO settings(key,value) VALUES($key,$value)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            WHERE settings.value IS NOT excluded.value
            """;
        var key = command.CreateParameter();
        key.ParameterName = "$key";
        command.Parameters.Add(key);
        var value = command.CreateParameter();
        value.ParameterName = "$value";
        command.Parameters.Add(value);
        foreach (var item in settings)
        {
            if (string.IsNullOrWhiteSpace(item.Key))
                throw new ArgumentException("مفتاح الإعداد غير صالح.", nameof(settings));
            key.Value = item.Key;
            value.Value = item.Value.GetRawText();
            command.ExecuteNonQuery();
        }
    }

    private static bool IsPresetKey(string key) =>
        PresetPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));

    private static Dictionary<string, JsonElement> CopyPreset(IReadOnlyDictionary<string, JsonElement> settings) =>
        settings.Where(item => IsPresetKey(item.Key))
                .ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal);

    public void ReplacePresetSettings(IReadOnlyDictionary<string, JsonElement> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var presets = CopyPreset(settings);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM settings WHERE substr(key,1,5)='card.'
                OR substr(key,1,7)='create.' OR substr(key,1,6)='print.'
                """;
            delete.ExecuteNonQuery();
        }
        WriteSettings(connection, transaction, presets);
        transaction.Commit();
    }

    public long SavePackage(
        string name, IReadOnlyDictionary<string, JsonElement> settings, long? packageId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 80)
            throw new ArgumentException("اسم الباقة مطلوب، وبحد أقصى 80 حرفا.", nameof(name));
        var payload = JsonSerializer.Serialize(CopyPreset(settings));
        var timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$time", timestamp);
            long result;
            if (packageId is null)
            {
                command.CommandText = """
                    INSERT INTO packages(name,settings_json,updated_at)
                    VALUES($name,$payload,$time);
                    SELECT last_insert_rowid();
                    """;
                result = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            else
            {
                command.CommandText = """
                    UPDATE packages SET name=$name,settings_json=$payload,updated_at=$time
                    WHERE id=$id
                    """;
                command.Parameters.AddWithValue("$id", packageId.Value);
                if (command.ExecuteNonQuery() != 1)
                    throw new ArgumentException("الباقة لم تعد موجودة.", nameof(packageId));
                result = packageId.Value;
            }
            transaction.Commit();
            return result;
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new ArgumentException("توجد باقة بهذا الاسم؛ اختر اسما آخر.", nameof(name), error);
        }
    }

    public PackagePreset? GetPackage(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,settings_json,updated_at FROM packages WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPackage(reader) : null;
    }

    public IReadOnlyList<PackagePreset> ListPackages()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,settings_json,updated_at FROM packages ORDER BY name COLLATE NOCASE,id";
        using var reader = command.ExecuteReader();
        var results = new List<PackagePreset>();
        while (reader.Read()) results.Add(ReadPackage(reader));
        return results;
    }

    public void DeletePackage(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM packages WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static PackagePreset ReadPackage(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1),
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reader.GetString(2)) ??
                new Dictionary<string, JsonElement>(),
            reader.GetString(3));
}
