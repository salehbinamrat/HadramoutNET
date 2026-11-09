using System.Text.Json;
using HadramoutNET.Core.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class PackageSettingsStoreTests
{
    private static string NewPath()
    {
        var folder = Path.Combine(Path.GetTempPath(), "HadramoutNET-package-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "hadramout-net.db");
    }

    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Default_path_is_separate_from_original_python_program()
    {
        var root = Path.Combine(Path.GetTempPath(), "isolated-root");
        var path = PackageSettingsStore.DefaultPath(root);
        Assert.Equal(Path.Combine(root, "HadramoutNET", "data", "hadramout-net.db"), path);
        Assert.DoesNotContain("NidiMikroTikCards", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Arabic_json_settings_survive_reopening_without_changing_types()
    {
        var path = NewPath();
        new PackageSettingsStore(path).SetSettings(new Dictionary<string, JsonElement>
        {
            ["create.profile"] = J("باقة تجريبية"),
            ["create.quantity"] = J(60),
            ["card.add_qr"] = J(true),
            ["card.positions"] = J(new Dictionary<string, int[]> { ["username"] = [76, 13] })
        });
        var restarted = new PackageSettingsStore(path);
        Assert.Equal("باقة تجريبية", restarted.GetSetting("create.profile")!.Value.GetString());
        Assert.Equal(60, restarted.GetSetting("create.quantity")!.Value.GetInt32());
        Assert.True(restarted.GetSetting("card.add_qr")!.Value.GetBoolean());
        Assert.Equal(76, restarted.GetSetting("card.positions")!.Value.GetProperty("username")[0].GetInt32());
    }

    [Fact]
    public void Each_package_preserves_its_own_design_creation_and_print_values()
    {
        var path = NewPath();
        var first = new PackageSettingsStore(path);
        long one = first.SavePackage(" باقة المكلا ", new Dictionary<string, JsonElement>
        {
            ["card.font_size"] = J(14), ["create.quantity"] = J(60),
            ["print.rows"] = J(15), ["license.code"] = J("must-not-be-saved")
        });
        long two = first.SavePackage("باقة سيئون", new Dictionary<string, JsonElement>
        {
            ["card.font_size"] = J(11), ["create.quantity"] = J(100), ["print.rows"] = J(20)
        });
        var reopened = new PackageSettingsStore(path);
        var p1 = Assert.IsType<PackagePreset>(reopened.GetPackage(one));
        var p2 = Assert.IsType<PackagePreset>(reopened.GetPackage(two));
        Assert.Equal("باقة المكلا", p1.Name);
        Assert.Equal(60, p1.Settings["create.quantity"].GetInt32());
        Assert.Equal(100, p2.Settings["create.quantity"].GetInt32());
        Assert.Equal(15, p1.Settings["print.rows"].GetInt32());
        Assert.False(p1.Settings.ContainsKey("license.code"));
        Assert.Equal(2, reopened.ListPackages().Count);
    }

    [Fact]
    public void Duplicate_names_ignore_ascii_case_and_do_not_change_original_package()
    {
        var store = new PackageSettingsStore(NewPath());
        var id = store.SavePackage("Starter", new Dictionary<string, JsonElement> { ["create.quantity"] = J(20) });
        Assert.Throws<ArgumentException>(() => store.SavePackage("starter", new Dictionary<string, JsonElement>
        {
            ["create.quantity"] = J(50)
        }));
        Assert.Equal(20, store.GetPackage(id)!.Settings["create.quantity"].GetInt32());
    }

    [Fact]
    public void Updating_a_package_keeps_id_and_other_packages_unchanged()
    {
        var store = new PackageSettingsStore(NewPath());
        var one = store.SavePackage("first", new Dictionary<string, JsonElement> { ["create.quantity"] = J(20) });
        var two = store.SavePackage("second", new Dictionary<string, JsonElement> { ["create.quantity"] = J(40) });
        Assert.Equal(one, store.SavePackage("renamed", new Dictionary<string, JsonElement> { ["create.quantity"] = J(50) }, one));
        Assert.Equal(50, store.GetPackage(one)!.Settings["create.quantity"].GetInt32());
        Assert.Equal(40, store.GetPackage(two)!.Settings["create.quantity"].GetInt32());
        Assert.Throws<ArgumentException>(() => store.SavePackage("none", new Dictionary<string, JsonElement>(), 999999));
    }

    [Fact]
    public void Switching_package_replaces_only_design_create_and_print_keys()
    {
        var store = new PackageSettingsStore(NewPath());
        store.SetSettings(new Dictionary<string, JsonElement>
        {
            ["ui.theme"] = J("red"), ["create.quantity"] = J(60), ["card.font_size"] = J(14),
            ["print.rows"] = J(15)
        });
        store.ReplacePresetSettings(new Dictionary<string, JsonElement>
        {
            ["create.quantity"] = J(100), ["print.rows"] = J(20), ["license.code"] = J("ignored")
        });
        Assert.Equal("red", store.GetSetting("ui.theme")!.Value.GetString());
        Assert.Equal(100, store.GetSetting("create.quantity")!.Value.GetInt32());
        Assert.Null(store.GetSetting("card.font_size"));
        Assert.Null(store.GetSetting("license.code"));
    }

    [Fact]
    public void Existing_python_sqlite_schema_is_rejected_without_mutation()
    {
        var path = NewPath();
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO meta VALUES('schema_version','8');";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new PackageSettingsStore(path));
        using var check = new SqliteConnection($"Data Source={path}");
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='app_metadata'";
        Assert.Equal(0L, (long)query.ExecuteScalar()!);
    }

    [Fact]
    public void Future_storage_version_is_rejected_before_any_updates()
    {
        var path = NewPath();
        new PackageSettingsStore(path).SetSetting("ui.theme", J("red"));
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE app_metadata SET value='999' WHERE key='storage_version'";
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        Assert.Throws<InvalidOperationException>(() => new PackageSettingsStore(path));
        using var check = new SqliteConnection($"Data Source={path}");
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT value FROM settings WHERE key='ui.theme'";
        Assert.Equal("\"red\"", (string)query.ExecuteScalar()!);
    }
}
