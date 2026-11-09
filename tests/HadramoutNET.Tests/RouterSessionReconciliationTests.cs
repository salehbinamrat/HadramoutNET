using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Data;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RouterSessionReconciliationTests
{
    private sealed class LocalAudit : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(),
            "hnet-reconcile-" + Guid.NewGuid().ToString("N"));
        public string PathName => Path.Combine(_dir, "hadramout-net.db");
        public SessionMaintenanceJournal Journal { get; }
        public LocalAudit()
        {
            Directory.CreateDirectory(_dir);
            Journal = new SessionMaintenanceJournal(PathName);
        }
        public void Dispose() => Directory.Delete(_dir, recursive: true);
    }

    private static readonly DateTimeOffset Now =
        new(2026, 11, 9, 13, 0, 0, TimeSpan.Zero);
    private static LicenseAccess Paid() =>
        new("active", "valid", "online_valid", "0123456789ABCDEF0123456789ABCDEF");
    private static TrialAccess Expired() =>
        new("locked", "trial_expired", "expired", 0);
    private static Task Send(NetworkStream stream, params string[] words) =>
        stream.WriteAsync(RouterOsSentenceCodec.Encode(words)).AsTask();
    private static readonly NetworkActiveSession Selected =
        new("*A", "Hotspot", "voucher-001", "10.0.0.12");

    private static async Task<RouterAuthorizedSession> Connect(int port, CancellationToken token)
    {
        var result = await RouterLoginCoordinator.CreateForTests(Paid, Expired, Expired)
            .LoginAsync(new RouterLoginRequest("127.0.0.1",
                port.ToString(), "admin", "sensitive"), Now, token);
        Assert.True(result.Success);
        return result.Session!;
    }

    private static async Task Handshake(NetworkStream s, CancellationToken ct)
    {
        Assert.Contains("/login", await RouterOsSentenceCodec.ReadAsync(s, ct));
        await Send(s, "!done");
        Assert.Contains("/system/identity/print", await RouterOsSentenceCodec.ReadAsync(s, ct));
        await Send(s, "!re", "=name=TestRouter");
        await Send(s, "!done");
    }

    private static async Task NoMoreCommands(NetworkStream stream)
    {
        var b = new byte[1];
        using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try { Assert.Equal(0, await stream.ReadAsync(b, wait.Token)); }
        catch (OperationCanceledException) { }
    }

    [Theory]
    [InlineData("absent", "observed_absent")]
    [InlineData("present", "observed_present")]
    [InlineData("changed", "observed_changed")]
    public async Task Read_only_reconciliation_records_an_observation_without_changing_original_audit(
        string response, string expected)
    {
        using var audit = new LocalAudit();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = connection.GetStream();
            await Handshake(stream, timeout.Token);
            var command = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Equal("/ip/hotspot/active/print", command[0]);
            Assert.Contains("?.id=*A", command);
            Assert.Contains("=.proplist=.id,user,name,address", command);
            Assert.DoesNotContain(command, w => w.Contains("remove", StringComparison.OrdinalIgnoreCase));
            if (response == "present")
                await Send(stream, "!re", "=.id=*A", "=user=voucher-001",
                    "=address=10.0.0.12", "=password=NOT_FOR_AUDIT");
            if (response == "changed")
                await Send(stream, "!re", "=.id=*A", "=user=someone-else",
                    "=address=10.0.0.12", "=password=NOT_FOR_AUDIT");
            await Send(stream, "!done");
            await NoMoreCommands(stream);
        }, timeout.Token);

        await using var session = await Connect(port, timeout.Token);
        long id = audit.Journal.Begin(session.Identity, session.Endpoint, Selected);
        audit.Journal.MarkDispatching(id);
        audit.Journal.Complete(id, "uncertain", "delivery_uncertain");

        var observed = await new RouterSessionReconciler(audit.Journal)
            .CheckAsync(session, id, timeout.Token);
        Assert.True(observed.Checked);
        Assert.Equal(expected, observed.Observation);
        await peer;

        var reopened = new SessionMaintenanceJournal(audit.PathName);
        var entry = Assert.Single(reopened.ReadRecent());
        Assert.Equal("uncertain", entry.State);
        Assert.Equal("delivery_uncertain", entry.Outcome);
        Assert.Equal(expected, entry.LastObservation);
        Assert.True(entry.RequiresManualReconciliation);
        Assert.DoesNotContain("NOT_FOR_AUDIT", entry.ToString());
        var history = reopened.ReadChecks(id);
        Assert.Single(history);
        Assert.Equal(expected, history[0].Observation);
    }

    [Fact]
    public async Task Wrong_router_identity_or_endpoint_blocks_even_the_read()
    {
        using var audit = new LocalAudit();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = connection.GetStream();
            await Handshake(stream, timeout.Token);
            await NoMoreCommands(stream);
        }, timeout.Token);

        await using var session = await Connect(port, timeout.Token);
        long id = audit.Journal.Begin("SomeOtherRouter", session.Endpoint, Selected);
        long wrongEndpoint = audit.Journal.Begin(
            session.Identity, "different.example:8728", Selected);
        var checker = new RouterSessionReconciler(audit.Journal);
        var check = await checker.CheckAsync(session, id, timeout.Token);
        var second = await checker.CheckAsync(session, wrongEndpoint, timeout.Token);
        Assert.False(check.Checked);
        Assert.Equal("router_mismatch", check.Observation);
        Assert.False(second.Checked);
        Assert.Equal("router_mismatch", second.Observation);
        Assert.Empty(audit.Journal.ReadChecks(id));
        Assert.Empty(audit.Journal.ReadChecks(wrongEndpoint));
        await session.DisposeAsync();
        await peer;
    }

    [Fact]
    public async Task Router_denied_read_is_explicitly_unavailable_and_does_not_erase_prior_audit()
    {
        using var audit = new LocalAudit();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = connection.GetStream();
            await Handshake(stream, timeout.Token);
            Assert.Contains("/ppp/active/print",
                await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token));
            await Send(stream, "!trap", "=message=permission denied password=LEAK");
            await Send(stream, "!done");
            await NoMoreCommands(stream);
        }, timeout.Token);
        await using var session = await Connect(port, timeout.Token);
        var target = Selected with { Service = "PPPoE" };
        long id = audit.Journal.Begin(session.Identity, session.Endpoint, target);
        audit.Journal.MarkDispatching(id);
        var check = await new RouterSessionReconciler(audit.Journal)
            .CheckAsync(session, id, timeout.Token);
        Assert.True(check.Checked);
        Assert.Equal("unavailable", check.Observation);
        Assert.DoesNotContain("LEAK", check.Message);
        Assert.Equal("dispatching", Assert.Single(audit.Journal.ReadRecent()).State);
        Assert.Equal("unavailable", Assert.Single(audit.Journal.ReadChecks(id)).Observation);
        await session.DisposeAsync();
        await peer;
    }

    [Fact]
    public void Rechecks_are_append_only_and_original_attempt_cannot_be_rewritten()
    {
        using var audit = new LocalAudit();
        long id = audit.Journal.Begin("Router", "localhost:8728", Selected);
        audit.Journal.MarkDispatching(id);
        audit.Journal.Complete(id, "accepted", "router_ack");
        audit.Journal.AppendCheck(id, "observed_present");
        audit.Journal.AppendCheck(id, "observed_absent");
        Assert.Equal(new[] { "observed_absent", "observed_present" },
            audit.Journal.ReadChecks(id).Select(c => c.Observation));
        var entry = Assert.Single(new SessionMaintenanceJournal(audit.PathName).ReadRecent());
        Assert.Equal("accepted", entry.State);
        Assert.Equal("observed_absent", entry.LastObservation);
        Assert.Throws<ArgumentException>(() => audit.Journal.AppendCheck(id, "confirmed_disconnected"));
        Assert.Throws<InvalidOperationException>(() =>
            audit.Journal.Complete(id, "rejected", "router_rejected"));
        Assert.Throws<InvalidOperationException>(() => audit.Journal.AppendCheck(9999, "observed_absent"));
        Assert.Equal(2, audit.Journal.ReadChecks(id).Count);
    }

    [Fact]
    public async Task Missing_audit_record_or_expired_session_causes_no_read_or_write()
    {
        using var audit = new LocalAudit();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = connection.GetStream();
            await Handshake(stream, timeout.Token);
            await NoMoreCommands(stream);
        }, timeout.Token);
        var session = await Connect(port, timeout.Token);
        var reconcile = new RouterSessionReconciler(audit.Journal);
        var notFound = await reconcile.CheckAsync(session, 9999, timeout.Token);
        Assert.False(notFound.Checked);
        Assert.Equal("not_found", notFound.Observation);
        await session.DisposeAsync();
        var expired = await reconcile.CheckAsync(session, 9999, timeout.Token);
        Assert.False(expired.Checked);
        Assert.Equal("authorization_expired", expired.Observation);
        await peer;
    }
}
