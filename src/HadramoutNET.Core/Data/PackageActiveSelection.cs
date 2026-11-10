using System.Text.Json;

namespace HadramoutNET.Core.Data;

/// <summary>
/// Persist only the last selected local package ID. A deleted or malformed
/// record never redirects the editor to a different package by name.
/// </summary>
public static class PackageActiveSelection
{
    private const string Setting = "packages.active_id";

    public static long? Read(PackageSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        JsonElement? value = store.GetSetting(Setting);
        if (value is null || value.Value.ValueKind != JsonValueKind.Number ||
            !value.Value.TryGetInt64(out long id) || id <= 0)
            return null;
        return store.GetPackage(id) is null ? null : id;
    }

    public static void Save(PackageSettingsStore store, long id)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (id <= 0 || store.GetPackage(id) is null)
            throw new ArgumentException("لا يمكن تذكر باقة لم تعد موجودة.", nameof(id));
        store.SetSetting(Setting, JsonSerializer.SerializeToElement(id));
    }
}
