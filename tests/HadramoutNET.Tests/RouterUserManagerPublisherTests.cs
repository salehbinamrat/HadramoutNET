using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RouterUserManagerPublisherTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Voucher[] Cards = [new("0001", "100100", "pass100")];

    private static async Task Reply(NetworkStream stream, params string[] words) =>
        await stream.WriteAsync(RouterOsSentenceCodec.Encode(words));

    private static async Task<RouterAuthorizedSession> Login(int port, CancellationToken token)
    {
        var coordinator = RouterLoginCoordinator.CreateForTests(
            () => new("active", "صالح", "online_valid", "0123456789ABCDEF0123456789ABCDEF"),
            () => new("locked", "trial_expired", "منتهي", 0),
            () => new("locked", "trial_expired", "منتهي", 0));
        RouterLoginResult loggedIn = await coordinator.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "tester", "pwd"), Now, token);
        Assert.True(loggedIn.Success);
        return loggedIn.Session!;
    }

    private static async Task Accept(NetworkStream stream, CancellationToken token)
    {
        Assert.Contains("/login", await RouterOsSentenceCodec.ReadAsync(stream, token));
        await Reply(stream, "!done");
        Assert.Equal("/system/identity/print",
            Assert.Single(await RouterOsSentenceCodec.ReadAsync(stream, token)));
        await Reply(stream, "!re", "=name=router");
        await Reply(stream, "!done");
    }

    [Fact]
    public async Task V6_requires_explicit_confirm_then_creates_account_and_binds_returned_id_to_profile()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await Accept(stream, deadline.Token);
            Assert.Contains("/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!trap", "=message=no such command");
            await Reply(stream, "!done");
            Assert.Contains("/tool/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!re", "=name=monthly", "=owner=admin");
            await Reply(stream, "!done");
            Assert.Contains("/tool/user-manager/user/print",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!done");

            var add = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/tool/user-manager/user/add", add);
            Assert.Contains("=username=100100", add);
            Assert.Contains("=password=pass100", add);
            Assert.Contains("=customer=admin", add);
            Assert.DoesNotContain(add, w => w.StartsWith("=name=", StringComparison.Ordinal));
            await Reply(stream, "!done", "=ret=*A");

            var profile = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/tool/user-manager/user/create-and-activate-profile", profile);
            Assert.Contains("=numbers=*A", profile);
            Assert.Contains("=customer=admin", profile);
            Assert.Contains("=profile=monthly", profile);
            await Reply(stream, "!done");
        }, deadline.Token);

        await using var session = await Login(port, deadline.Token);
        VoucherPublishReport report = await new RouterUserManagerPublisher().PublishAsync(
            session, Cards, new UserManagerPublishOptions("monthly", Confirmed: true),
            deadline.Token);
        Assert.True(report.Success);
        Assert.Equal(1, report.Added);
        Assert.Equal(new[] { "100100" }, report.AddedUsernames);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task V7_uses_name_then_user_profile_not_v6_customer_or_id_commands()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await Accept(stream, deadline.Token);
            Assert.Contains("/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!re", "=name=weekly");
            await Reply(stream, "!done");
            Assert.Contains("/user-manager/user/print",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!done");

            var add = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/user-manager/user/add", add);
            Assert.Contains("=name=100100", add);
            Assert.Contains("=password=pass100", add);
            Assert.DoesNotContain(add, w => w.StartsWith("=customer=", StringComparison.Ordinal));
            await Reply(stream, "!done", "=ret=*B");

            var profile = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/user-manager/user-profile/add", profile);
            Assert.Contains("=user=100100", profile);
            Assert.Contains("=profile=weekly", profile);
            await Reply(stream, "!done");
        }, deadline.Token);

        await using var session = await Login(port, deadline.Token);
        var report = await new RouterUserManagerPublisher().PublishAsync(session, Cards,
            new UserManagerPublishOptions("weekly", Confirmed: true), deadline.Token);
        Assert.True(report.Success);
        Assert.Equal(1, report.Added);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task V6_missing_owner_refuses_first_write()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await Accept(stream, deadline.Token);
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!trap", "=message=bad command name user-manager");
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!re", "=name=plan");
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!done");
            byte[] extra = new byte[1];
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try { Assert.Equal(0, await stream.ReadAsync(extra, timeout.Token)); }
            catch (OperationCanceledException) { }
        }, deadline.Token);

        await using var session = await Login(port, deadline.Token);
        var report = await new RouterUserManagerPublisher().PublishAsync(session, Cards,
            new UserManagerPublishOptions("plan", Confirmed: true), deadline.Token);
        Assert.False(report.Success);
        Assert.Equal("customer_required", report.Reason);
        Assert.Equal(0, report.Added);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task Duplicate_username_stops_before_first_remote_write()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await Accept(stream, deadline.Token);
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!re", "=name=weekly");
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!re", "=name=100100");
            await Reply(stream, "!done");
        }, deadline.Token);

        await using var session = await Login(port, deadline.Token);
        var report = await new RouterUserManagerPublisher().PublishAsync(session, Cards,
            new UserManagerPublishOptions("weekly", Confirmed: true), deadline.Token);
        Assert.Equal("duplicate_username", report.Reason);
        Assert.Equal(0, report.Added);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task Missing_v6_created_id_prevents_profile_link_and_flags_uncertain_delivery()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await Accept(stream, deadline.Token);
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!trap", "=message=unknown command");
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!re", "=name=monthly", "=owner=admin");
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!done");
            Assert.Contains("/tool/user-manager/user/add",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!done"); // Router acknowledged but omitted .ret
            byte[] extra = new byte[1];
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try { Assert.Equal(0, await stream.ReadAsync(extra, timeout.Token)); }
            catch (OperationCanceledException) { }
        }, deadline.Token);

        await using var session = await Login(port, deadline.Token);
        var report = await new RouterUserManagerPublisher().PublishAsync(session, Cards,
            new UserManagerPublishOptions("monthly", Confirmed: true), deadline.Token);
        Assert.False(report.Success);
        Assert.Equal("delivery_uncertain", report.Reason);
        Assert.True(report.Uncertain);
        Assert.Equal(0, report.Added);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task V7_profile_assignment_rejection_stops_without_retry_or_automatic_delete()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await Accept(stream, deadline.Token);
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!re", "=name=weekly");
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!done", "=ret=*C");
            Assert.Contains("/user-manager/user-profile/add",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!trap", "=message=bad profile secret pass100");
            await Reply(stream, "!done");
            byte[] extra = new byte[1];
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try { Assert.Equal(0, await stream.ReadAsync(extra, timeout.Token)); }
            catch (OperationCanceledException) { }
        }, deadline.Token);

        await using var session = await Login(port, deadline.Token);
        var report = await new RouterUserManagerPublisher().PublishAsync(session, Cards,
            new UserManagerPublishOptions("weekly", Confirmed: true), deadline.Token);
        Assert.False(report.Success);
        Assert.True(report.Uncertain);
        Assert.Equal("profile_assignment_uncertain", report.Reason);
        Assert.Equal(0, report.Added);
        Assert.DoesNotContain("pass100", report.Message);
        await session.DisposeAsync();
        await server;
    }
}
