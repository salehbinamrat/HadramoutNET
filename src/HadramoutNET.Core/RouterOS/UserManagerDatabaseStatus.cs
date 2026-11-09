using System.Text.RegularExpressions;

namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// A safe read-only User Manager database maintenance status. A missing,
/// forbidden or partial response is never represented as zero free space.
/// Version is the actually discovered RouterOS User Manager command family.
/// </summary>
public sealed record UserManagerDatabaseSnapshot(
    string Status, int Version, string? DatabaseSize, string? FreeDiskSpace)
{
    public bool Available => Status == "ready";
}

/// <summary>
/// Parity with the Python NetworkGateway.database_maintenance_status() path
/// while deliberately avoiding raw errors, unknown fields or mutations.
/// /user-manager is tested first, /tool/user-manager is a fallback ONLY for
/// unsupported commands. No database optimize or backup command is issued.
/// </summary>
public sealed class UserManagerDatabaseStatus
{
    private static readonly IReadOnlyDictionary<string, string> CountOnly =
        new Dictionary<string, string> { ["count-only"] = "yes" };

    private static readonly IReadOnlyDictionary<string, string> DatabaseFields =
        new Dictionary<string, string>
        {
            [".proplist"] = "db-size,free-disk-space"
        };

    // RouterOS versions may report raw byte counts or readable unit suffixes.
    // Do not guess units: display only a compact, safe numeric measurement.
    private static readonly Regex Measurement = new(
        @"^[0-9]+(?:[.,][0-9]+)?(?:\s*[A-Za-z]{1,5})?$",
        RegexOptions.CultureInvariant);

    public async Task<UserManagerDatabaseSnapshot> ReadAsync(
        RouterAuthorizedSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.HasCurrentAccess)
            throw new InvalidOperationException(
                "انتهت صلاحية جلسة MikroTik؛ يجب تسجيل الدخول مجددا.");

        int version = 7;
        try
        {
            // count-only avoids loading all profiles merely to locate the
            // User Manager menu, even on routers with many profiles.
            await session.ReadAsync("/user-manager/profile/print", CountOnly,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RouterOsTrapException ex) when (Unsupported(ex.Message))
        {
            version = 6;
            try
            {
                await session.ReadAsync("/tool/user-manager/profile/print", CountOnly,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (RouterOsTrapException second) when (Unsupported(second.Message))
            {
                return Unavailable("not_installed");
            }
            catch (RouterOsTrapException)
            {
                return Unavailable("permission_denied");
            }
        }
        catch (RouterOsTrapException)
        {
            // A permission failure on the v7 path does not authorize
            // guessing that the device must run v6.
            return Unavailable("permission_denied");
        }

        string databasePath = version == 7
            ? "/user-manager/database/print"
            : "/tool/user-manager/database/print";
        try
        {
            var result = await session.ReadAsync(databasePath, DatabaseFields,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Rows.Count == 0)
                return new("empty_response", version, null, null);
            // Ambiguous or duplicate rows are not reliable database status.
            if (result.Rows.Count != 1)
                return new("fields_unavailable", version, null, null);
            var row = result.Rows[0];
            string? size = ReadMeasurement(row, "db-size");
            string? free = ReadMeasurement(row, "free-disk-space");
            if (size is null || free is null)
                return new("fields_unavailable", version, null, null);
            return new("ready", version, size, free);
        }
        catch (RouterOsTrapException)
        {
            // Do not echo the router trap: some API errors may include
            // sensitive information, and denied is not zero.
            return new("database_unavailable", version, null, null);
        }
    }

    private static UserManagerDatabaseSnapshot Unavailable(string reason) =>
        new(reason, 0, null, null);

    private static string? ReadMeasurement(
        IReadOnlyDictionary<string, string> row, string field)
    {
        if (!row.TryGetValue(field, out string? value) || value is null)
            return null;
        string text = value.Trim();
        return text.Length is > 0 and <= 64 && Measurement.IsMatch(text)
            ? text : null;
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
