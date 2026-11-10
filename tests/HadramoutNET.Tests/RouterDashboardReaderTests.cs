using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RouterDashboardReaderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static LicenseAccess Active() =>
        new("active", "صالح", "online_valid", "0123456789ABCDEF0123456789ABCDEF");
    private static TrialAccess Expired() =>
        new("locked", "trial_expired", "انتهت", 0);
    private static async Task Reply(NetworkStream stream, params string[] words) =>
        await stream.WriteAsync(RouterOsSentenceCodec.Encode(words));

    [Fact]
    public async Task Dashboard_reads_real_router_resources_and_counts_without_any_mutation()
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
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!re", "=name=My-MikroTik");
            await Reply(stream, "!done");

            var resource = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/system/resource/print", resource);
            await Reply(stream, "!re", "=cpu-load=41", "=total-memory=1000",
                "=free-memory=250", "=version=6.49.19", "=uptime=1d2h",
                "=board-name=RB1100AHx4");
            await Reply(stream, "!done");

            foreach (var (command, count) in new[]
            {
                ("/ip/hotspot/user/print", "102"),
                ("/ip/hotspot/active/print", "24"),
                ("/ppp/active/print", "5"),
                ("/tool/user-manager/user/print", "33")
            })
            {
                var request = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
                Assert.Contains(command, request);
                Assert.Contains("=count-only=yes", request);
                await Reply(stream, "!done", "=ret=" + count);
            }
        }, deadline.Token);

        var coordinator = RouterLoginCoordinator.CreateForTests(
            Active, Expired, Expired);
        var result = await coordinator.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "tester", "secret"),
            Now, deadline.Token);
        Assert.True(result.Success);
        await using var session = result.Session!;
        var dashboard = await new RouterDashboardReader().ReadAsync(session, deadline.Token);
        Assert.Equal("My-MikroTik", dashboard.RouterName);
        Assert.Equal("RB1100AHx4",
            dashboard.GetType().GetProperty("RouterBoardName")?.GetValue(dashboard));
        Assert.Equal("6.49.19", dashboard.RouterOsVersion);
        Assert.Equal("1d2h", dashboard.Uptime);
        Assert.Equal(41, dashboard.CpuPercent);
        Assert.Equal(75, dashboard.MemoryPercent);
        Assert.Equal(102, dashboard.HotspotUsers);
        Assert.Equal(24, dashboard.HotspotActive);
        Assert.Equal(5, dashboard.PppActive);
        Assert.Equal(33, dashboard.UserManagerUsers);
        await server;
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Unavailable_service_is_null_not_zero_and_later_services_still_load()
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
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!re", "=name=Legacy-Router");
            await Reply(stream, "!done");
            Assert.Contains("/system/resource/print",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!re", "=cpu-load=unknown", "=total-memory=0");
            await Reply(stream, "!done");

            Assert.Contains("/ip/hotspot/user/print",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!trap", "=message=no such command");
            await Reply(stream, "!done");

            foreach (var (name, count) in new[]
            {
                ("/ip/hotspot/active/print", "0"),
                ("/ppp/active/print", "9")
            })
            {
                Assert.Contains(name, await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
                await Reply(stream, "!done", "=ret=" + count);
            }
            Assert.Contains("/tool/user-manager/user/print",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!trap", "=message=not installed");
            await Reply(stream, "!done");
        }, deadline.Token);

        var login = RouterLoginCoordinator.CreateForTests(Active, Expired, Expired);
        var result = await login.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "tester", "pwd"),
            Now, deadline.Token);
        Assert.True(result.Success);
        await using var session = result.Session!;
        var dashboard = await new RouterDashboardReader().ReadAsync(session, deadline.Token);
        Assert.Null(dashboard.CpuPercent);
        Assert.Null(dashboard.MemoryPercent);
        Assert.Null(dashboard.HotspotUsers);
        Assert.Equal(0, dashboard.HotspotActive);
        Assert.Equal(9, dashboard.PppActive);
        Assert.Null(dashboard.UserManagerUsers);
        Assert.Contains("Hotspot", dashboard.UnavailableServices);
        Assert.Contains("User Manager", dashboard.UnavailableServices);
        await server;
        await login.DisposeAsync();
    }
}
