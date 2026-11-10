using System.Text.Json;
using HadramoutNET.Core.Data;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class PackageActiveSelectionTests
{
    [Fact]
    public void Last_selected_card_package_survives_restart_without_overwriting_other_packages()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hnet-active-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PackageSettingsStore(Path.Combine(directory, "hadramout-net.db"));
            long first = store.SaveOrUpdatePackage("باقة المكلا", new Dictionary<string, JsonElement>
            {
                ["create.quantity"] = JsonSerializer.SerializeToElement(50)
            });
            long second = store.SaveOrUpdatePackage("باقة سيئون", new Dictionary<string, JsonElement>
            {
                ["create.quantity"] = JsonSerializer.SerializeToElement(200)
            });
            PackageActiveSelection.Save(store, first);
            var reopened = new PackageSettingsStore(Path.Combine(directory, "hadramout-net.db"));
            Assert.Equal(first, PackageActiveSelection.Read(reopened));
            PackageActiveSelection.Save(reopened, second);
            Assert.Equal(second, PackageActiveSelection.Read(new PackageSettingsStore(
                Path.Combine(directory, "hadramout-net.db"))));
            Assert.Equal(50, reopened.GetPackage(first)!.Settings["create.quantity"].GetInt32());
            Assert.Equal(200, reopened.GetPackage(second)!.Settings["create.quantity"].GetInt32());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Missing_or_deleted_package_id_does_not_restore_an_unrelated_package()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hnet-active-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PackageSettingsStore(Path.Combine(directory, "hadramout-net.db"));
            Assert.Null(PackageActiveSelection.Read(store));
            long id = store.SaveOrUpdatePackage("اختبار", new Dictionary<string, JsonElement>());
            PackageActiveSelection.Save(store, id);
            store.DeletePackage(id);
            Assert.Null(PackageActiveSelection.Read(store));
            Assert.Throws<ArgumentException>(() => PackageActiveSelection.Save(store, id));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
