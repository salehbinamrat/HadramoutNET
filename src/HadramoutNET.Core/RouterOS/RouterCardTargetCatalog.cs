using System.Globalization;

namespace HadramoutNET.Core.RouterOS;

public sealed record RouterCardProfile(string Name, string Owner = "");
public sealed record RouterCardTargets(bool Available, string Reason, int UserManagerVersion,
    IReadOnlyList<RouterCardProfile> Profiles, IReadOnlyList<string> Servers);

/// <summary>
/// Complete, bounded and read-only profile/server discovery for voucher creation.
/// No user lists or password fields are read.
/// </summary>
public sealed class RouterCardTargetCatalog
{
    private const int MaximumRows = 3000;
    private static readonly IReadOnlyDictionary<string, string> CountOnly =
        new Dictionary<string, string> { ["count-only"] = "yes" };

    public async Task<RouterCardTargets> ReadAsync(
        RouterAuthorizedSession session, string service,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.HasCurrentAccess)
            throw new InvalidOperationException("انتهت جلسة MikroTik.");
        if (service is not ("hotspot" or "ppp" or "userman"))
            throw new ArgumentException("الخدمة المطلوبة غير معروفة.", nameof(service));
        try
        {
            if (service == "hotspot")
            {
                var profiles = await ReadList(session, "/ip/hotspot/user/profile/print",
                    "name", cancellationToken).ConfigureAwait(false);
                var servers = await ReadList(session, "/ip/hotspot/print",
                    "name", cancellationToken).ConfigureAwait(false);
                return new RouterCardTargets(true, "ready", 0, profiles,
                    servers.Select(x => x.Name).ToArray());
            }
            if (service == "ppp")
            {
                var profiles = await ReadList(session, "/ppp/profile/print",
                    "name", cancellationToken).ConfigureAwait(false);
                return new RouterCardTargets(true, "ready", 0, profiles, []);
            }
            try
            {
                var profiles = await ReadList(session, "/user-manager/profile/print",
                    "name", cancellationToken).ConfigureAwait(false);
                return new RouterCardTargets(true, "ready", 7, profiles, []);
            }
            catch (RouterOsTrapException ex) when (Unsupported(ex.Message))
            {
                try
                {
                    var profiles = await ReadList(session, "/tool/user-manager/profile/print",
                        "name,owner,customer", cancellationToken).ConfigureAwait(false);
                    return new RouterCardTargets(true, "ready", 6, profiles, []);
                }
                catch (RouterOsTrapException inner) when (Unsupported(inner.Message))
                {
                    return new RouterCardTargets(false, "not_installed", 0, [], []);
                }
            }
        }
        catch (RouterOsTrapException)
        {
            return new RouterCardTargets(false, "permission_denied", 0, [], []);
        }
        catch (InvalidDataException)
        {
            return new RouterCardTargets(false, "incomplete", 0, [], []);
        }
    }

    private static async Task<IReadOnlyList<RouterCardProfile>> ReadList(
        RouterAuthorizedSession session, string path, string columns,
        CancellationToken cancellationToken)
    {
        var count = await session.ReadAsync(path, CountOnly,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (count.Rows.Count != 0 ||
            !count.Done.TryGetValue("ret", out string? raw) ||
            !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int expected) ||
            expected is < 0 or > MaximumRows)
            throw new InvalidDataException("عدد الباقات أو السيرفرات غير موثوق.");
        var listed = await session.ReadAsync(path,
            new Dictionary<string, string> { [".proplist"] = columns },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (listed.Rows.Count != expected)
            throw new InvalidDataException("القائمة المقروءة من MikroTik غير مكتملة.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RouterCardProfile>(expected);
        foreach (var row in listed.Rows)
        {
            if (!row.TryGetValue("name", out string? name) ||
                string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
                name.Any(char.IsControl) || !names.Add(name))
                throw new InvalidDataException("أسماء الباقات أو السيرفرات غير صالحة.");
            row.TryGetValue("owner", out string? owner);
            if (string.IsNullOrWhiteSpace(owner))
                row.TryGetValue("customer", out owner);
            result.Add(new RouterCardProfile(name, owner ?? ""));
        }
        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool Unsupported(string message)
    {
        string text = message.ToLowerInvariant();
        return text.Contains("no such command", StringComparison.Ordinal) ||
               text.Contains("bad command name", StringComparison.Ordinal) ||
               text.Contains("unknown command", StringComparison.Ordinal) ||
               text.Contains("not installed", StringComparison.Ordinal);
    }
}
