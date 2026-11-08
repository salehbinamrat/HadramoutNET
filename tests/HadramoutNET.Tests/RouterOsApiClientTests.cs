using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>
/// Real TCP loopback integration tests, no router or test credentials required.
/// </summary>
public sealed class RouterOsApiClientTests
{
    private static async Task SendAsync(NetworkStream stream, params string[] words) =>
        await stream.WriteAsync(RouterOsSentenceCodec.Encode(words));

    [Fact]
    public async Task Authenticates_and_reads_identity_over_real_TCP()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using NetworkStream stream = peer.GetStream();
            var login = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/login", login);
            Assert.Contains("=name=tester", login);
            await SendAsync(stream, "!done");
            var command = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Equal("/system/identity/print", Assert.Single(command));
            await SendAsync(stream, "!re", "=name=router-one");
            await SendAsync(stream, "!done");
        }, timeout.Token);

        await using var api = new RouterOsApiClient(
            new RouterOsConnectionOptions("127.0.0.1", port, "tester", "redacted"));
        await api.ConnectAsync(timeout.Token);
        var identity = await api.ExecuteAsync("/system/identity/print", cancellationToken: timeout.Token);
        Assert.Equal("router-one", Assert.Single(identity.Rows)["name"]);
        await server;
    }

    [Fact]
    public async Task Rejected_login_is_not_retried_in_remote_mode()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using NetworkStream stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await SendAsync(stream, "!trap", "=message=wrong password");
            await SendAsync(stream, "!done");
        }, timeout.Token);

        await using var api = new RouterOsApiClient(
            new RouterOsConnectionOptions("127.0.0.1", port, "tester", "wrong", RemoteMode: true));
        var ex = await Assert.ThrowsAsync<RouterOsTrapException>(() => api.ConnectAsync(timeout.Token));
        Assert.Equal("wrong password", ex.Message);
        Assert.False(api.Connected);
        await server;
    }

    [Fact]
    public async Task Legacy_login_challenge_is_answered_exactly_once()
    {
        byte[] challenge = [1, 2, 3, 4, 5];
        string challengeHex = Convert.ToHexString(challenge).ToLowerInvariant();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using NetworkStream stream = peer.GetStream();
            var first = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("=password=pwd", first);
            await SendAsync(stream, "!done", "=ret=" + challengeHex);
            var second = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            byte[] input = [0, ..Encoding.UTF8.GetBytes("pwd"), ..challenge];
            string expected = "00" + Convert.ToHexString(MD5.HashData(input)).ToLowerInvariant();
            Assert.Contains("=response=" + expected, second);
            await SendAsync(stream, "!done");
        }, timeout.Token);

        await using var api = new RouterOsApiClient(
            new RouterOsConnectionOptions("127.0.0.1", port, "tester", "pwd"));
        await api.ConnectAsync(timeout.Token);
        Assert.True(api.Connected);
        await server;
    }

    [Fact]
    public async Task Unsafe_command_is_refused_before_a_write()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using NetworkStream stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await SendAsync(stream, "!done");
            byte[] probe = new byte[1];
            using var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            try
            {
                int received = await stream.ReadAsync(probe, shortWait.Token);
                Assert.Equal(0, received);
            }
            catch (OperationCanceledException) { /* No second command reached the socket. */ }
        }, timeout.Token);

        await using (var api = new RouterOsApiClient(
            new RouterOsConnectionOptions("127.0.0.1", port, "tester", "pwd")))
        {
            await api.ConnectAsync(timeout.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => api.ExecuteAsync("/ip/firewall/filter/add", cancellationToken: timeout.Token));
        }
        await server;
    }
}
