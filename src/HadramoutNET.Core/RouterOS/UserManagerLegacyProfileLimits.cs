namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// Pure, linear-time RouterOS 6 User Manager effective Transfer Limit map.
/// Based on the pinned Python profile/limitation/profile-limitation join:
/// choose the minimum positive transfer limit among linked limitations.
/// Never use Total Time Left, uptime or independent upload/download limits
/// as proof that a voucher is expired.
/// </summary>
public static class UserManagerLegacyProfileLimits
{
    public static IReadOnlyDictionary<string, long> Build(
        IReadOnlyList<IReadOnlyDictionary<string, string>> profiles,
        IReadOnlyList<IReadOnlyDictionary<string, string>> limitations,
        IReadOnlyList<IReadOnlyDictionary<string, string>> links)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(limitations);
        ArgumentNullException.ThrowIfNull(links);

        var aliasToProfile = new Dictionary<string, string>(StringComparer.Ordinal);
        var profileLimits = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var profile in profiles)
        {
            string name = Get(profile, "name").Trim();
            string id = Get(profile, ".id").Trim();
            string canonical = name.Length != 0 ? name : id;
            if (canonical.Length == 0) continue;
            profileLimits.TryAdd(canonical, 0);
            AddAlias(aliasToProfile, canonical, canonical);
            if (id.Length > 0) AddAlias(aliasToProfile, id, canonical);
        }

        var limitsByAlias = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var limitation in limitations)
        {
            string id = Get(limitation, ".id").Trim();
            string name = Get(limitation, "name").Trim();
            long bytes = UserManagerLifecycleClassifier.ParseBytes(
                Get(limitation, "transfer-limit"));
            if (id.Length != 0) AddAlias(limitsByAlias, id, bytes);
            if (name.Length != 0) AddAlias(limitsByAlias, name, bytes);
        }

        foreach (var link in links)
        {
            if (!aliasToProfile.TryGetValue(Get(link, "profile"),
                    out string? profile) ||
                !limitsByAlias.TryGetValue(Get(link, "limitation"), out long bytes) ||
                bytes <= 0)
                continue;

            long current = profileLimits[profile];
            if (current == 0 || bytes < current)
                profileLimits[profile] = bytes;
        }

        return aliasToProfile.ToDictionary(
            item => item.Key, item => profileLimits[item.Value],
            StringComparer.Ordinal);
    }

    private static void AddAlias<T>(Dictionary<string, T> aliases,
        string key, T value)
    {
        if (!aliases.TryAdd(key, value) &&
            !EqualityComparer<T>.Default.Equals(aliases[key], value))
            throw new InvalidOperationException(
                "Conflicting User Manager profile or limitation reference.");
    }

    private static string Get(IReadOnlyDictionary<string, string> row,
        string field) => row.TryGetValue(field, out string? value) ? value ?? "" : "";
}
