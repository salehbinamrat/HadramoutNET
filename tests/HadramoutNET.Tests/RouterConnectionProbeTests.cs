using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>Real TCP integration; only a loopback fake RouterOS server is contacted.</summary>
public sealed class RouterConnectionProbeTests
{
    private static async Task RespondAsync(NetworkStream stream, params string[] words) =>
        await stream.WriteAsync(RouterOsSentenceCodec.Encode(words));

    [Fact]
    public async Task Successful_probe_requires_authenticated_identity_read_and_does_not_write_router()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            var auth = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/login", auth);
            Assert.Contains("=name=operator", auth);
            Assert.Contains("=password=super-private", auth);
            await RespondAsync(stream, "!done");

            var identity = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Equal("/system/identity/print", Assert.Single(identity));
            await RespondAsync(stream, "!re", "=name=مكلا - NET");
            await RespondAsync(stream, "!done");

            // A probe must not send card mutations or retain a live session.
            byte[] extra = new byte[1];
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { Assert.Equal(0, await stream.ReadAsync(extra, wait.Token)); }
            catch (OperationCanceledException) { } // No further request was sent.
        }, timeout.Token);

        var probe = new RouterConnectionProbe();
        var result = await probe.TestAsync(
            new RouterProbeRequest("127.0.0.1", port.ToString(), "operator", "super-private"),
            timeout.Token);
        Assert.True(result.Success);
        Assert.Equal("authenticated_read_only", result.Status);
        Assert.Equal("مكلا - NET", result.Identity);
        Assert.Equal($"127.0.0.1:{port}", result.Endpoint);
        Assert.DoesNotContain("super-private", result.Message);
        await server;
    }

    [Fact]
    public async Task Wrong_credentials_never_count_as_success_or_trigger_retry()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int loginRequests = 0;
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            Assert.Contains("/login", await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token));
            loginRequests++;
            await RespondAsync(stream, "!trap", "=message=wrong password: super-private");
            await RespondAsync(stream, "!done");
        }, timeout.Token);

        var probe = new RouterConnectionProbe();
        var result = await probe.TestAsync(
            new RouterProbeRequest("127.0.0.1", port.ToString(), "operator", "super-private",
                ConnectionMode: "remote"), timeout.Token);
        Assert.False(result.Success);
        Assert.Equal("authentication_rejected", result.Status);
        Assert.Equal(1, loginRequests);
        Assert.DoesNotContain("super-private", result.Message);
        await server;
    }

    [Fact]
    public async Task Authenticated_transport_without_identity_permission_is_not_a_verified_probe()
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
            await RespondAsync(stream, "!done");
            Assert.Equal("/system/identity/print",
                Assert.Single(await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token)));
            await RespondAsync(stream, "!trap", "=message=forbidden identity");
            await RespondAsync(stream, "!done");
        }, timeout.Token);

        var result = await new RouterConnectionProbe().TestAsync(
            new RouterProbeRequest("127.0.0.1", port.ToString(), "operator", "super-private"),
            timeout.Token);
        Assert.False(result.Success);
        Assert.Equal("identity_unavailable", result.Status);
        Assert.Equal("", result.Identity);
        await server;
    }

    [Fact]
    public async Task Empty_identity_reply_falls_back_to_normalized_host_only_after_read_completed()
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
            await RespondAsync(stream, "!done");
            Assert.Equal("/system/identity/print",
                Assert.Single(await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token)));
            await RespondAsync(stream, "!done");
        }, timeout.Token);

        var result = await new RouterConnectionProbe().TestAsync(
            new RouterProbeRequest("127.0.0.1", port.ToString(), "operator", ""), timeout.Token);
        Assert.True(result.Success);
        Assert.Equal("127.0.0.1", result.Identity);
        await server;
    }

    [Theory]
    [InlineData("127.0.0.1", "abc", "operator", "local")]
    [InlineData("127.0.0.1", "8728", "", "local")]
    [InlineData("127.0.0.1", "8728", "x\nname", "local")]
    [InlineData("http://example.com/path", "8728", "operator", "remote")]
    [InlineData("127.0.0.1", "8728", "operator", "wrong-mode")]
    public async Task Invalid_fields_are_rejected_before_any_router_connection(
        string host, string port, string user, string mode)
    {
        var result = await new RouterConnectionProbe().TestAsync(
            new RouterProbeRequest(host, port, user, "private",
                ConnectionMode: mode));
        Assert.False(result.Success);
        Assert.Equal("invalid_settings", result.Status);
        Assert.DoesNotContain("private", result.Message);
    }

    [Fact]
    public async Task Remote_endpoint_uses_explicit_api_port_not_embedded_winbox_port()
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
            await RespondAsync(stream, "!done");
            Assert.Equal("/system/identity/print",
                Assert.Single(await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token)));
            await RespondAsync(stream, "!re", "=name=remote-probe");
            await RespondAsync(stream, "!done");
        }, timeout.Token);

        var result = await new RouterConnectionProbe().TestAsync(
            new RouterProbeRequest("http://127.0.0.1:8291/", port.ToString(), "operator",
                "private", ConnectionMode: "remote"), timeout.Token);
        Assert.True(result.Success);
        Assert.Equal($"127.0.0.1:{port}", result.Endpoint);
        Assert.Equal("remote-probe", result.Identity);
        await server;
    }

    [Fact]
    public async Task User_cancel_during_network_attempt_does_not_return_success()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(cancellation.Token);
            connected.SetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        }, cancellation.Token);
        using var attempt = new CancellationTokenSource();
        Task<RouterProbeResult> pending = new RouterConnectionProbe().TestAsync(
            new RouterProbeRequest("127.0.0.1", port.ToString(), "operator", "private"), attempt.Token);
        await connected.Task.WaitAsync(cancellation.Token);
        attempt.Cancel();
        var result = await pending;
        Assert.False(result.Success);
        Assert.Equal("cancelled", result.Status);
        cancellation.Cancel();
        try { await server; }
        catch (OperationCanceledException) { }
    }
}
