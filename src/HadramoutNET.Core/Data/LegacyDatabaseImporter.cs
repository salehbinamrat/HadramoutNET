using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HadramoutNET.Core.Data;

/// <summary>Counts of operational history copied to a dedicated .NET namespace.</summary>
public sealed record LegacyImportReport(
    int Routers, int Packages, int Batches, int Cards,
    int RouterPresets, int Activities, int Settings);

/// <summary>
/// Reads a pinned Python SQLite schema-v8 database as a consistent in-memory
/// snapshot and imports operational data into isolated legacy_* .NET tables.
/// NEVER opens the Python source for writes, copies router credentials, touches
/// DPAPI/trial/paid-licensing state, or overwrites an existing import.
///
/// Caller must explicitly select the source database. The resulting records
/// are not activated as native .NET router connections until service parity
/// and license validation have been independently implemented.
/// </summary>
public sealed class LegacyDatabaseImporter(string targetPath)
{
    private readonly string _targetPath = Path.GetFullPath(
        !string.IsNullOrWhiteSpace(targetPath) ? targetPath :
            throw new ArgumentException("Target .NET database path is required.", nameof(targetPath)));

    private static readonly IReadOnlyDictionary<string, string[]> Columns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["settings"] = ["key", "value"],
            ["routers"] = ["id", "name", "host", "port", "username", "password", "use_ssl",
                "verify_ssl", "connection_mode", "ftp_port", "is_default", "created_at"],
            ["packages"] = ["id", "name", "settings_json", "updated_at"],
            ["batches"] = ["id", "batch_no", "router_id", "server", "profile", "quantity", "price",
                "created_at", "notes", "package_id", "package_name", "print_settings_json", "service"],
            ["cards"] = ["id", "batch_id", "sequence", "username", "password", "price", "profile",
                "server", "validity_days", "limit_uptime", "limit_bytes", "created_at", "status",
                "printed", "print_count", "comment", "sent_at", "last_error", "service", "router_id"],
            ["activity"] = ["id", "ts", "category", "action", "status", "details"],
            ["router_presets"] = ["router_id", "service", "remote_id", "profile_name", "package_id"]
        };

    /// <summary>
    /// Source is read-only and its WAL is included via SQLite backup into
    /// memory; the original database and its saved passwords are never written
    /// or copied into a temporary file on disk.
    /// </summary>
    public LegacyImportReport Import(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string input = Path.GetFullPath(sourcePath);
        if (string.Equals(input, _targetPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("لا يمكن استيراد قاعدة البيانات إلى نفس الملف.");
        if (!File.Exists(input))
            throw new FileNotFoundException("قاعدة بيانات حضرموت غير موجودة.", input);

        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = input, Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 10
        }.ToString());
        source.Open();

        // Snapshot is private and memory-backed: do not create an exposed copy
        // of the source's DPAPI-protected router secrets or historical settings.
        using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:", Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        snapshot.Open();
        source.BackupDatabase(snapshot);
        ValidateSource(snapshot);

        // Only our own, independent .NET database may be mutated.
        _ = new PackageSettingsStore(_targetPath);
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _targetPath, Mode = SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 10
        }.ToString());
        destination.Open();
        using (var pragma = destination.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON";
            pragma.ExecuteNonQuery();
        }

        if (TableExists(destination, "legacy_import_info") ||
            Columns.Keys.Any(table => TableExists(destination, "legacy_" + table)))
            throw new InvalidOperationException("تم استيراد سجل حضرموت مسبقا أو توجد جداول استيراد؛ لن يتم استبدال أي بيانات.");

        using var tx = destination.BeginTransaction();
        CreateSchema(destination, tx);

        int routers = Copy(snapshot, destination, tx, "routers");
        int packages = Copy(snapshot, destination, tx, "packages");
        int settings = Copy(snapshot, destination, tx, "settings");
        int batches = Copy(snapshot, destination, tx, "batches");
        int cards = Copy(snapshot, destination, tx, "cards");
        int presets = Copy(snapshot, destination, tx, "router_presets");
        int activities = Copy(snapshot, destination, tx, "activity");

        using (var command = destination.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "INSERT INTO legacy_import_info(source_schema_version, imported_at_utc) VALUES ('8',$time)";
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        using (var integrity = destination.CreateCommand())
        {
            integrity.Transaction = tx;
            integrity.CommandText = "PRAGMA foreign_key_check";
            using var reader = integrity.ExecuteReader();
            if (reader.Read())
                throw new InvalidOperationException("فشل تحقق علاقات بيانات الاستيراد؛ لم تحفظ التغييرات.");
        }
        tx.Commit();
        return new LegacyImportReport(routers, packages, batches, cards, presets, activities, settings);
    }

    private static bool TableExists(SqliteConnection db, string name)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name)";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static void ValidateSource(SqliteConnection snapshot)
    {
        using (var check = snapshot.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check";
            object? result = check.ExecuteScalar();
            if (!string.Equals(result as string, "ok", StringComparison.Ordinal))
                throw new InvalidOperationException("قاعدة حضرموت لا تجتاز فحص سلامة SQLite.");
        }
        if (!TableExists(snapshot, "meta"))
            throw new InvalidOperationException("قاعدة البيانات ليست نسخة حضرموت بمخطط v8.");
        using (var version = snapshot.CreateCommand())
        {
            version.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
            string? value = Convert.ToString(version.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (value != "8")
                throw new InvalidOperationException("الاستيراد يدعم المخطط v8 فقط؛ لم تتغير أي بيانات.");
        }

        foreach (var table in Columns)
        {
            if (!TableExists(snapshot, table.Key))
                throw new InvalidOperationException("جدول مفقود من مخطط v8: " + table.Key);
            using var command = snapshot.CreateCommand();
            command.CommandText = "PRAGMA table_info(" + table.Key + ")";
            using var reader = command.ExecuteReader();
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
                available.Add(reader.GetString(1));
            foreach (string col in table.Value)
            {
                // Schema v8 can retain the old globally unique cards table for
                // single-router installations. router_id is then inferred later
                // from each owning batch, without modifying the source.
                if (table.Key == "cards" && col == "router_id") continue;
                if (!available.Contains(col))
                    throw new InvalidOperationException($"العمود {table.Key}.{col} مفقود من مخطط v8.");
            }
        }

        using var fk = snapshot.CreateCommand();
        fk.CommandText = "PRAGMA foreign_key_check";
        using var fkReader = fk.ExecuteReader();
        if (fkReader.Read())
            throw new InvalidOperationException("قاعدة حضرموت تحتوي علاقات مفقودة؛ تم رفض الاستيراد.");
    }

    private static void CreateSchema(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE legacy_import_info(
                source_schema_version TEXT NOT NULL,
                imported_at_utc TEXT NOT NULL);
            CREATE TABLE legacy_settings(
                key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE legacy_routers(
                id INTEGER PRIMARY KEY,name TEXT,host TEXT,port INTEGER,username TEXT,
                password TEXT NOT NULL DEFAULT '',use_ssl INTEGER,verify_ssl INTEGER,
                connection_mode TEXT,ftp_port INTEGER,is_default INTEGER,created_at TEXT);
            CREATE TABLE legacy_packages(
                id INTEGER PRIMARY KEY,name TEXT,settings_json TEXT,updated_at TEXT);
            CREATE TABLE legacy_batches(
                id INTEGER PRIMARY KEY,batch_no TEXT,router_id INTEGER,server TEXT,profile TEXT,
                quantity INTEGER,price REAL,created_at TEXT,notes TEXT,package_id INTEGER,
                package_name TEXT,print_settings_json TEXT,service TEXT,
                FOREIGN KEY(router_id) REFERENCES legacy_routers(id) ON DELETE SET NULL,
                FOREIGN KEY(package_id) REFERENCES legacy_packages(id) ON DELETE SET NULL);
            CREATE TABLE legacy_cards(
                id INTEGER PRIMARY KEY,batch_id INTEGER,sequence TEXT,username TEXT,password TEXT,
                price REAL,profile TEXT,server TEXT,validity_days TEXT,limit_uptime TEXT,
                limit_bytes INTEGER,created_at TEXT,status TEXT,printed INTEGER,print_count INTEGER,
                comment TEXT,sent_at TEXT,last_error TEXT,service TEXT,router_id INTEGER,
                FOREIGN KEY(batch_id) REFERENCES legacy_batches(id) ON DELETE CASCADE);
            CREATE INDEX legacy_cards_batch ON legacy_cards(batch_id);
            CREATE INDEX legacy_cards_username_router ON legacy_cards(username,router_id);
            CREATE TABLE legacy_activity(
                id INTEGER PRIMARY KEY,ts TEXT,category TEXT,action TEXT,status TEXT,details TEXT);
            CREATE TABLE legacy_router_presets(
                router_id INTEGER,service TEXT,remote_id TEXT,profile_name TEXT,package_id INTEGER,
                PRIMARY KEY(router_id,service,remote_id),
                FOREIGN KEY(router_id) REFERENCES legacy_routers(id),
                FOREIGN KEY(package_id) REFERENCES legacy_packages(id));
            """;
        command.ExecuteNonQuery();
    }

    private static int Copy(SqliteConnection source, SqliteConnection dest, SqliteTransaction tx, string table)
    {
        string[] columns = Columns[table];
        // Legacy single-router installations may not contain cards.router_id.
        bool hasRouterId = false;
        if (table == "cards")
        {
            using var pragma = source.CreateCommand();
            pragma.CommandText = "PRAGMA table_info(cards)";
            using var schemaReader = pragma.ExecuteReader();
            while (schemaReader.Read())
                if (schemaReader.GetString(1) == "router_id") hasRouterId = true;
        }
        string selectColumns = string.Join(",", columns.Select(c =>
            table == "cards" && c == "router_id" && !hasRouterId ? "NULL AS router_id" : c));
        using var read = source.CreateCommand();
        read.CommandText = "SELECT " + selectColumns + " FROM " + table;
        using var reader = read.ExecuteReader();
        using var write = dest.CreateCommand();
        write.Transaction = tx;
        string parameters = string.Join(",", Enumerable.Range(0, columns.Length).Select(i => "$p" + i));
        write.CommandText = $"INSERT INTO legacy_{table} ({string.Join(",", columns)}) VALUES({parameters})";
        for (int i = 0; i < columns.Length; i++)
            write.Parameters.Add(new SqliteParameter("$p" + i, DBNull.Value));

        int count = 0;
        while (reader.Read())
        {
            if (table == "settings" && !SafeSetting(reader.GetString(0)))
                continue;
            for (int i = 0; i < columns.Length; i++)
            {
                object value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                string col = columns[i];

                if (table == "routers" && col == "password")
                    value = string.Empty; // DPAPI or plaintext router passwords never migrate.
                else if (table == "activity" && col == "details")
                    value = DBNull.Value; // Historical log details may embed secrets.
                else if ((table == "packages" && col == "settings_json") ||
                         (table == "batches" && col == "print_settings_json"))
                {
                    if (value is string json)
                        value = SanitizePresetJson(json);
                }
                else if (table == "settings" && col == "value" && value is string settingJson)
                {
                    using var doc = JsonDocument.Parse(settingJson);
                    value = doc.RootElement.GetRawText();
                }
                write.Parameters[i].Value = value;
            }
            write.ExecuteNonQuery();
            count++;
        }
        return count;
    }

    private static bool SafeSetting(string key)
    {
        if (!(key.StartsWith("card.", StringComparison.Ordinal) ||
              key.StartsWith("print.", StringComparison.Ordinal) ||
              key.StartsWith("create.", StringComparison.Ordinal)))
            return false;
        string lowered = key.ToLowerInvariant();
        if (new[] { "secret", "license", "activation", "token", "fingerprint",
            "api_key", "private", "credential" }.Any(lowered.Contains))
            return false;
        if (lowered.StartsWith("create.password", StringComparison.Ordinal) &&
            lowered is not ("create.password_chars" or "create.password_length"))
            return false;
        return true;
    }

    private static string SanitizePresetJson(string raw)
    {
        using var json = JsonDocument.Parse(raw);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("إعدادات باقة حضرموت ليست كائن JSON صالح.");
        var filtered = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in json.RootElement.EnumerateObject())
            if (SafeSetting(property.Name))
                filtered[property.Name] = property.Value.Clone();
        return JsonSerializer.Serialize(filtered);
    }
}
