using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RouterNetworkInventoryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 9, 13, 0, 0, TimeSpan.Zero);
    private static LicenseAccess Active() =>
        new("active", "valid", "online_valid", "0123456789ABCDEF0123456789ABCDEF");
    private static TrialAccess Expired() =>
        new("locked", "trial_expired", "expired", 0);
    private static Task Reply(NetworkStream stream, params string[] words) =>
        stream.WriteAsync(RouterOsSentenceCodec.Encode(words)).AsTask();

    private static async Task<RouterAuthorizedSession> Connect(int port, CancellationToken ct)
    {
        var coordinator = RouterLoginCoordinator.CreateForTests(Active, Expired, Expired);
        var result = await coordinator.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "operator", "sensitive"),
            Now, ct);
        Assert.True(result.Success);
        return result.Session!;
    }

    private static async Task Authenticate(NetworkStream stream, CancellationToken ct)
    {
        Assert.Contains("/login", await RouterOsSentenceCodec.ReadAsync(stream, ct));
        await Reply(stream, "!done");
        Assert.Contains("/system/identity/print", await RouterOsSentenceCodec.ReadAsync(stream, ct));
        await Reply(stream, "!re", "=name=Test-Router");
        await Reply(stream, "!done");
    }

    [Fact]
    public async Task Servers_return_only_known_fields_and_valid_ids_in_sorted_order()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await Authenticate(stream, deadline.Token);
            var command = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ip/hotspot/print", command);
            Assert.Contains("=.proplist=.id,name,interface,profile,disabled,invalid,running", command);
            await Reply(stream, "!re", "=.id=*B", "=name=Z", "=interface=bridge2",
                "=profile=default", "=disabled=no", "=running=yes", "=password=DO_NOT_LEAK");
            await Reply(stream, "!re", "=.id=invalid", "=name=IGNORE");
            await Reply(stream, "!re", "=.id=*A", "=name=Alpha", "=interface=bridge1",
                "=profile=guests", "=disabled=true", "=invalid=no", "=running=yes");
            await Reply(stream, "!done");
        }, deadline.Token);
        await using var session = await Connect(port, deadline.Token);
        var result = await new RouterNetworkInventory().ReadServersAsync(session, deadline.Token);
        Assert.True(result.Available);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("Alpha", result.Items[0].Name);
        Assert.Equal("*A", result.Items[0].Id);
        Assert.True(result.Items[0].Disabled);
        Assert.Equal("Z", result.Items[1].Name);
        Assert.False(result.Items[1].Disabled);
        Assert.DoesNotContain("DO_NOT_LEAK", result.ToString());
        await server;
    }

    [Fact]
    public async Task Neighbors_preflight_count_limits_and_projects_only_discovery_fields()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await Authenticate(stream, deadline.Token);
            var count = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ip/neighbor/print", count);
            Assert.Contains("=count-only=yes", count);
            await Reply(stream, "!done", "=ret=1");
            var list = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ip/neighbor/print", list);
            Assert.Contains("=.proplist=interface,address,address4,mac-address,identity,platform,version,board", list);
            await Reply(stream, "!re", "=interface=ether3", "=address=fe80::1",
                "=address4=10.0.0.3", "=mac-address=AA:BB:CC:DD:EE:FF",
                "=identity=Device", "=platform=MikroTik", "=version=6.49.19",
                "=password=LEAK");
            await Reply(stream, "!done");
        }, deadline.Token);
        await using var session = await Connect(port, deadline.Token);
        var result = await new RouterNetworkInventory().ReadNeighborsAsync(session, deadline.Token);
        Assert.True(result.Available);
        Assert.Equal("10.0.0.3", Assert.Single(result.Items).Address);
        Assert.Equal("AA:BB:CC:DD:EE:FF", result.Items[0].Mac);
        Assert.DoesNotContain("LEAK", result.ToString());
        await server;
    }

    [Fact]
    public async Task Too_many_neighbors_refuses_listing_after_safe_count()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await Authenticate(stream, deadline.Token);
            var count = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("=count-only=yes", count);
            await Reply(stream, "!done", "=ret=5001");
            // No listing may be sent after the maximum was exceeded.
            var buffer = new byte[1];
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try { Assert.Equal(0, await stream.ReadAsync(buffer, timeout.Token)); }
            catch (OperationCanceledException) { }
        }, deadline.Token);
        await using var session = await Connect(port, deadline.Token);
        var result = await new RouterNetworkInventory().ReadNeighborsAsync(session, deadline.Token);
        Assert.False(result.Available);
        Assert.Equal("limit_exceeded", result.Reason);
        Assert.Empty(result.Items);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task Sessions_count_both_services_before_loading_and_drop_other_fields()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await Authenticate(stream, deadline.Token);
            foreach (string name in new[] { "/ip/hotspot/active/print", "/ppp/active/print" })
            {
                var cmd = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
                Assert.Contains(name, cmd);
                Assert.Contains("=count-only=yes", cmd);
                await Reply(stream, "!done", "=ret=1");
            }
            foreach (string name in new[] { "/ip/hotspot/active/print", "/ppp/active/print" })
            {
                var cmd = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
                Assert.Contains(name, cmd);
                Assert.Contains("=.proplist=.id,user,address,name,caller-id,session-id,mac-address,server", cmd);
                await Reply(stream, "!re", "=.id=*A", "=user=user1", "=address=10.0.0.4",
                    "=password=DO_NOT_DISPLAY");
                await Reply(stream, "!done");
            }
        }, deadline.Token);
        await using var session = await Connect(port, deadline.Token);
        var result = await new RouterNetworkInventory().ReadSessionsAsync(session, deadline.Token);
        Assert.True(result.Available);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("Hotspot", result.Items[0].Service);
        Assert.Equal("PPPoE", result.Items[1].Service);
        Assert.Equal("user1", result.Items[0].Username);
        Assert.DoesNotContain("DO_NOT_DISPLAY", result.ToString());
        await server;
    }

    [Fact]
    public async Task Sessions_denied_preflight_does_not_claim_empty_success()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await Authenticate(stream, deadline.Token);
            Assert.Contains("/ip/hotspot/active/print",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!trap", "=message=not enough permissions");
            await Reply(stream, "!done");
        }, deadline.Token);
        await using var session = await Connect(port, deadline.Token);
        var result = await new RouterNetworkInventory().ReadSessionsAsync(session, deadline.Token);
        Assert.False(result.Available);
        Assert.Equal("unavailable", result.Reason);
        Assert.Empty(result.Items);
        await server;
    }

    [Fact]
    public async Task Closed_session_refuses_all_network_inventory_requests()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await Authenticate(stream, deadline.Token);
        }, deadline.Token);
        var session = await Connect(port, deadline.Token);
        await session.DisposeAsync();
        var inventory = new RouterNetworkInventory();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => inventory.ReadServersAsync(session, deadline.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => inventory.ReadNeighborsAsync(session, deadline.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => inventory.ReadSessionsAsync(session, deadline.Token));
        await server;
    }
}
