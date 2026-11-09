using System.Globalization;
using System.Text.RegularExpressions;

namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// Read-only, filtered operational snapshots. Empty rows with Available=false
/// mean denied, unsupported or unsafe to list; never present as zero users.
/// </summary>
public sealed record NetworkInventoryResult<T>(
    bool Available, string Reason, IReadOnlyList<T> Items);

public sealed record NetworkHotspotServer(
    string Id, string Name, string Interface, string Profile,
    bool Disabled, bool Invalid, bool Running);

public sealed record NetworkNeighbor(
    string Interface, string Address, string Mac,
    string Identity, string Platform, string Version, string Board);

public sealed record NetworkActiveSession(
    string Id, string Service, string Username, string Address);

/// <summary>
/// Read-only network maintenance discovery; deliberately no disconnect,
/// reboot, disable or scan APIs. Authenticated RouterOS session is required
/// before every command and unknown response fields are always discarded.
/// </summary>
public sealed class RouterNetworkInventory
{
    private const int NeighborLimit = 5000;
    private const int SessionLimitPerService = 2500;

    private static readonly Regex RouterRecordId = new(
        @"^\*[0-9A-Fa-f]+$", RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string> ServerFields =
        new Dictionary<string, string>
        {
            [".proplist"] = ".id,name,interface,profile,disabled,invalid,running"
        };
    private static readonly IReadOnlyDictionary<string, string> NeighborFields =
        new Dictionary<string, string>
        {
            [".proplist"] = "interface,address,address4,mac-address,identity,platform,version,board"
        };
    private static readonly IReadOnlyDictionary<string, string> SessionFields =
        new Dictionary<string, string>
        {
            [".proplist"] = ".id,user,address,name,caller-id,session-id,mac-address,server"
        };
    private static readonly IReadOnlyDictionary<string, string> CountOnly =
        new Dictionary<string, string> { ["count-only"] = "yes" };

    public async Task<NetworkInventoryResult<NetworkHotspotServer>> ReadServersAsync(
        RouterAuthorizedSession session, CancellationToken cancellationToken = default)
    {
        RequireAuthorized(session);
        try
        {
            var reply = await session.ReadAsync("/ip/hotspot/print",
                ServerFields, cancellationToken: cancellationToken).ConfigureAwait(false);

            var servers = reply.Rows
                .Where(row => RouterRecordId.IsMatch(Field(row, ".id", 64)))
                .Select(row => new NetworkHotspotServer(
                    Field(row, ".id", 64),
                    Field(row, "name", 120), Field(row, "interface", 120),
                    Field(row, "profile", 120),
                    Truth(Field(row, "disabled", 10)),
                    Truth(Field(row, "invalid", 10)),
                    Truth(Field(row, "running", 10))))
                .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Interface, StringComparer.Ordinal)
                .ToArray();
            return new NetworkInventoryResult<NetworkHotspotServer>(
                true, "ready", servers);
        }
        catch (RouterOsTrapException)
        {
            return Unavailable<NetworkHotspotServer>();
        }
    }

    public async Task<NetworkInventoryResult<NetworkNeighbor>> ReadNeighborsAsync(
        RouterAuthorizedSession session, CancellationToken cancellationToken = default)
    {
        RequireAuthorized(session);
        try
        {
            int? count = await CountAsync(session, "/ip/neighbor/print",
                cancellationToken).ConfigureAwait(false);
            if (count is null)
                return Unavailable<NetworkNeighbor>();
            if (count.Value > NeighborLimit)
                return new NetworkInventoryResult<NetworkNeighbor>(false, "limit_exceeded", []);

            // Uses the router's discovery table; NEVER scans IP address space.
            var reply = await session.ReadAsync("/ip/neighbor/print",
                NeighborFields, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (reply.Rows.Count > NeighborLimit)
                return new NetworkInventoryResult<NetworkNeighbor>(false, "limit_exceeded", []);

            var neighbors = reply.Rows.Select(row => new NetworkNeighbor(
                Field(row, "interface"),
                FirstNonEmpty(Field(row, "address4"), Field(row, "address")),
                Field(row, "mac-address"), Field(row, "identity"),
                Field(row, "platform"), Field(row, "version"),
                Field(row, "board"))).ToArray();
            return new NetworkInventoryResult<NetworkNeighbor>(true, "ready", neighbors);
        }
        catch (RouterOsTrapException)
        {
            return Unavailable<NetworkNeighbor>();
        }
    }

    public async Task<NetworkInventoryResult<NetworkActiveSession>> ReadSessionsAsync(
        RouterAuthorizedSession session, CancellationToken cancellationToken = default)
    {
        RequireAuthorized(session);
        var paths = new[] {
            (Name: "Hotspot", Path: "/ip/hotspot/active/print"),
            (Name: "PPPoE", Path: "/ppp/active/print")
        };

        try
        {
            // Ensure both count-only checks pass before requesting ANY
            // potentially large table. Never treat a permission failure as 0.
            foreach (var item in paths)
            {
                int? count = await CountAsync(session, item.Path,
                    cancellationToken).ConfigureAwait(false);
                if (count is null)
                    return Unavailable<NetworkActiveSession>();
                if (count.Value > SessionLimitPerService)
                    return new NetworkInventoryResult<NetworkActiveSession>(
                        false, "limit_exceeded", []);
            }

            var output = new List<NetworkActiveSession>();
            foreach (var item in paths)
            {
                var response = await session.ReadAsync(item.Path,
                    SessionFields, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (response.Rows.Count > SessionLimitPerService)
                    return new NetworkInventoryResult<NetworkActiveSession>(
                        false, "limit_exceeded", []);
                foreach (var row in response.Rows)
                {
                    string id = Field(row, ".id", 64);
                    if (!RouterRecordId.IsMatch(id)) continue;
                    output.Add(new NetworkActiveSession(
                        id, item.Name,
                        FirstNonEmpty(Field(row, "user", 90), Field(row, "name", 90)),
                        Field(row, "address", 80)));
                }
            }
            return new NetworkInventoryResult<NetworkActiveSession>(
                true, "ready", output.ToArray());
        }
        catch (RouterOsTrapException)
        {
            // Discard all partial results if either service is denied.
            return Unavailable<NetworkActiveSession>();
        }
    }

    private static async Task<int?> CountAsync(
        RouterAuthorizedSession session, string command, CancellationToken cancellationToken)
    {
        var result = await session.ReadAsync(command, CountOnly,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Done.TryGetValue("ret", out string? countText) ||
            !int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
            count < 0)
            return null;
        return count;
    }

    private static string Field(
        IReadOnlyDictionary<string, string> row, string key, int maxLength = 120)
    {
        if (!row.TryGetValue(key, out string? value) || value is null)
            return "";
        return value.Length > maxLength ? value[..maxLength] : value;
    }

    private static bool Truth(string value) =>
        value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(string first, string second) =>
        !string.IsNullOrEmpty(first) ? first : second;

    private static NetworkInventoryResult<T> Unavailable<T>() =>
        new(false, "unavailable", []);

    private static void RequireAuthorized(RouterAuthorizedSession? session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.HasCurrentAccess)
            throw new InvalidOperationException("انتهت صلاحية جلسة MikroTik؛ يجب تسجيل الدخول مجددا.");
    }
}
