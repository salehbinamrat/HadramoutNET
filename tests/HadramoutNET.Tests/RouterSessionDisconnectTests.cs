using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.Data;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>Real RouterOS protocol over loopback, not mocked service results.</summary>
public sealed class RouterSessionDisconnectTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 13, 0, 0, TimeSpan.Zero);
    private static LicenseAccess Paid() =>
        new("active", "valid", "online_valid", "0123456789ABCDEF0123456789ABCDEF");
    private static TrialAccess Locked() => new("locked", "expired", "expired", 0);
    private static Task Send(NetworkStream s, params string[] words) =>
        s.WriteAsync(RouterOsSentenceCodec.Encode(words)).AsTask();

    private static async Task<RouterAuthorizedSession> Connect(int port, CancellationToken ct)
    {
        var coordinator = RouterLoginCoordinator.CreateForTests(Paid, Locked, Locked);
        var result = await coordinator.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "admin", "testpassword"), Now, ct);
        Assert.True(result.Success);
        return result.Session!;
    }

    private static async Task Handshake(NetworkStream stream, CancellationToken ct)
    {
        Assert.Contains("/login", await RouterOsSentenceCodec.ReadAsync(stream, ct));
        await Send(stream, "!done");
        Assert.Contains("/system/identity/print",
            await RouterOsSentenceCodec.ReadAsync(stream, ct));
        await Send(stream, "!re", "=name=TestRouter");
        await Send(stream, "!done");
    }

    private static readonly NetworkActiveSession Selected =
        new("*A", "Hotspot", "voucher-001", "10.0.0.12");

    private sealed class TestJournal : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "hnet-disconnect-test-" + Guid.NewGuid().ToString("N"));
        public SessionMaintenanceJournal Journal { get; }

        public TestJournal()
        {
            Directory.CreateDirectory(_dir);
            Journal = new SessionMaintenanceJournal(Path.Combine(_dir, "hadramout-net.db"));
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);
    }



    private static async Task NoMoreCommands(NetworkStream stream)
    {
        var one = new byte[1];
        using var delay = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        try { Assert.Equal(0, await stream.ReadAsync(one, delay.Token)); }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Without_confirmation_no_router_command_is_sent()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await Handshake(stream, timeout.Token);
            await NoMoreCommands(stream);
        }, timeout.Token);
        await using var session = await Connect(port, timeout.Token);
        using var audit = new TestJournal();
        var result = await new RouterSessionDisconnect(audit.Journal).DisconnectAsync(
            session, Selected, confirmed: false, timeout.Token);
        Assert.False(result.Success);
        Assert.False(result.Uncertain);
        Assert.Equal("confirmation_required", result.Reason);
        Assert.Empty(audit.Journal.ReadRecent());
        await session.DisposeAsync();
        await peer;
    }

    [Fact]
    public async Task Stale_selected_session_refuses_remove_even_when_record_id_matches()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await Handshake(stream, timeout.Token);
            var read = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/ip/hotspot/active/print", read);
            Assert.Contains("?.id=*A", read);
            Assert.Contains("=.proplist=.id,user,name,address", read);
            await Send(stream, "!re", "=.id=*A", "=user=other-user", "=address=10.0.0.12");
            await Send(stream, "!done");
            await NoMoreCommands(stream);
        }, timeout.Token);
        await using var session = await Connect(port, timeout.Token);
        using var audit = new TestJournal();
        var result = await new RouterSessionDisconnect(audit.Journal).DisconnectAsync(
            session, Selected, confirmed: true, timeout.Token);
        Assert.False(result.Success);
        Assert.False(result.Uncertain);
        Assert.Equal("stale_selection", result.Reason);
        Assert.Equal("rejected", Assert.Single(audit.Journal.ReadRecent()).State);
        await session.DisposeAsync();
        await peer;
    }

    [Fact]
    public async Task Confirmed_targeted_disconnect_writes_only_one_exact_record_and_never_retries()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await Handshake(stream, timeout.Token);
            var preflight = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("?.id=*A", preflight);
            await Send(stream, "!re", "=.id=*A", "=user=voucher-001", "=address=10.0.0.12");
            await Send(stream, "!done");
            var write = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Equal("/ip/hotspot/active/remove", write[0]);
            Assert.Contains("=.id=*A", write);
            Assert.Equal(2, write.Count);
            await Send(stream, "!done");
            await NoMoreCommands(stream);
        }, timeout.Token);
        await using var session = await Connect(port, timeout.Token);
        using var audit = new TestJournal();
        var result = await new RouterSessionDisconnect(audit.Journal).DisconnectAsync(
            session, Selected, confirmed: true, timeout.Token);
        Assert.True(result.Success);
        Assert.False(result.Uncertain);
        Assert.Equal("accepted", result.Reason);
        Assert.Equal("accepted", Assert.Single(audit.Journal.ReadRecent()).State);
        await session.DisposeAsync();
        await peer;
    }

    [Fact]
    public async Task Lost_disconnect_ack_is_uncertain_and_never_retried()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await Handshake(stream, timeout.Token);
            var preflight = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/ppp/active/print", preflight);
            await Send(stream, "!re", "=.id=*B", "=name=ppp01", "=address=10.0.0.22");
            await Send(stream, "!done");
            var write = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Equal("/ppp/active/remove", write[0]);
            Assert.Contains("=.id=*B", write);
            // Simulate router processing the remove and dropping the socket
            // before delivering an ACK. No second command can be sent.
        }, timeout.Token);
        await using var session = await Connect(port, timeout.Token);
        var selected = new NetworkActiveSession("*B", "PPPoE", "ppp01", "10.0.0.22");
        using var audit = new TestJournal();
        var result = await new RouterSessionDisconnect(audit.Journal).DisconnectAsync(
            session, selected, confirmed: true, timeout.Token);
        Assert.False(result.Success);
        Assert.True(result.Uncertain);
        Assert.Equal("delivery_uncertain", result.Reason);
        var logged = Assert.Single(audit.Journal.ReadRecent());
        Assert.Equal("uncertain", logged.State);
        Assert.True(logged.RequiresManualReconciliation);
        await peer;
    }

    [Fact]
    public async Task Explicit_router_trap_is_rejected_not_replayed()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await Handshake(stream, timeout.Token);
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await Send(stream, "!re", "=.id=*A", "=user=voucher-001", "=address=10.0.0.12");
            await Send(stream, "!done");
            Assert.Contains("/ip/hotspot/active/remove",
                await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token));
            await Send(stream, "!trap", "=message=permission denied: secret");
            await Send(stream, "!done");
            await NoMoreCommands(stream);
        }, timeout.Token);
        await using var session = await Connect(port, timeout.Token);
        using var audit = new TestJournal();
        var result = await new RouterSessionDisconnect(audit.Journal).DisconnectAsync(
            session, Selected, confirmed: true, timeout.Token);
        Assert.False(result.Success);
        Assert.False(result.Uncertain);
        Assert.Equal("router_rejected", result.Reason);
        Assert.DoesNotContain("secret", result.Message);
        Assert.Equal("rejected", Assert.Single(audit.Journal.ReadRecent()).State);
        await session.DisposeAsync();
        await peer;
    }

    [Fact]
    public async Task Invalid_target_never_requests_even_a_read()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await Handshake(stream, timeout.Token);
            await NoMoreCommands(stream);
        }, timeout.Token);
        await using var session = await Connect(port, timeout.Token);
        using var audit = new TestJournal();
        var result = await new RouterSessionDisconnect(audit.Journal).DisconnectAsync(
            session, Selected with { Id = "invalid", Service = "Other" },
            confirmed: true, timeout.Token);
        Assert.False(result.Success);
        Assert.Equal("invalid_selection", result.Reason);
        Assert.Empty(audit.Journal.ReadRecent());
        await session.DisposeAsync();
        await peer;
    }
}
