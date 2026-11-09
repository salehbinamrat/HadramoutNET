using System.Globalization;

namespace HadramoutNET.Core.RouterOS;

public sealed record UserManagerLifecycleReadResult(
    string Status, UserManagerLifecycleSnapshot? Snapshot, bool Cached = false)
{
    public bool Ready => Status == "ready" && Snapshot is not null;
}

/// <summary>
/// Read-only, count-verified User Manager inventory. One users table and
/// one assignment table per explicit v7 scan; one user table in v6.
/// A short session-bound cache keeps consecutive active/expired screens
/// from downloading the same large account inventory twice.
/// </summary>
public sealed class UserManagerLifecycleReader
{
    public const int MaxUsers = 40_000;
    public const int MaxAssignments = 80_000;
    private static readonly TimeSpan CacheLife = TimeSpan.FromSeconds(90);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RouterAuthorizedSession? _cachedSession;
    private DateTimeOffset _cachedAt;
    private UserManagerLifecycleSnapshot? _cachedSnapshot;

    private const string V7Users =
        ".id,name,disabled,transfer-limit,total-download-used,total-upload-used,download-used,upload-used";
    private const string V6Users =
        ".id,username,name,disabled,incomplete,actual-profile,money-used,uptime-used,total-download-used,total-upload-used,download-used,upload-used,transfer-limit,last-used-ip,last-used-mac,last-seen,active,active-sessions";

    public async Task<UserManagerLifecycleReadResult> ReadAsync(
        RouterAuthorizedSession session,
        CancellationToken cancellationToken = default, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.HasCurrentAccess)
            return new UserManagerLifecycleReadResult("not_authorized", null);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && ReferenceEquals(_cachedSession, session) &&
                _cachedSnapshot is not null &&
                DateTimeOffset.UtcNow - _cachedAt < CacheLife &&
                session.HasCurrentAccess)
                return new UserManagerLifecycleReadResult(
                    "ready", _cachedSnapshot, Cached: true);

            int version;
            try
            {
                await session.ReadAsync("/user-manager/profile/print",
                    new Dictionary<string, string> { [".proplist"] = "name" },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                version = 7;
            }
            catch (RouterOsTrapException ex) when (Unsupported(ex.Message))
            {
                try
                {
                    await session.ReadAsync("/tool/user-manager/profile/print",
                        new Dictionary<string, string> { [".proplist"] = "name" },
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    version = 6;
                }
                catch (RouterOsTrapException)
                {
                    return new UserManagerLifecycleReadResult("unavailable", null);
                }
            }

            string root = version == 7 ? "/user-manager" : "/tool/user-manager";
            string userProjection = version == 7 ? V7Users : V6Users;
            var users = await ReadVerifiedRows(session, root + "/user/print",
                userProjection, MaxUsers, cancellationToken).ConfigureAwait(false);
            if (users is null)
                return new UserManagerLifecycleReadResult("incomplete", null);

            IReadOnlyList<IReadOnlyDictionary<string, string>> assignments = [];
            if (version == 7)
            {
                var mapped = await ReadVerifiedRows(session,
                    "/user-manager/user-profile/print", "user,state",
                    MaxAssignments, cancellationToken).ConfigureAwait(false);
                if (mapped is null)
                    return new UserManagerLifecycleReadResult("incomplete", null);
                assignments = mapped;
            }
            else if (users.Count > 0 && !users.Any(row =>
                     row.ContainsKey("actual-profile") ||
                     row.ContainsKey("money-used") ||
                     row.ContainsKey("uptime-used") ||
                     row.ContainsKey("transfer-limit") ||
                     row.ContainsKey("total-download-used") ||
                     row.ContainsKey("download-used")))
            {
                // ROS6 can silently omit dynamic fields from a projected
                // response. Never show "0 active/expired" for such data.
                return new UserManagerLifecycleReadResult("incomplete", null);
            }

            UserManagerLifecycleSnapshot snapshot =
                UserManagerLifecycleClassifier.Classify(version, users, assignments);
            _cachedSession = session;
            _cachedSnapshot = snapshot;
            _cachedAt = DateTimeOffset.UtcNow;
            return new UserManagerLifecycleReadResult("ready", snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is RouterOsTrapException or IOException or
            InvalidOperationException or OperationCanceledException or ArgumentException)
        {
            // No incomplete or malformed inventory is classified as zero.
            return new UserManagerLifecycleReadResult("unavailable", null);
        }
        finally { _gate.Release(); }
    }

    public void Invalidate()
    {
        _cachedSnapshot = null;
        _cachedSession = null;
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>?>
        ReadVerifiedRows(RouterAuthorizedSession session, string menu,
            string projection, int maximum, CancellationToken cancellationToken)
    {
        var count = await session.ReadAsync(menu,
            new Dictionary<string, string> { ["count-only"] = "yes" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (count.Rows.Count != 0 ||
            !count.Done.TryGetValue("ret", out string? text) ||
            !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture,
                out int expected) || expected < 0 || expected > maximum)
            return null;

        var rows = await session.ReadAsync(menu,
            new Dictionary<string, string> { [".proplist"] = projection },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (rows.Rows.Count != expected) return null;

        string[] fields = projection.Split(',');
        var sanitized = new List<IReadOnlyDictionary<string, string>>(expected);
        foreach (var row in rows.Rows)
        {
            // Preserve only explicitly requested fields. Unknown server-side
            // fields, including password if a firmware ignores .proplist,
            // cannot escape this read boundary.
            var allowed = new Dictionary<string, string>(
                StringComparer.Ordinal);
            foreach (string key in fields)
                if (row.TryGetValue(key, out string? value) && value is not null)
                    allowed[key] = value;
            sanitized.Add(allowed);
        }
        return sanitized;
    }

    private static bool Unsupported(string message)
    {
        string lower = message.ToLowerInvariant();
        return lower.Contains("no such command", StringComparison.Ordinal) ||
            lower.Contains("bad command name", StringComparison.Ordinal) ||
            lower.Contains("unknown command", StringComparison.Ordinal) ||
            lower.Contains("not installed", StringComparison.Ordinal);
    }
}
