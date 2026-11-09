using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RouterAccountDirectoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static LicenseAccess Active() =>
        new("active", "صالح", "online_valid", "0123456789ABCDEF0123456789ABCDEF");
    private static TrialAccess Expired() => new("locked", "trial_expired", "انتهت", 0);
    private static Task Reply(NetworkStream stream, params string[] words) =>
        stream.WriteAsync(RouterOsSentenceCodec.Encode(words)).AsTask();

    private static async Task<RouterAuthorizedSession> Connect(int port, CancellationToken token)
    {
        var coordinator = RouterLoginCoordinator.CreateForTests(Active, Expired, Expired);
        var login = await coordinator.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "operator", "private-pass"),
            Now, token);
        Assert.True(login.Success);
        return login.Session!;
    }

    private static async Task AcceptLogin(NetworkStream stream, CancellationToken token)
    {
        Assert.Contains("/login", await RouterOsSentenceCodec.ReadAsync(stream, token));
        await Reply(stream, "!done");
        Assert.Contains("/system/identity/print", await RouterOsSentenceCodec.ReadAsync(stream, token));
        await Reply(stream, "!re", "=name=Test-Router");
        await Reply(stream, "!done");
    }

    [Fact]
    public async Task Ppp_directory_reads_safe_fields_without_requesting_passwords_or_writes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await AcceptLogin(stream, deadline.Token);
            var query = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ppp/secret/print", query);
            Assert.Contains("=.proplist=name,profile,disabled,service", query);
            Assert.DoesNotContain(query, word => word.Contains("password", StringComparison.OrdinalIgnoreCase));
            await Reply(stream, "!re", "=name=user1", "=profile=basic",
                "=disabled=false", "=service=pppoe", "=password=server-must-not-request");
            await Reply(stream, "!done");
        }, deadline.Token);

        await using var session = await Connect(port, deadline.Token);
        var result = await new RouterAccountDirectory().ReadPppAsync(session, deadline.Token);
        Assert.True(result.Available);
        Assert.Single(result.Accounts);
        Assert.Equal("user1", result.Accounts[0].Name);
        Assert.Equal("basic", result.Accounts[0].Group);
        Assert.Equal("pppoe", result.Accounts[0].Detail);
        Assert.DoesNotContain("server-must-not-request", result.ToString());
        await server;
    }

    [Fact]
    public async Task Ppp_permission_failure_is_unavailable_not_an_empty_success()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await AcceptLogin(stream, deadline.Token);
            Assert.Contains("/ppp/secret/print",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!trap", "=message=permission denied");
            await Reply(stream, "!done");
        }, deadline.Token);
        await using var session = await Connect(port, deadline.Token);
        var result = await new RouterAccountDirectory().ReadPppAsync(session, deadline.Token);
        Assert.False(result.Available);
        Assert.Empty(result.Accounts);
        Assert.Equal("unavailable", result.Version);
        await server;
    }

    [Fact]
    public async Task Closed_session_cannot_read_directory()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await AcceptLogin(stream, deadline.Token);
        }, deadline.Token);
        var session = await Connect(port, deadline.Token);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new RouterAccountDirectory().ReadPppAsync(session, deadline.Token));
        await server;
    }
}
