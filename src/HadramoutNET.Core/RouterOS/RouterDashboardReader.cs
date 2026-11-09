using System.Globalization;

namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// Counts and resource statistics are read from the authenticated RouterOS
/// session. Null means unavailable/forbidden, and is NEVER disguised as zero.
/// </summary>
public sealed record RouterDashboardSnapshot(
    string RouterName,
    string RouterOsVersion,
    string Uptime,
    int? CpuPercent,
    int? MemoryPercent,
    int? HotspotUsers,
    int? HotspotActive,
    int? PppActive,
    int? UserManagerUsers,
    IReadOnlyList<string> UnavailableServices);

public sealed class RouterDashboardReader
{
    public async Task<RouterDashboardSnapshot> ReadAsync(
        RouterAuthorizedSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.HasCurrentAccess)
            throw new InvalidOperationException("انتهت جلسة الراوتر أو صلاحية البرنامج.");

        var errors = new List<string>();
        var resource = await ResourceAsync(session, cancellationToken).ConfigureAwait(false);
        if (resource is null) errors.Add("System");

        int? hotspot = await CountAsync(session, "/ip/hotspot/user/print",
            "Hotspot", errors, cancellationToken).ConfigureAwait(false);
        int? active = await CountAsync(session, "/ip/hotspot/active/print",
            "Hotspot active", errors, cancellationToken).ConfigureAwait(false);
        int? ppp = await CountAsync(session, "/ppp/active/print",
            "PPP", errors, cancellationToken).ConfigureAwait(false);
        int? um = await CountAsync(session, "/tool/user-manager/user/print",
            "User Manager", errors, cancellationToken).ConfigureAwait(false);

        int? cpu = resource is not null && resource.TryGetValue("cpu-load", out string? load)
            ? ToPercent(load) : null;
        int? memory = null;
        if (resource is not null &&
            resource.TryGetValue("total-memory", out string? totalText) &&
            resource.TryGetValue("free-memory", out string? freeText) &&
            long.TryParse(totalText, NumberStyles.None, CultureInfo.InvariantCulture, out long total) &&
            long.TryParse(freeText, NumberStyles.None, CultureInfo.InvariantCulture, out long free) &&
            total > 0 && free >= 0 && free <= total)
        {
            memory = (int)Math.Round((total - free) * 100d / total,
                MidpointRounding.AwayFromZero);
        }

        return new RouterDashboardSnapshot(
            session.Identity,
            resource is not null && resource.TryGetValue("version", out string? version) ?
                version : "",
            resource is not null && resource.TryGetValue("uptime", out string? uptime) ?
                uptime : "",
            cpu, memory, hotspot, active, ppp, um, errors);
    }

    private static async Task<IReadOnlyDictionary<string, string>?> ResourceAsync(
        RouterAuthorizedSession session, CancellationToken ct)
    {
        try
        {
            var result = await session.ReadAsync("/system/resource/print",
                cancellationToken: ct).ConfigureAwait(false);
            return result.Rows.Count > 0 ? result.Rows[0] : null;
        }
        catch (RouterOsTrapException) { return null; }
    }

    private static async Task<int?> CountAsync(
        RouterAuthorizedSession session,
        string command, string label, List<string> unavailable,
        CancellationToken ct)
    {
        try
        {
            var result = await session.ReadAsync(command,
                new Dictionary<string, string> { ["count-only"] = "yes" },
                cancellationToken: ct).ConfigureAwait(false);
            if (result.Done.TryGetValue("ret", out string? countText) &&
                int.TryParse(countText, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int count) && count >= 0)
                return count;
            unavailable.Add(label);
            return null;
        }
        catch (RouterOsTrapException)
        {
            unavailable.Add(label);
            return null;
        }
    }

    private static int? ToPercent(string raw) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int pct) &&
        pct is >= 0 and <= 100 ? pct : null;
}
