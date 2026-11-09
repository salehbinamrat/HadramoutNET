namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// Display-only PPP account fields. Passwords, comments, and arbitrary
/// RouterOS attributes are discarded even if a server returns them.
/// </summary>
public sealed record RouterDirectoryAccount(
    string Name, string Group, string Disabled, string Detail);

public sealed record RouterDirectoryResult(
    bool Available, string Version, IReadOnlyList<RouterDirectoryAccount> Accounts);

/// <summary>
/// Read-only PPP account directory. User Manager uses the existing,
/// richer UserManagerCatalog; this class intentionally does not duplicate it.
/// </summary>
public sealed class RouterAccountDirectory
{
    public async Task<RouterDirectoryResult> ReadPppAsync(
        RouterAuthorizedSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.HasCurrentAccess)
            throw new InvalidOperationException("انتهت جلسة MikroTik؛ يجب تسجيل الدخول مجددا.");
        try
        {
            var reply = await session.ReadAsync("/ppp/secret/print",
                new Dictionary<string, string>
                {
                    [".proplist"] = "name,profile,disabled,service"
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
            var rows = reply.Rows.Select(row => new RouterDirectoryAccount(
                Field(row, "name"), Field(row, "profile"),
                Field(row, "disabled"), Field(row, "service"))).ToArray();
            return new RouterDirectoryResult(true, "ppp", rows);
        }
        catch (RouterOsTrapException)
        {
            // Permission denied or unavailable service is never shown as zero.
            return new RouterDirectoryResult(false, "unavailable", []);
        }
    }

    private static string Field(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out string? value) ? value : "";
}
