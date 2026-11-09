using System.Net;
using System.Net.Sockets;
using System.Text;
using HadramoutNET.Core.Data;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class VoucherDeliveryReconciliationTests
{
    private sealed class TempDb : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "hnet-reconcile-" + Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(Root, "hadramout-net.db");
        public TempDb() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }

    private static readonly DateTimeOffset Now =
        new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static async Task Reply(NetworkStream stream, params string[] words) =>
        await stream.WriteAsync(RouterOsSentenceCodec.Encode(words));

    private static async Task<RouterAuthorizedSession> Connect(int port, CancellationToken ct)
    {
        var coordinator = RouterLoginCoordinator.CreateForTests(
            () => new LicenseAccess("active", "valid", "online_valid",
                "0123456789ABCDEF0123456789ABCDEF"),
            () => new TrialAccess("locked", "trial_expired", "expired", 0),
            () => new TrialAccess("locked", "trial_expired", "expired", 0));
        var result = await coordinator.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "tester", "password"),
            Now, ct);
        Assert.True(result.Success);
        return result.Session!;
    }

    private static async Task Peer(TcpListener listener, CancellationToken ct,
        Func<NetworkStream, Task> handle)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        using var stream = client.GetStream();
        Assert.Contains("/login", await RouterOsSentenceCodec.ReadAsync(stream, ct));
        await Reply(stream, "!done");
        Assert.Equal("/system/identity/print",
            Assert.Single(await RouterOsSentenceCodec.ReadAsync(stream, ct)));
        await Reply(stream, "!re", "=name=Router");
        await Reply(stream, "!done");
        await handle(stream);
    }

    [Fact]
    public async Task Restarted_uncertain_um6_batch_can_be_observed_and_persisted_without_secrets_or_replay()
    {
        using var data = new TempDb();
        var journal = new VoucherDeliveryJournal(data.FilePath);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string endpoint = $"127.0.0.1:{port}";
        long attempt = journal.Begin("Router", endpoint, "userman", "daily", 2);
        journal.MarkDispatching(attempt); // Simulated crash before recording remote result.
        journal = new VoucherDeliveryJournal(data.FilePath);

        var server = Peer(listener, deadline.Token, async stream =>
        {
            Assert.Contains("/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!trap", "=message=no such command");
            await Reply(stream, "!done");
            Assert.Contains("/tool/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!re", "=name=daily", "=owner=admin");
            await Reply(stream, "!done");
            var users = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/tool/user-manager/user/print", users);
            Assert.DoesNotContain(users, x => x.Contains("password", StringComparison.OrdinalIgnoreCase));
            await Reply(stream, "!re", "=username=card-abc", "=actual-profile=daily", "=disabled=no");
            await Reply(stream, "!done");
            var count = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("=count-only=yes", count);
            await Reply(stream, "!done", "=ret=1");
            byte[] next = new byte[1];
            using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try { Assert.Equal(0, await stream.ReadAsync(next, quiet.Token)); }
            catch (OperationCanceledException) { }
        });
        await using var session = await Connect(port, deadline.Token);
        var result = await new VoucherDeliveryReconciler(journal).ReconcileAsync(
            session, attempt, ["card-abc", "card-other"], deadline.Token);
        Assert.Equal("recorded", result.Status);
        Assert.True(result.Available);
        Assert.Equal(6, result.Version);
        Assert.NotNull(result.Check);
        Assert.Equal(1, result.Check!.Counts["profile_reported"]);
        Assert.Equal(1, result.Check.Counts["not_found"]);
        await session.DisposeAsync();
        await server;

        var restarted = new VoucherDeliveryJournal(data.FilePath);
        var original = Assert.Single(restarted.ReadUnresolved("Router", endpoint));
        Assert.Equal(attempt, original.Id);
        Assert.Equal("dispatching", original.State); // Historical outcome NOT rewritten.
        var persisted = Assert.Single(restarted.ReadChecks(attempt));
        Assert.Equal(result.Check.Id, persisted.Id);
        Assert.Equal(6, persisted.Version);
        Assert.Equal(2, persisted.Counts.Values.Sum());
        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(data.FilePath));
        Assert.DoesNotContain("card-abc", raw);
        Assert.DoesNotContain("card-other", raw);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Journal_only_allows_append_only_aggregate_checks_for_exact_unresolved_router_and_service()
    {
        using var data = new TempDb();
        var journal = new VoucherDeliveryJournal(data.FilePath);
        long uncertain = journal.Begin("Router", "10.0.0.1:8728", "userman", "daily", 2);
        journal.MarkDispatching(uncertain);
        journal.Complete(uncertain, "uncertain", "delivery_uncertain", 0);
        long accepted = journal.Begin("Router", "10.0.0.1:8728", "userman", "daily", 1);
        journal.MarkDispatching(accepted);
        journal.Complete(accepted, "accepted", "published", 1);
        long hotspot = journal.Begin("Router", "10.0.0.1:8728", "hotspot", "default", 2);
        journal.MarkDispatching(hotspot);

        var summary = new Dictionary<string, int> {
            ["profile_active"] = 1, ["not_found"] = 1
        };
        Assert.Throws<InvalidOperationException>(() => journal.AppendVerification(
            uncertain, "Another Router", "10.0.0.1:8728", 7, summary));
        Assert.Throws<InvalidOperationException>(() => journal.AppendVerification(
            uncertain, "Router", "10.0.0.2:8728", 7, summary));
        Assert.Throws<InvalidOperationException>(() => journal.AppendVerification(
            accepted, "Router", "10.0.0.1:8728", 7, new Dictionary<string, int> {
                ["profile_active"] = 1
            }));
        Assert.Throws<InvalidOperationException>(() => journal.AppendVerification(
            hotspot, "Router", "10.0.0.1:8728", 7, summary));
        Assert.Throws<ArgumentException>(() => journal.AppendVerification(
            uncertain, "Router", "10.0.0.1:8728", 7, new Dictionary<string, int> {
                ["profile_active"] = 1
            }));
        Assert.Throws<ArgumentException>(() => journal.AppendVerification(
            uncertain, "Router", "10.0.0.1:8728", 7, new Dictionary<string, int> {
                ["password=SECRET"] = 2
            }));
        Assert.Throws<ArgumentException>(() => journal.AppendVerification(
            uncertain, "Router", "10.0.0.1:8728", 6, summary));

        var saved = journal.AppendVerification(
            uncertain, "Router", "10.0.0.1:8728", 7, summary);
        Assert.Equal(2, saved.Counts.Values.Sum());
        var second = journal.AppendVerification(
            uncertain, "Router", "10.0.0.1:8728", 7,
            new Dictionary<string, int> { ["profile_used"] = 2 });
        Assert.True(second.Id > saved.Id);
        var checks = new VoucherDeliveryJournal(data.FilePath).ReadChecks(uncertain);
        Assert.Equal(new[] { second.Id, saved.Id }, checks.Select(x => x.Id));
        Assert.Equal(2, checks[0].Counts["profile_used"]);
        Assert.Equal("uncertain", journal.GetAttempt(uncertain)!.State);
        Assert.Empty(journal.ReadChecks(hotspot));
    }

    [Fact]
    public async Task Incorrect_operator_batch_and_router_identity_are_refused_before_any_remote_read()
    {
        using var data = new TempDb();
        var journal = new VoucherDeliveryJournal(data.FilePath);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Peer(listener, deadline.Token, async stream =>
        {
            byte[] next = new byte[1];
            using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(650));
            try { Assert.Equal(0, await stream.ReadAsync(next, quiet.Token)); }
            catch (OperationCanceledException) { }
        });
        await using var session = await Connect(port, deadline.Token);
        long wrongRouter = journal.Begin("Other", session.Endpoint,
            "userman", "daily", 2);
        journal.MarkDispatching(wrongRouter);
        long rightRouter = journal.Begin(session.Identity, session.Endpoint,
            "userman", "daily", 2);
        journal.MarkDispatching(rightRouter);
        var reconcile = new VoucherDeliveryReconciler(journal);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reconcile.ReconcileAsync(session, wrongRouter,
                ["card-abc", "card-other"], deadline.Token));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await reconcile.ReconcileAsync(session, rightRouter, ["card-abc"], deadline.Token));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await reconcile.ReconcileAsync(session, rightRouter,
                ["card-abc", "card-abc"], deadline.Token));
        Assert.Empty(journal.ReadChecks(rightRouter));
        await session.DisposeAsync();
        await server;
    }
}
