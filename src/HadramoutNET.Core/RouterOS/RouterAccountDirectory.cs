namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// Display-only RouterOS account fields. Credentials, comments and arbitrary
/// RouterOS attributes are deliberately excluded even if a server returns them.
/// </summary>
public sealed record RouterDirectoryAccount(
    string Name, string Group, string Disabled, string Detail);

public sealed record RouterDirectoryResult(
    bool Available, string Version, IReadOnlyList<RouterDirectoryAccount> Accounts);

/// <summary>
/// Read-only PPP and User Manager account directories behind an authorized live
/// session. Not a management API: no passwords, mutations or automatic retries.
/// </summary>
public sealed class RouterAccountDirectory
{
    public Task<RouterDirectoryResult> ReadPppAsync(
        RouterAuthorizedSession session, CancellationToken cancellationToken = default) =>
        ReadPppInternalAsync(session, cancellationToken);

    public async Task<RouterDirectoryResult> ReadUserManagerAsync(
        RouterAuthorizedSession session, CancellationToken cancellationToken = default)
    {
        RequireAccess(session);
        try
        {
            var v6 = await session.ReadAsync("/tool/user-manager/user/print",
                new Dictionary<string, string>
                {
                    [".proplist"] = "username,disabled,customer"
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new RouterDirectoryResult(true, "v6",
                Project(v6, "username", "customer", ""));
        }
        catch (RouterOsTrapException error) when (Unsupported(error.Message))
        {
            // RouterOS 7 changed the User Manager API path. Only an
            // unsupported command is evidence to try the alternative.
            try
            {
                var v7 = await session.ReadAsync("/user-manager/user/print",
                    new Dictionary<string, string>
                    {
                        [".proplist"] = "name,disabled,group"
                    }, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new RouterDirectoryResult(true, "v7",
                    Project(v7, "name", "group", ""));
            }
            catch (RouterOsTrapException)
            {
                return Unavailable();
            }
        }
        catch (RouterOsTrapException)
        {
            // Permission failure is NOT evidence of an alternative API version.
            return Unavailable();
        }
    }

    private static async Task<RouterDirectoryResult> ReadPppInternalAsync(
        RouterAuthorizedSession session, CancellationToken cancellationToken)
    {
        RequireAccess(session);
        try
        {
            var reply = await session.ReadAsync("/ppp/secret/print",
                new Dictionary<string, string>
                {
                    [".proplist"] = "name,profile,disabled,service"
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new RouterDirectoryResult(true, "ppp",
                Project(reply, "name", "profile", "service"));
        }
        catch (RouterOsTrapException)
        {
            return Unavailable();
        }
    }

    private static IReadOnlyList<RouterDirectoryAccount> Project(
        RouterOsCommandResult response, string name, string group, string detail)
    {
        return response.Rows.Select(row => new RouterDirectoryAccount(
            Field(row, name), Field(row, group), Field(row, "disabled"),
            detail.Length > 0 ? Field(row, detail) : "")).ToArray();
    }

    private static string Field(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out string? value) ? value : "";

    private static void RequireAccess(RouterAuthorizedSession? session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.HasCurrentAccess)
            throw new InvalidOperationException("انتهت جلسة MikroTik؛ يجب تسجيل الدخول مجددا.");
    }

    private static RouterDirectoryResult Unavailable() =>
        new(false, "unavailable", []);

    private static bool Unsupported(string message)
    {
        string lower = message.ToLowerInvariant();
        return lower.Contains("no such command", StringComparison.Ordinal) ||
            lower.Contains("bad command name", StringComparison.Ordinal) ||
            lower.Contains("unknown command", StringComparison.Ordinal);
    }
}
