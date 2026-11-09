using HadramoutNET.Core.Licensing;

namespace HadramoutNET.Core.RouterOS;

public sealed record RouterLoginRequest(
    string Host, string ApiPort, string Username, string Password,
    bool TrialOptIn = false,
    bool UseSsl = false, bool VerifySsl = false,
    string ConnectionMode = "local",
    int ConnectTimeoutSeconds = 8, int CommandTimeoutSeconds = 45);

public sealed record RouterLoginResult(
    bool Success, string Reason, string Message, RouterAuthorizedSession? Session = null);

/// <summary>
/// A live authenticated RouterOS API session. It never trusts TCP alone:
/// /login AND /system/identity/print must complete. Current paid/trial
/// authorization is checked before every subsequent read.
/// </summary>
public sealed class RouterAuthorizedSession : IAsyncDisposable
{
    private readonly RouterOsApiClient _api;
    private readonly Func<bool> _canUse;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _closed;

    internal RouterAuthorizedSession(
        RouterOsApiClient api, string identity, string endpoint, Func<bool> canUse)
    {
        _api = api;
        Identity = identity;
        Endpoint = endpoint;
        _canUse = canUse;
    }

    public string Identity { get; }
    public string Endpoint { get; }
    public bool IsOpen => !_closed && _api.Connected;
    public bool HasCurrentAccess => IsOpen && _canUse();

    public async Task<RouterOsCommandResult> ReadAsync(
        string command, IReadOnlyDictionary<string, string>? attributes = null,
        IEnumerable<string>? queries = null, CancellationToken cancellationToken = default)
    {
        if (!command.EndsWith("/print", StringComparison.Ordinal))
            throw new InvalidOperationException("جلسة القراءة لا تسمح بتغيير إعدادات الراوتر.");
        RouterOsCommandGuard.Validate(command, attributes, queries);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!HasCurrentAccess)
                throw new InvalidOperationException("انتهت صلاحية الجلسة أو الترخيص، سجّل الدخول مجددا.");
            return await _api.ExecuteAsync(command, attributes, queries,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed) return;
            _closed = true;
            await _api.DisposeAsync().ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }
}

/// <summary>
/// Real UI login coordinator, unlike RouterConnectionProbe: requires paid
/// signed online verification or an opted-in valid 480h free trial BEFORE
/// connecting. An original/new trial record is started or advanced ONLY AFTER
/// the identity read and it is NEVER rewritten in the original Python store.
/// </summary>
public sealed class RouterLoginCoordinator : IAsyncDisposable
{
    private readonly Func<LicenseAccess> _paidStatus;
    private readonly Func<TrialAccess> _trialStatus;
    private readonly Func<TrialAccess> _confirmTrial;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RouterAuthorizedSession? _active;
    private bool _disposed;

    public RouterLoginCoordinator(
        PaidLicenseCoordinator paid, TrialStateManager trial,
        Func<TimeSpan> processUptime)
        : this(() => paid.StatusAt(processUptime()),
            () => trial.Preview(DateTimeOffset.UtcNow),
            () => trial.ConfirmAfterAuthenticatedRouterLogin(DateTimeOffset.UtcNow))
    {
    }

    internal static RouterLoginCoordinator CreateForTests(
        Func<LicenseAccess> paid, Func<TrialAccess> trial,
        Func<TrialAccess> afterAuthenticated) =>
        new(paid, trial, afterAuthenticated);

    private RouterLoginCoordinator(
        Func<LicenseAccess> paid, Func<TrialAccess> trial,
        Func<TrialAccess> afterAuthenticated)
    {
        _paidStatus = paid ?? throw new ArgumentNullException(nameof(paid));
        _trialStatus = trial ?? throw new ArgumentNullException(nameof(trial));
        _confirmTrial = afterAuthenticated ??
            throw new ArgumentNullException(nameof(afterAuthenticated));
    }

    public RouterAuthorizedSession? ActiveSession => _active?.IsOpen == true ? _active : null;

