using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>
/// Real RouterOS TCP sentences, not mocked API responses. No remote writes.
/// </summary>
public sealed class UserManagerVoucherVerificationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static async Task Reply(NetworkStream stream, params string[] words) =>
        await stream.WriteAsync(RouterOsSentenceCodec.Encode(words));

    private static async Task<RouterAuthorizedSession> Connect(int port, CancellationToken token)
    {
        var login = RouterLoginCoordinator.CreateForTests(
            () => new LicenseAccess("active", "ok", "online_valid", "0123456789ABCDEF0123456789ABCDEF"),
            () => new TrialAccess("locked", "trial_expired", "expired", 0),
            () => new TrialAccess("locked", "trial_expired", "expired", 0));
        var result = await login.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "tester", "password"),
            Now, token);
        Assert.True(result.Success);
        return result.Session!;
    }

    private static async Task HandlePeer(TcpListener listener, CancellationToken token,
        Func<NetworkStream, Task> serve)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        using var stream = client.GetStream();
        Assert.Contains("/login", await RouterOsSentenceCodec.ReadAsync(stream, token));
        await Reply(stream, "!done");
        Assert.Equal("/system/identity/print",
            Assert.Single(await RouterOsSentenceCodec.ReadAsync(stream, token)));
        await Reply(stream, "!re", "=name=Router");
        await Reply(stream, "!done");
        await serve(stream);
    }

    [Fact]
    public async Task Um7_reads_separate_profile_assignments_and_distinguishes_active_ready_used_and_absent()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = HandlePeer(listener, timeout.Token, async stream =>
        {
            var profiles = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/user-manager/profile/print", profiles);
            await Reply(stream, "!re", "=name=monthly");
            await Reply(stream, "!done");
            var users = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/user-manager/user/print", users);
            Assert.DoesNotContain(users, x => x.Contains("password", StringComparison.OrdinalIgnoreCase));
            foreach (string username in new[] { "one", "two", "three", "four", "five" })
                await Reply(stream, "!re", "=name=" + username, "=disabled=no");
            await Reply(stream, "!done");
            var userCount = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/user-manager/user/print", userCount);
            Assert.Contains("=count-only=yes", userCount);
            await Reply(stream, "!done", "=ret=5");
            var count = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/user-manager/user-profile/print", count);
            Assert.Contains("=count-only=yes", count);
            await Reply(stream, "!done", "=ret=4");
            var assignments = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/user-manager/user-profile/print", assignments);
            Assert.Contains("=.proplist=user,profile,state", assignments);
            Assert.DoesNotContain(assignments, x => x.Contains("password", StringComparison.OrdinalIgnoreCase));
            await Reply(stream, "!re", "=user=one", "=profile=monthly", "=state=running active");
            await Reply(stream, "!re", "=user=two", "=profile=monthly", "=state=running");
            await Reply(stream, "!re", "=user=three", "=profile=monthly", "=state=used");
            await Reply(stream, "!re", "=user=four", "=profile=other", "=state=running active");
            await Reply(stream, "!done");
        });
        await using var session = await Connect(port, timeout.Token);
        var result = await new UserManagerVoucherVerifier().VerifyAsync(session,
            ["one", "two", "three", "four", "five", "absent"], "monthly", timeout.Token);
        Assert.True(result.Available);
        Assert.Equal(7, result.Version);
        Assert.Equal(new[] { "profile_active", "profile_ready", "profile_used",
                "profile_other", "profile_missing", "not_found" },
            result.Cards.Select(c => c.State));
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task Um6_falls_back_and_reports_actual_profile_without_claiming_live_assignment()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = HandlePeer(listener, timeout.Token, async stream =>
        {
            Assert.Contains("/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token));
            await Reply(stream, "!trap", "=message=no such command");
            await Reply(stream, "!done");
            Assert.Contains("/tool/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token));
            await Reply(stream, "!re", "=name=daily", "=owner=admin");
            await Reply(stream, "!done");
            var users = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/tool/user-manager/user/print", users);
            await Reply(stream, "!re", "=username=a", "=actual-profile=daily", "=disabled=no");
            await Reply(stream, "!re", "=username=b", "=actual-profile=daily", "=disabled=yes");
            await Reply(stream, "!re", "=username=c", "=actual-profile=other", "=disabled=no");
            await Reply(stream, "!re", "=username=d", "=disabled=no");
            await Reply(stream, "!done");
            var userCount = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/tool/user-manager/user/print", userCount);
            Assert.Contains("=count-only=yes", userCount);
            await Reply(stream, "!done", "=ret=4");
        });
        await using var session = await Connect(port, timeout.Token);
        var result = await new UserManagerVoucherVerifier().VerifyAsync(session,
            ["a", "b", "c", "d", "e"], "daily", timeout.Token);
        Assert.True(result.Available);
        Assert.Equal(6, result.Version);
        Assert.Equal(new[] { "profile_reported", "disabled", "profile_other",
                "profile_unverified", "not_found" }, result.Cards.Select(c => c.State));
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task Um7_denied_association_listing_is_unavailable_not_missing_profiles()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = HandlePeer(listener, timeout.Token, async stream =>
        {
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await Reply(stream, "!re", "=name=daily");
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await Reply(stream, "!re", "=name=a");
            await Reply(stream, "!done");
            var userCount = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/user-manager/user/print", userCount);
            Assert.Contains("=count-only=yes", userCount);
            await Reply(stream, "!done", "=ret=1");
            var count = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/user-manager/user-profile/print", count);
            Assert.Contains("=count-only=yes", count);
            await Reply(stream, "!trap", "=message=not enough permissions");
            await Reply(stream, "!done");
        });
        await using var session = await Connect(port, timeout.Token);
        var result = await new UserManagerVoucherVerifier().VerifyAsync(
            session, ["a"], "daily", timeout.Token);
        Assert.False(result.Available);
        Assert.Equal("unavailable", result.Status);
        Assert.Empty(result.Cards);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task Validation_rejects_duplicate_or_invalid_usernames_before_any_read()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = HandlePeer(listener, timeout.Token, async stream =>
        {
            byte[] buffer = new byte[1];
            using var idle = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            try { Assert.Equal(0, await stream.ReadAsync(buffer, idle.Token)); }
            catch (OperationCanceledException) { }
        });
        await using var session = await Connect(port, timeout.Token);
        var verifier = new UserManagerVoucherVerifier();
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await verifier.VerifyAsync(session, ["a", "a"], "daily", timeout.Token));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await verifier.VerifyAsync(session, ["\n"], "daily", timeout.Token));
        await session.DisposeAsync();
        await server;
    }
}
