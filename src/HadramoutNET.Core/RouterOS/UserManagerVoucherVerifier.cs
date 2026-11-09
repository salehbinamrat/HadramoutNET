using System.Globalization;

namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// A strictly read-only observation of the CURRENT in-memory voucher draft.
/// Account existence and profile assignment do not prove that a prior
/// upload caused them or that RADIUS authentication currently works.
/// </summary>
public sealed record UserManagerVoucherObservation(string Username, string State);

public sealed record UserManagerVoucherVerificationResult(
    string Status, int Version, IReadOnlyList<UserManagerVoucherObservation> Cards)
{
    public bool Available => Status == "ready";
}

/// <summary>
/// Reconciles an operator-selected in-memory draft with CURRENT User Manager
/// account/profile facts. Does not store usernames, passwords or assignments;
/// does not issue additions, profile activation, retries or removals.
/// </summary>
public sealed class UserManagerVoucherVerifier
{
    private const int MaxRows = 5000;

    private static readonly IReadOnlyDictionary<string, string> CountOnly =
        new Dictionary<string, string> { ["count-only"] = "yes" };
    private static readonly IReadOnlyDictionary<string, string> V7AssignmentFields =
        new Dictionary<string, string> { [".proplist"] = "user,profile,state" };

    public async Task<UserManagerVoucherVerificationResult> VerifyAsync(
        RouterAuthorizedSession session, IReadOnlyList<string> usernames,
        string expectedProfile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(usernames);

        if (!session.HasCurrentAccess)
            return Unavailable("not_authorized");

        if (usernames.Count is < 1 or > MaxRows ||
            string.IsNullOrWhiteSpace(expectedProfile) ||
            expectedProfile.Length > 80 || expectedProfile.Any(char.IsControl))
            throw new ArgumentException("كمية الكروت أو اسم الباقة غير صالح للفحص.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string username in usernames)
        {
            if (string.IsNullOrWhiteSpace(username) || username.Length > 64 ||
                username.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch)) ||
                !names.Add(username))
                throw new ArgumentException("أسماء المسودة مكررة أو غير صالحة.");
        }

        UserManagerCatalogResult catalog;
        try
        {
            catalog = await new UserManagerCatalog().ReadAsync(session, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or RouterOsTrapException
            or InvalidOperationException or OperationCanceledException)
        {
            return Unavailable("unavailable");
        }

        if (!catalog.UsersComplete || catalog.Version is not (6 or 7))
            return Unavailable(catalog.Status == "not_installed" ?
                "not_installed" : "unavailable");

        // A partial projection must not be misreported as absent users.
        if (catalog.Users.Count > MaxRows ||
            catalog.Users.Select(x => x.Username).Distinct(StringComparer.Ordinal).Count() !=
            catalog.Users.Count)
            return Unavailable("limit_exceeded");

        string userPath = catalog.Version == 7 ?
            "/user-manager/user/print" : "/tool/user-manager/user/print";
        try
        {
            int? userCount = await ReadCountAsync(session, userPath,
                cancellationToken).ConfigureAwait(false);
            if (userCount is null)
                return Unavailable("unavailable");
            if (userCount > MaxRows)
                return Unavailable("limit_exceeded");
            if (userCount != catalog.Users.Count)
                return Unavailable("incomplete");

            if (catalog.Version == 6)
            {
                var rows = catalog.Users.ToDictionary(x => x.Username, StringComparer.Ordinal);
                return new UserManagerVoucherVerificationResult("ready", 6,
                    usernames.Select(username => new UserManagerVoucherObservation(username,
                        !rows.TryGetValue(username, out UserManagerUserRow? row) ? "not_found" :
                        row.Disabled ? "disabled" :
                        string.Equals(row.Profile, expectedProfile, StringComparison.Ordinal) ?
                            "profile_reported" :
                        row.Profile.Length != 0 ? "profile_other" : "profile_unverified"
                    )).ToArray());
            }

            // User-profile is a separate v7 table. A user name alone does not
            // prove profile binding, and running / used are not active.
            const string assignmentPath = "/user-manager/user-profile/print";
            int? assignmentCount = await ReadCountAsync(session, assignmentPath,
                cancellationToken).ConfigureAwait(false);
            if (assignmentCount is null)
                return Unavailable("unavailable");
            if (assignmentCount > MaxRows)
                return Unavailable("limit_exceeded");

            var fetched = await session.ReadAsync(assignmentPath,
                V7AssignmentFields, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (fetched.Rows.Count != assignmentCount)
                return Unavailable("incomplete");

            var assignments = new Dictionary<string, List<(string Profile, string State)>>(
                StringComparer.Ordinal);
            foreach (var row in fetched.Rows)
            {
                string user = Field(row, "user");
                string profile = Field(row, "profile");
                if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(profile))
                    return Unavailable("incomplete");
                if (!assignments.TryGetValue(user, out var assigned))
                    assignments.Add(user, assigned = []);
                assigned.Add((profile, Field(row, "state")));
            }

            var users = catalog.Users.ToDictionary(x => x.Username, StringComparer.Ordinal);
            var observations = new List<UserManagerVoucherObservation>(usernames.Count);
            foreach (string username in usernames)
            {
                string state;
                if (!users.TryGetValue(username, out UserManagerUserRow? user))
                    state = "not_found";
                else if (user.Disabled)
                    state = "disabled";
                else if (!assignments.TryGetValue(username, out var assigned) ||
                    assigned.Count == 0)
                    state = "profile_missing";
                else
                {
                    var desired = assigned.Where(x =>
                        string.Equals(x.Profile, expectedProfile, StringComparison.Ordinal)).ToArray();
                    state = desired.Any(x => IsActive(x.State)) ? "profile_active" :
                        desired.Any(x => string.Equals(x.State, "running",
                            StringComparison.OrdinalIgnoreCase)) ? "profile_ready" :
                        desired.Any(x => string.Equals(x.State, "used",
                            StringComparison.OrdinalIgnoreCase)) ? "profile_used" :
                        desired.Length != 0 ? "profile_unverified" : "profile_other";
                }
                observations.Add(new UserManagerVoucherObservation(username, state));
            }
            return new UserManagerVoucherVerificationResult("ready", 7, observations);
        }
        catch (Exception ex) when (ex is IOException or RouterOsTrapException
            or InvalidOperationException or OperationCanceledException)
        {
            return Unavailable("unavailable");
        }
    }

    private static async Task<int?> ReadCountAsync(RouterAuthorizedSession session,
        string path, CancellationToken cancellationToken)
    {
        var result = await session.ReadAsync(path, CountOnly,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Done.TryGetValue("ret", out string? count) ||
            !int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture,
                out int value) || value < 0)
            return null;
        return value;
    }

    private static bool IsActive(string state) =>
        state.Equals("running active", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("running-active", StringComparison.OrdinalIgnoreCase);

    private static string Field(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out string? value) ? value ?? "" : "";

    private static UserManagerVoucherVerificationResult Unavailable(string status) =>
        new(status, 0, []);
}
