using System.Security.Cryptography;
using System.Text.Json;
using HadramoutNET.Core.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class LegacyDatabaseImporterTests
{
    private static string NewFolder()
    {
        string path = Path.Combine(Path.GetTempPath(), "hnet-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Read(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        object? result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static void Fixture(string path, int version = 8, bool orphan = false)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false }.ToString());
        db.Open();
        Exec(db, """
            CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE routers(id INTEGER PRIMARY KEY,name TEXT,host TEXT,port INTEGER,username TEXT,password TEXT,use_ssl INTEGER,verify_ssl INTEGER,connection_mode TEXT,ftp_port INTEGER,is_default INTEGER,created_at TEXT);
            CREATE TABLE packages(id INTEGER PRIMARY KEY,name TEXT,settings_json TEXT,updated_at TEXT);
            CREATE TABLE batches(id INTEGER PRIMARY KEY,batch_no TEXT,router_id INTEGER,server TEXT,profile TEXT,quantity INTEGER,price REAL,created_at TEXT,notes TEXT,package_id INTEGER,package_name TEXT,print_settings_json TEXT,service TEXT,FOREIGN KEY(router_id) REFERENCES routers(id));
            CREATE TABLE cards(id INTEGER PRIMARY KEY,batch_id INTEGER,sequence TEXT,username TEXT,password TEXT,price REAL,profile TEXT,server TEXT,validity_days TEXT,limit_uptime TEXT,limit_bytes INTEGER,created_at TEXT,status TEXT,printed INTEGER,print_count INTEGER,comment TEXT,sent_at TEXT,last_error TEXT,service TEXT,router_id INTEGER,FOREIGN KEY(batch_id) REFERENCES batches(id));
            CREATE TABLE activity(id INTEGER PRIMARY KEY,ts TEXT,category TEXT,action TEXT,status TEXT,details TEXT);
            CREATE TABLE router_presets(router_id INTEGER,service TEXT,remote_id TEXT,profile_name TEXT,package_id INTEGER,PRIMARY KEY(router_id,service,remote_id),FOREIGN KEY(router_id) REFERENCES routers(id),FOREIGN KEY(package_id) REFERENCES packages(id));
            """);
        Exec(db, $$"""
            INSERT INTO meta VALUES('schema_version','{{version}}');
            INSERT INTO settings VALUES('create.quantity','60'),('license.code','"secret-license"'),('card.positions','{"username":[76,13]}');
            INSERT INTO routers VALUES(2,'المكلا','192.168.88.1',8728,'admin','dpapi:v1:PRIVATE',0,0,'local',21,1,'2026-11-08');
            INSERT INTO packages VALUES(9,'باقة المكلا','{"card.font_size":14,"print.rows":15,"license.code":"PRIVATE"}','2026-11-08');
            INSERT INTO batches VALUES(5,'BT-5',2,'all','day',1,100,'2026-11-08','note',9,'باقة المكلا','{"print.rows":15,"license.code":"PRIVATE"}','hotspot');
            INSERT INTO cards VALUES(11,5,'0001','00123','=password',100,'day','all','7','1h',1048576,'2026-11-08','CREATED',1,2,'memo',NULL,NULL,'hotspot',2);
            INSERT INTO activity VALUES(3,'2026-11-08','CARDS','created','SUCCESS','sensitive=PRIVATE');
            INSERT INTO router_presets VALUES(2,'hotspot','*1','day',9);
            """);
        if (orphan)
        {
            Exec(db, "PRAGMA foreign_keys=OFF");
            Exec(db, "INSERT INTO cards VALUES(12,999,'0002','orphan','p',0,NULL,NULL,NULL,NULL,0,'2026-11-08','CREATED',0,0,NULL,NULL,NULL,'hotspot',2)");
        }
    }

    [Fact]
    public void Imports_history_and_relations_but_no_router_password_or_license_state()
    {
        string root = NewFolder();
        try
        {
            string source = Path.Combine(root, "nidi_cards.db");
            string target = Path.Combine(root, "net", "hadramout-net.db");
            Fixture(source);
            byte[] before = SHA256.HashData(File.ReadAllBytes(source));
            var store = new PackageSettingsStore(target);
            LegacyImportReport report = new LegacyDatabaseImporter(target).Import(source);
            Assert.Equal((1, 1, 1, 1, 1, 1, 2),
                (report.Routers, report.Packages, report.Batches, report.Cards, report.RouterPresets, report.Activities, report.Settings));
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
            Assert.Equal("00123", Read(target, "SELECT username FROM legacy_cards WHERE id=11"));
            Assert.Equal("=password", Read(target, "SELECT password FROM legacy_cards WHERE id=11"));
            Assert.Equal("0001", Read(target, "SELECT sequence FROM legacy_cards WHERE id=11"));
            Assert.Equal(2L, Read(target, "SELECT print_count FROM legacy_cards WHERE id=11"));
            Assert.Equal(9L, Read(target, "SELECT package_id FROM legacy_router_presets WHERE remote_id='*1'"));
            Assert.Equal("", Read(target, "SELECT password FROM legacy_routers WHERE id=2"));
            Assert.Null(Read(target, "SELECT details FROM legacy_activity WHERE id=3"));
            Assert.Equal(0L, Read(target, "SELECT COUNT(*) FROM legacy_settings WHERE key LIKE 'license.%'"));
            Assert.DoesNotContain("PRIVATE", (string)Read(target, "SELECT settings_json FROM legacy_packages WHERE id=9")!);
            Assert.DoesNotContain("PRIVATE", (string)Read(target, "SELECT print_settings_json FROM legacy_batches WHERE id=5")!);
            Assert.Null(store.GetSetting("license.code"));
            Assert.Equal("8", Read(target, "SELECT source_schema_version FROM legacy_import_info"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(9, false)]
    [InlineData(8, true)]
    public void Fail_closed_on_unsupported_schema_or_orphan_without_partial_import(int version, bool orphan)
    {
        string root = NewFolder();
        try
        {
            string source = Path.Combine(root, "nidi_cards.db");
            string target = Path.Combine(root, "hadramout-net.db");
            Fixture(source, version, orphan);
            new PackageSettingsStore(target);
            Assert.ThrowsAny<Exception>(() => new LegacyDatabaseImporter(target).Import(source));
            Assert.Equal(0L, Read(target, "SELECT COUNT(*) FROM sqlite_master WHERE name='legacy_import_info'"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Existing_import_cannot_be_reset_by_reimport()
    {
        string root = NewFolder();
        try
        {
            string source = Path.Combine(root, "nidi_cards.db");
            string target = Path.Combine(root, "hadramout-net.db");
            Fixture(source);
            var store = new PackageSettingsStore(target);
            store.SetSetting("ui.theme", JsonSerializer.SerializeToElement("glossy_red"));
            var importer = new LegacyDatabaseImporter(target);
            importer.Import(source);
            Assert.Throws<InvalidOperationException>(() => importer.Import(source));
            Assert.Equal(1L, Read(target, "SELECT COUNT(*) FROM legacy_cards"));
            Assert.Equal("glossy_red", store.GetSetting("ui.theme")!.Value.GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WAL_snapshot_includes_uncheckpointed_rows_without_changing_source()
    {
        string root = NewFolder();
        try
        {
            string source = Path.Combine(root, "nidi_cards.db");
            string target = Path.Combine(root, "hadramout-net.db");
            Fixture(source);
            using var live = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = source, Pooling = false }.ToString());
            live.Open();
            Exec(live, "PRAGMA journal_mode=WAL");
            Exec(live, "INSERT INTO cards VALUES(12,5,'0002','wal-user','p',0,NULL,NULL,NULL,NULL,0,'2026-11-08','CREATED',0,0,NULL,NULL,NULL,'hotspot',2)");
            new PackageSettingsStore(target);
            Assert.Equal(2, new LegacyDatabaseImporter(target).Import(source).Cards);
            Assert.Equal("wal-user", Read(target, "SELECT username FROM legacy_cards WHERE id=12"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Older_single_router_v8_cards_without_router_id_are_imported_by_batch()
    {
        string root = NewFolder();
        try
        {
            string source = Path.Combine(root, "nidi_cards.db");
            string target = Path.Combine(root, "hadramout-net.db");
            Fixture(source);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = source, Pooling = false }.ToString()))
            {
                db.Open();
                Exec(db, "ALTER TABLE cards DROP COLUMN router_id");
            }
            new PackageSettingsStore(target);
            Assert.Equal(1, new LegacyDatabaseImporter(target).Import(source).Cards);
            Assert.Null(Read(target, "SELECT router_id FROM legacy_cards WHERE id=11"));
            Assert.Equal(2L, Read(target, """
                SELECT b.router_id FROM legacy_cards c
                JOIN legacy_batches b ON b.id=c.batch_id WHERE c.id=11
                """));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Refuses_source_equal_to_target_without_modifying_source()
    {
        string root = NewFolder();
        try
        {
            string source = Path.Combine(root, "nidi_cards.db");
            Fixture(source);
            byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
            Assert.Throws<InvalidOperationException>(
                () => new LegacyDatabaseImporter(source).Import(source));
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
            Assert.Equal("8", Read(source, "SELECT value FROM meta WHERE key='schema_version'"));
        }
        finally { Directory.Delete(root, true); }
    }

}
