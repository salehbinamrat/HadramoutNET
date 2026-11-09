using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RouterVoucherPublisherTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 9, 11, 0, 0, TimeSpan.Zero);
    private static LicenseAccess Active() =>
        new("active", "صالح", "online_valid", "0123456789ABCDEF0123456789ABCDEF");
    private static TrialAccess Expired() =>
        new("locked", "trial_expired", "انتهت", 0);
    private static readonly Voucher[] Cards =
        [new("0001", "voucher001", "secret001"), new("0002", "voucher002", "secret002")];
    private static async Task Send(NetworkStream stream, params string[] words) =>
        await stream.WriteAsync(RouterOsSentenceCodec.Encode(words));

    private static async Task<RouterAuthorizedSession> Connect(
        int port, CancellationToken token)
    {
        var manager = RouterLoginCoordinator.CreateForTests(Active, Expired, Expired);
        var result = await manager.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "tester", "pwd"),
            Now, token);
        Assert.True(result.Success);
        return result.Session!;
    }

    [Fact]
    public async Task Unconfirmed_publication_never_sends_a_write_command()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Send(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Send(stream, "!re", "=name=Router");
            await Send(stream, "!done");
            byte[] probe = new byte[1];
            using var idle = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try { Assert.Equal(0, await stream.ReadAsync(probe, idle.Token)); }
            catch (OperationCanceledException) { }
        }, deadline.Token);
        await using var session = await Connect(port, deadline.Token);
        var result = await new RouterVoucherPublisher().PublishAsync(session,
            Cards, new VoucherPublishOptions("hotspot", "default", Confirmed: false),
            deadline.Token);
        Assert.False(result.Success);
        Assert.Equal("confirmation_required", result.Reason);
        Assert.Equal(0, result.Added);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task Preflight_refuses_existing_username_and_never_starts_an_add()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Send(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Send(stream, "!done");
            var count = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ip/hotspot/user/print", count);
            Assert.Contains("=count-only=yes", count);
            await Send(stream, "!done", "=ret=1");
            var list = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ip/hotspot/user/print", list);
            Assert.Contains("=.proplist=name", list);
            await Send(stream, "!re", "=name=voucher001");
            await Send(stream, "!done");
        }, deadline.Token);

        await using var session = await Connect(port, deadline.Token);
        var result = await new RouterVoucherPublisher().PublishAsync(session,
            Cards, new VoucherPublishOptions("hotspot", "default", Confirmed: true),
            deadline.Token);
        Assert.False(result.Success);
        Assert.Equal("duplicate_username", result.Reason);
        Assert.Equal(0, result.Added);
        await server;
    }

    [Fact]
    public async Task Confirmed_hotspot_upload_validates_profile_and_sends_each_card_once()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Send(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Send(stream, "!re", "=name=Verified");
            await Send(stream, "!done");

            var count = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ip/hotspot/user/print", count);
            Assert.Contains("=count-only=yes", count);
            await Send(stream, "!done", "=ret=1");
            var list = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ip/hotspot/user/print", list);
            await Send(stream, "!re", "=name=other-user");
            await Send(stream, "!done");
            var profileCount = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ip/hotspot/user/profile/print", profileCount);
            Assert.Contains("=count-only=yes", profileCount);
            await Send(stream, "!done", "=ret=1");
            var profiles = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ip/hotspot/user/profile/print", profiles);
            await Send(stream, "!re", "=name=default");
            await Send(stream, "!done");
            foreach (Voucher voucher in Cards)
            {
                var command = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
                Assert.Contains("/ip/hotspot/user/add", command);
                Assert.Contains("=name=" + voucher.Username, command);
                Assert.Contains("=password=" + voucher.Password, command);
                Assert.Contains("=profile=default", command);
                await Send(stream, "!done", "=ret=*A");
            }
        }, deadline.Token);

        await using var session = await Connect(port, deadline.Token);
        var result = await new RouterVoucherPublisher().PublishAsync(session,
            Cards, new VoucherPublishOptions("hotspot", "default", Confirmed: true),
            deadline.Token);
        Assert.True(result.Success);
        Assert.Equal(2, result.Added);
        Assert.False(result.Uncertain);
        Assert.Equal(new[] { "voucher001", "voucher002" }, result.AddedUsernames);
        await server;
    }

    [Fact]
    public async Task Hotspot_upload_applies_each_cards_actual_time_and_transfer_limits()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token); // login
            await Send(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token); // identity
            await Send(stream, "!re", "=name=Verified");
            await Send(stream, "!done");
            var count = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("=count-only=yes", count);
            await Send(stream, "!done", "=ret=0");
            var names = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("=.proplist=name", names);
            await Send(stream, "!done");
            var countProfiles = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("=count-only=yes", countProfiles);
            await Send(stream, "!done", "=ret=1");
            var profiles = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ip/hotspot/user/profile/print", profiles);
            await Send(stream, "!re", "=name=weekly");
            await Send(stream, "!done");
            var add = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ip/hotspot/user/add", add);
            Assert.Contains("=name=voucherlimit", add);
            Assert.Contains("=limit-uptime=2h10m", add);
            Assert.Contains("=limit-bytes-total=2147483648", add);
            Assert.DoesNotContain(add, x => x.StartsWith("=price=", StringComparison.Ordinal));
            await Send(stream, "!done", "=ret=*A");
        }, deadline.Token);

        await using var session = await Connect(port, deadline.Token);
        var report = await new RouterVoucherPublisher().PublishAsync(session,
            [new Voucher("0001", "voucherlimit", "pass1",
                LimitBytes: 2147483648, LimitUptime: "2h10m")],
            new VoucherPublishOptions("hotspot", "weekly", Confirmed: true),
            deadline.Token);
        Assert.True(report.Success);
        Assert.Equal(1, report.Added);
        await server;
    }

    [Theory]
    [InlineData(-1L, "1h")]
    [InlineData(1024L, "invalid")]
    [InlineData(1024L, "1h;bad")]
    [InlineData(1024L, "99999999999999999999999h")]
    public async Task Invalid_card_quota_fails_before_any_router_read_or_write(
        long bytes, string uptime)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Send(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Send(stream, "!re", "=name=Verified");
            await Send(stream, "!done");
            byte[] one = new byte[1];
            using var quiet = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(600));
            try { Assert.Equal(0, await stream.ReadAsync(one, quiet.Token)); }
            catch (OperationCanceledException) { }
        }, deadline.Token);
        await using var session = await Connect(port, deadline.Token);
        var result = await new RouterVoucherPublisher().PublishAsync(session,
            [new Voucher("0001", "voucherinvalid", "password",
                LimitBytes: bytes, LimitUptime: uptime)],
            new VoucherPublishOptions("hotspot", "weekly", Confirmed: true),
            deadline.Token);
        Assert.False(result.Success);
        Assert.Equal("invalid_cards", result.Reason);
        Assert.Equal(0, result.Added);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task Trap_on_second_card_is_partial_and_must_never_retry()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Send(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Send(stream, "!done");
            var usersCount = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("=count-only=yes", usersCount);
            await Send(stream, "!done", "=ret=0");
            var users = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("=.proplist=name", users);
            await Send(stream, "!done"); // Empty user list.
            var profilesCount = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("=count-only=yes", profilesCount);
            await Send(stream, "!done", "=ret=1");
            var profiles = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("=.proplist=name", profiles);
            await Send(stream, "!re", "=name=default");
            await Send(stream, "!done");
            Assert.Contains("=name=voucher001",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Send(stream, "!done", "=ret=*A");
            Assert.Contains("=name=voucher002",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Send(stream, "!trap", "=message=duplicate secret002");
            await Send(stream, "!done");
        }, deadline.Token);

        await using var session = await Connect(port, deadline.Token);
        var result = await new RouterVoucherPublisher().PublishAsync(session, Cards,
            new VoucherPublishOptions("hotspot", "default", Confirmed: true), deadline.Token);
        Assert.False(result.Success);
        Assert.False(result.Uncertain);
        Assert.Equal("partial_failure", result.Reason);
        Assert.Equal(1, result.Added);
        Assert.DoesNotContain("secret002", result.Message);
        await server;
    }
}