    public async Task<RouterLoginResult> LoginAsync(
        RouterLoginRequest request, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        RouterOsApiClient? api = null;
        try
        {
            if (_active?.IsOpen == true)
                return Denied("already_connected", "سجّل الخروج قبل الاتصال براوتر آخر.");

            LicenseAccess paid = _paidStatus();
            TrialAccess trial = _trialStatus();
            bool paidPermitted = paid.Permitted;
            bool trialPermitted = trial.Permitted;
            if (!paidPermitted && !trialPermitted)
                return Denied(trial.Reason, trial.Message);
            if (!paidPermitted && !request.TrialOptIn)
                return Denied("trial_opt_in_required",
                    "اختر بدء التجربة المجانية قبل تسجيل الدخول، أو فعّل رمزا مدفوعا.");

            RouterEndpoint endpoint;
            string name;
            try
            {
                endpoint = RouterAddress.Normalize(request.Host, request.ApiPort,
                    request.ConnectionMode);
                name = request.Username?.Trim() ?? "";
                if (name.Length == 0 || name.Any(char.IsControl))
                    throw new ArgumentException("اسم مستخدم الراوتر غير صالح.");
                if (request.ConnectTimeoutSeconds <= 0 || request.CommandTimeoutSeconds <= 0)
                    throw new ArgumentException("مهلة الاتصال غير صالحة.");
            }
            catch (ArgumentException)
            {
                return Denied("invalid_settings",
                    "راجع عنوان MikroTik ومنفذ API واسم المستخدم ونوع الاتصال.");
            }

            api = new RouterOsApiClient(new RouterOsConnectionOptions(
                endpoint.Host, endpoint.Port, name, request.Password ?? "",
                request.UseSsl, request.VerifySsl, request.ConnectionMode == "remote",
                request.ConnectTimeoutSeconds, request.CommandTimeoutSeconds));

            await api.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var identity = await api.ExecuteAsync("/system/identity/print",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            string routerIdentity = identity.Rows.Count != 0 &&
                identity.Rows[0].TryGetValue("name", out string? value)
                ? value.Trim() : "";
            if (string.IsNullOrEmpty(routerIdentity))
                routerIdentity = endpoint.Host;

            // A successful TCP connection or even /login alone is not enough.
            // The original Python program records first login even for paid
            // users; the protected 480-hour trial start is never reserved.
            TrialAccess confirmed = _confirmTrial();
            if (!paidPermitted && !confirmed.Permitted)
                return Denied(confirmed.Reason, confirmed.Message);

            // Check again immediately after I/O in case a paid signature
            // expired or was revoked while RouterOS was authenticating.
            bool allowTrial = request.TrialOptIn && confirmed.Permitted;
            if (!_paidStatus().Permitted && !allowTrial)
                return Denied("authorization_expired",
                    "انتهت صلاحية الترخيص قبل اكتمال تسجيل الدخول.");

            RouterOsApiClient transferred = api;
            api = null;
            _active = new RouterAuthorizedSession(
                transferred, routerIdentity,
                RouterAddress.EndpointText(endpoint.Host, endpoint.Port),
                () => _paidStatus().Permitted ||
                      (allowTrial && _trialStatus().Permitted));
            return new RouterLoginResult(true, "authenticated",
                "تم تسجيل الدخول إلى MikroTik بنجاح.", _active);
        }
        catch (RouterOsTrapException)
        {
            // Raw RouterOS trap texts may echo secrets; never display.
            return Denied("router_auth_failed", "رفض الراوتر بيانات الدخول أو صلاحية قراءة الهوية.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Denied("cancelled", "أُلغي تسجيل الدخول دون فتح جلسة.");
        }
        catch (Exception ex) when (ex is IOException or
            System.Security.Authentication.AuthenticationException
            or OperationCanceledException or InvalidOperationException)
        {
            return Denied("router_connection_failed",
                "تعذر إنشاء جلسة MikroTik مصادق عليها؛ تحقق من API وإعدادات الشبكة.");
        }
        finally
        {
            if (api is not null) await api.DisposeAsync().ConfigureAwait(false);
            _gate.Release();
        }
    }

    public async Task LogoutAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RouterAuthorizedSession? old = _active;
            _active = null;
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await LogoutAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private static RouterLoginResult Denied(string reason, string message) =>
        new(false, reason, message);
}
