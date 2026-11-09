using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class DashboardUserManagerVersionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static async Task Send(NetworkStream stream, params string[] words) =>
        await stream.WriteAsync(RouterOsSentenceCodec.Encode(words));

    private static async Task<RouterAuthorizedSession> Login(int port, CancellationToken token)
    {
        var manager = RouterLoginCoordinator.CreateForTests(
            () => new("active", "valid", "online_valid", "0123456789ABCDEF0123456789ABCDEF"),
            () => new("locked", "trial_expired", "expired", 0),
            () => new("locked", "trial_expired", "expired", 0));
        var result = await manager.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "test", "secret"), Now, token);
        Assert.True(result.Success);
        return result.Session!;
    }

    private static async Task Initial(NetworkStream stream, CancellationToken ct)
    {
        Assert.Contains("/login", await RouterOsSentenceCodec.ReadAsync(stream, ct));
        await Send(stream, "!done");
        Assert.Contains("/system/identity/print", await RouterOsSentenceCodec.ReadAsync(stream, ct));
        await Send(stream, "!re", "=name=router");
        await Send(stream, "!done");
        Assert.Contains("/system/resource/print", await RouterOsSentenceCodec.ReadAsync(stream, ct));
        await Send(stream, "!re", "=cpu-load=20", "=total-memory=1000", "=free-memory=500");
        await Send(stream, "!done");
        foreach (string command in new[] { "/ip/hotspot/user/print",
            "/ip/hotspot/active/print", "/ppp/active/print" })
        {
            Assert.Contains(command, await RouterOsSentenceCodec.ReadAsync(stream, ct));
            await Send(stream, "!done", "=ret=0");
        }
    }

    [Fact]
    public async Task Dashboard_counts_user_manager_v7_after_v6_command_is_unsupported()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            await Initial(stream, timeout.Token);
            var v6 = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/tool/user-manager/user/print", v6);
            Assert.Contains("=count-only=yes", v6);
            await Send(stream, "!trap", "=message=no such command");
            await Send(stream, "!done");

            var v7 = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/user-manager/user/print", v7);
            Assert.Contains("=count-only=yes", v7);
            await Send(stream, "!done", "=ret=72");
        }, timeout.Token);

        await using var session = await Login(port, timeout.Token);
        var result = await new RouterDashboardReader().ReadAsync(session, timeout.Token);
        Assert.Equal(72, result.UserManagerUsers);
        Assert.DoesNotContain("User Manager", result.UnavailableServices);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task Permission_denied_on_v6_does_not_downgrade_to_v7_or_disguise_it_as_zero()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            await Initial(stream, timeout.Token);
            Assert.Contains("/tool/user-manager/user/print",
                await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token));
            await Send(stream, "!trap", "=message=not enough permissions");
            await Send(stream, "!done");
            byte[] extra = new byte[1];
            using var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try { Assert.Equal(0, await stream.ReadAsync(extra, shortWait.Token)); }
            catch (OperationCanceledException) { }
        }, timeout.Token);

        await using var session = await Login(port, timeout.Token);
        var result = await new RouterDashboardReader().ReadAsync(session, timeout.Token);
        Assert.Null(result.UserManagerUsers);
        Assert.Contains("User Manager", result.UnavailableServices);
        await session.DisposeAsync();
        await server;
    }
}
