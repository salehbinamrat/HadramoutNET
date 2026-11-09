namespace HadramoutNET.Core.RouterOS;

/// <summary>No password or session is persisted by this read-only test.</summary>
public sealed record RouterProbeRequest(
    string Host, string ApiPort, string Username, string Password = "",
    bool UseSsl = false, bool VerifySsl = false,
    string ConnectionMode = "local",
    int ConnectTimeoutSeconds = 8, int CommandTimeoutSeconds = 45);

public sealed record RouterProbeResult(
    bool Success, string Status, string Message,
    string Endpoint = "", string Identity = "");

/// <summary>
/// Authenticates against RouterOS v6/v7 and reads identity exactly as in
/// Python core/session.py. A successful probe never starts a trial, saves a
/// password or grants access to application screens or mutating commands.
/// </summary>
public sealed class RouterConnectionProbe
{
    public async Task<RouterProbeResult> TestAsync(
        RouterProbeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        RouterEndpoint address;
        string username;
        try
        {
            address = RouterAddress.Normalize(
                request.Host, request.ApiPort, request.ConnectionMode);
            username = request.Username?.Trim() ?? "";
            if (username.Length == 0 || username.Any(char.IsControl))
                throw new ArgumentException("اسم مستخدم الراوتر غير صالح.");
            if (request.ConnectTimeoutSeconds <= 0 || request.CommandTimeoutSeconds <= 0)
                throw new ArgumentException("مهلة اتصال الراوتر غير صالحة.");
        }
        catch (ArgumentException)
        {
            return new RouterProbeResult(false, "invalid_settings",
                "تحقق من عنوان الراوتر ومنفذ API واسم المستخدم ونوع الاتصال.");
        }

        string endpoint = RouterAddress.EndpointText(address.Host, address.Port);
        bool authenticated = false;
        try
        {
            await using var api = new RouterOsApiClient(new RouterOsConnectionOptions(
                address.Host, address.Port, username, request.Password ?? "",
                request.UseSsl, request.VerifySsl,
                RemoteMode: request.ConnectionMode == "remote",
                ConnectTimeoutSeconds: request.ConnectTimeoutSeconds,
                CommandTimeoutSeconds: request.CommandTimeoutSeconds));

            await api.ConnectAsync(cancellationToken).ConfigureAwait(false);
            authenticated = true;
            // The only command sent after login is this allowlisted read.
            var identity = await api.ExecuteAsync("/system/identity/print",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            string name = identity.Rows.Count > 0 &&
                identity.Rows[0].TryGetValue("name", out string? title)
                ? title.Trim() : "";
            if (string.IsNullOrEmpty(name))
                name = address.Host; // Exact fallback from Python authenticate_router.
            return new RouterProbeResult(true, "authenticated_read_only",
                "نجح اختبار MikroTik وهوية الراوتر؛ لم يتم فتح جلسة إدارة.",
                endpoint, name);
        }
        catch (RouterOsTrapException)
        {
            // RouterOS trap messages could echo passwords; do not display them.
            return new RouterProbeResult(false,
                authenticated ? "identity_unavailable" : "authentication_rejected",
                authenticated ? "تم الاتصال لكن تعذرت قراءة هوية الراوتر." :
                "رفض MikroTik اسم المستخدم أو كلمة المرور.", endpoint);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RouterProbeResult(false, "cancelled",
                "أُلغي اختبار الاتصال دون حفظ جلسة.", endpoint);
        }
        catch (Exception ex) when (
            ex is IOException or System.Net.Sockets.SocketException
                or System.Security.Authentication.AuthenticationException
                or OperationCanceledException or UnauthorizedAccessException)
        {
            return new RouterProbeResult(false, "connection_failed",
                "تعذر إتمام اختبار API؛ راجع العنوان والمنفذ وإعدادات SSL.", endpoint);
        }
    }
}
