namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// Read-only User Manager discovery, using the original Python NetworkGateway
/// order: RouterOS 7 /user-manager first, then RouterOS 6 /tool/user-manager
/// only for genuinely unsupported commands. Permission errors never trigger
/// an alternate menu, and no passwords or session secrets are requested.
/// </summary>
public sealed record UserManagerProfileRow(string Name, string Owner);
public sealed record UserManagerUserRow(
    string Username, string Profile, bool Disabled, string Customer);

public sealed record UserManagerCatalogResult(
    string Status, int Version,
    IReadOnlyList<UserManagerProfileRow> Profiles,
    IReadOnlyList<UserManagerUserRow> Users)
{
    public bool UsersComplete => Status == "ready";
}

public sealed class UserManagerCatalog
{
    private static readonly IReadOnlyDictionary<string, string> V7ProfilesFields =
        new Dictionary<string, string> { [".proplist"] = "name" };
    private static readonly IReadOnlyDictionary<string, string> V6ProfilesFields =
        new Dictionary<string, string> { [".proplist"] = "name,owner,customer" };
    private static readonly IReadOnlyDictionary<string, string> V7UsersFields =
        new Dictionary<string, string> { [".proplist"] = "name,disabled,actual-profile" };
    private static readonly IReadOnlyDictionary<string, string> V6UsersFields =
        new Dictionary<string, string>
        {
            [".proplist"] = "username,actual-profile,disabled,customer"
        };

    public async Task<UserManagerCatalogResult> ReadAsync(
        RouterAuthorizedSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.HasCurrentAccess)
            throw new InvalidOperationException("يجب تسجيل الدخول بترخيص صالح قبل قراءة User Manager.");

        int version = 7;
        RouterOsCommandResult profiles;
        try
        {
            profiles = await session.ReadAsync("/user-manager/profile/print",
                V7ProfilesFields, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RouterOsTrapException ex) when (Unsupported(ex.Message))
        {
            version = 6;
            try
            {
                profiles = await session.ReadAsync("/tool/user-manager/profile/print",
                    V6ProfilesFields, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (RouterOsTrapException inner) when (Unsupported(inner.Message))
            {
                return Empty("not_installed");
            }
            catch (RouterOsTrapException)
            {
                return Empty("permission_denied");
            }
        }
        catch (RouterOsTrapException)
        {
            // A v7 permission denial must never be silently downgraded to v6.
            return Empty("permission_denied");
        }

        var normalizedProfiles = profiles.Rows
            .Select(row => new UserManagerProfileRow(
                Field(row, "name"), Field(row, "owner", "customer")))
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .ToArray();

        string path = version == 7 ?
            "/user-manager/user/print" : "/tool/user-manager/user/print";
        var fields = version == 7 ? V7UsersFields : V6UsersFields;
        try
        {
            RouterOsCommandResult users = await session.ReadAsync(
                path, fields, cancellationToken: cancellationToken).ConfigureAwait(false);
            var normalizedUsers = users.Rows
                .Select(row => new UserManagerUserRow(
                    Field(row, "name", "username"),
                    Field(row, "actual-profile", "profile"),
                    IsTrue(Field(row, "disabled")),
                    Field(row, "customer")))
                .Where(row => !string.IsNullOrWhiteSpace(row.Username))
                .ToArray();
            return new UserManagerCatalogResult(
                "ready", version, normalizedProfiles, normalizedUsers);
        }
        catch (RouterOsTrapException)
        {
            return new UserManagerCatalogResult(
                "users_unavailable", version, normalizedProfiles, []);
        }
    }

    private static UserManagerCatalogResult Empty(string reason) =>
        new(reason, 0, [], []);

    private static string Field(
        IReadOnlyDictionary<string, string> row, string name, string? alternative = null)
    {
        if (row.TryGetValue(name, out string? value) && value is not null)
            return value;
        return alternative is not null &&
               row.TryGetValue(alternative, out string? other) ? other ?? "" : "";
    }

    private static bool IsTrue(string text) =>
        text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1";

    private static bool Unsupported(string message)
    {
        string lower = message.ToLowerInvariant();
        return lower.Contains("no such command", StringComparison.Ordinal) ||
               lower.Contains("bad command name", StringComparison.Ordinal) ||
               lower.Contains("unknown command", StringComparison.Ordinal) ||
               lower.Contains("not installed", StringComparison.Ordinal);
    }
}
