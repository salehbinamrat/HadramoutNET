using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RouterAuthorizedLoginTests
{
    private const string Host = "127.0.0.1";
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 11, 0, 0, TimeSpan.Zero);
    private static LicenseAccess Active() => new("active", "صالح", "online_valid",
        "0123456789ABCDEF0123456789ABCDEF");

    private static TrialAccess Ready() => new("ready", "not_started", "جاهزة", 480L * 3600L);
    private static TrialAccess Started() => new("trial", "trial_valid", "سارية",
        480L * 3600L, Now, Now.AddHours(480));
    private static TrialAccess Expired() => new("locked", "trial_expired", "منتهية", 0);

    private static RouterLoginCoordinator Login(
        Func<LicenseAccess> paid, Func<TrialAccess> trial, Func<TrialAccess> commit) =>
        RouterLoginCoordinator.CreateForTests(paid, trial, commit);

    private static RouterLoginRequest Request(int port, bool free = false) =>
        new(Host, port.ToString(), "tester", "private-password", TrialOptIn: free);

    private static async Task Reply(NetworkStream stream, params string[] words) =>
        await stream.WriteAsync(RouterOsSentenceCodec.Encode(words));

    [Fact]
    public async Task No_signed_paid_license_or_trial_opt_in_cannot_reach_any_router()
    {
        int recorded = 0;
        var login = Login(() => new("locked", "فحص", "not_checked"),
            Ready, () => { recorded++; return Started(); });
        var result = await login.LoginAsync(Request(1), Now);
        Assert.False(result.Success);
        Assert.Equal("trial_opt_in_required", result.Reason);
        Assert.Equal(0, recorded);
        Assert.Null(result.Session);
        await login.DisposeAsync();
    }

    [Fact]
    public async Task Trial_opt_in_only_records_start_after_successful_login_and_identity()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            Assert.Contains("/login", await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token));
            await Reply(stream, "!done");
            Assert.Equal("/system/identity/print", Assert.Single(
                await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token)));
            await Reply(stream, "!re", "=name=Hadramout-Router");
            await Reply(stream, "!done");
            Assert.Equal("/system/resource/print", Assert.Single(
                await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token)));
            await Reply(stream, "!re", "=cpu-load=29", "=free-memory=65536");
            await Reply(stream, "!done");
        }, timeout.Token);

        int committed = 0;
        TrialAccess trial = Ready();
        var login = Login(() => new("locked", "لا يوجد ترخيص", "not_found"),
            () => trial,
            () => { committed++; trial = Started(); return trial; });
        var result = await login.LoginAsync(Request(port, free: true), Now, timeout.Token);
        Assert.True(result.Success);
        Assert.Equal("authenticated", result.Reason);
        Assert.Equal(1, committed);
        Assert.Equal("Hadramout-Router", result.Session!.Identity);
        Assert.Equal("29", Assert.Single((await result.Session.ReadAsync(
            "/system/resource/print", cancellationToken: timeout.Token)).Rows)["cpu-load"]);
        await result.Session.DisposeAsync();
        Assert.False(result.Session.IsOpen);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            result.Session.ReadAsync("/system/resource/print", cancellationToken: timeout.Token));
        await server;
        await login.DisposeAsync();
    }

    [Fact]
    public async Task Router_login_rejection_never_starts_trial_or_creates_session()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await Reply(stream, "!trap", "=message=wrong private-password");
            await Reply(stream, "!done");
        }, timeout.Token);

        int commits = 0;
        var login = Login(() => new("locked", "لا يوجد", "missing_code"), Ready,
            () => { commits++; return Started(); });
        var result = await login.LoginAsync(Request(port, free: true), Now, timeout.Token);
        Assert.False(result.Success);
        Assert.Equal("router_auth_failed", result.Reason);
        Assert.DoesNotContain("private-password", result.Message);
        Assert.Equal(0, commits);
        Assert.Null(result.Session);
        await server;
        await login.DisposeAsync();
    }

    [Fact]
    public async Task A_paid_entitlement_can_log_in_after_trial_expiry_but_still_records_no_reset()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await Reply(stream, "!re", "=name=PaidRouter");
            await Reply(stream, "!done");
        }, timeout.Token);

        int preserved = 0;
        var login = Login(Active, Expired, () => { preserved++; return Expired(); });
        var result = await login.LoginAsync(Request(port), Now, timeout.Token);
        Assert.True(result.Success);
        Assert.Equal("PaidRouter", result.Session!.Identity);
        Assert.Equal(1, preserved);
        await result.Session.DisposeAsync();
        await server;
        await login.DisposeAsync();
    }

    [Fact]
    public async Task Online_revocation_drops_live_permission_without_reusing_old_grant()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await Reply(stream, "!done");
        }, timeout.Token);

        bool valid = true;
        var login = Login(
            () => valid ? Active() : new("locked", "ملغي", "revoked"),
            Expired, Expired);
        var result = await login.LoginAsync(Request(port), Now, timeout.Token);
        Assert.True(result.Success);
        valid = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            result.Session!.ReadAsync("/system/resource/print", cancellationToken: timeout.Token));
        await result.Session!.DisposeAsync();
        await server;
        await login.DisposeAsync();
    }

    [Fact]
    public async Task Invalid_router_address_and_missing_trial_state_fail_before_network()
    {
        var coordinator = Login(
            () => new("locked", "غير مسموح", "not_found"),
            () => new("locked", "state_invalid", "سجل تالف", 0), Started);
        var result = await coordinator.LoginAsync(Request(8728, free: true), Now);
        Assert.False(result.Success);
        Assert.Equal("state_invalid", result.Reason);
        Assert.Null(result.Session);
        await coordinator.DisposeAsync();
    }
}
