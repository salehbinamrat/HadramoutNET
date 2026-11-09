using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RouterCardTargetCatalogTests
{
    private static async Task Send(NetworkStream stream, params string[] words) =>
        await stream.WriteAsync(RouterOsSentenceCodec.Encode(words));

    private static async Task<RouterAuthorizedSession> Login(int port, CancellationToken cancel)
    {
        var manager = RouterLoginCoordinator.CreateForTests(
            () => new LicenseAccess("active", "ok", "online_valid", "0123456789ABCDEF0123456789ABCDEF"),
            () => new TrialAccess("locked", "trial_expired", "no", 0),
            () => new TrialAccess("locked", "trial_expired", "no", 0));
        var result = await manager.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "tester", "pwd"),
            DateTimeOffset.UtcNow, cancel);
        Assert.True(result.Success);
        return result.Session!;
    }

    [Fact]
    public async Task Hotspot_profiles_and_servers_are_read_with_matching_counts()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token); await Send(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await Send(stream, "!re", "=name=Router"); await Send(stream, "!done");

            var count = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/ip/hotspot/user/profile/print", count);
            Assert.Contains("=count-only=yes", count);
            await Send(stream, "!done", "=ret=2");
            var rows = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("=.proplist=name", rows);
            await Send(stream, "!re", "=name=default");
            await Send(stream, "!re", "=name=Gold"); await Send(stream, "!done");

            var serverCount = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/ip/hotspot/print", serverCount);
            await Send(stream, "!done", "=ret=1");
            var serverRows = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("=.proplist=name", serverRows);
            await Send(stream, "!re", "=name=Server1"); await Send(stream, "!done");
        }, timeout.Token);

        await using var session = await Login(port, timeout.Token);
        var result = await new RouterCardTargetCatalog().ReadAsync(session, "hotspot", timeout.Token);
        Assert.True(result.Available);
        Assert.Equal(new[] { "default", "Gold" },
            result.Profiles.Select(x => x.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        Assert.Equal("Server1", Assert.Single(result.Servers));
        await server;
    }

    [Fact]
    public async Task Incomplete_profiles_are_not_exposed_as_available()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token); await Send(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await Send(stream, "!re", "=name=Router"); await Send(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await Send(stream, "!done", "=ret=2");
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await Send(stream, "!re", "=name=OnlyOne"); await Send(stream, "!done");
        }, timeout.Token);
        await using var session = await Login(port, timeout.Token);
        var result = await new RouterCardTargetCatalog().ReadAsync(session, "ppp", timeout.Token);
        Assert.False(result.Available);
        Assert.Equal("incomplete", result.Reason);
        await server;
    }
}
