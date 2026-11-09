using System.Net;
using System.Net.Sockets;
using System.Text;
using HadramoutNET.Core.Data;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class StandardVoucherReconciliationTests
{
    private sealed class TempDb : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "hnet-standard-review-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_root, "hadramout-net.db");
        public TempDb() => Directory.CreateDirectory(_root);
        public void Dispose() => Directory.Delete(_root, true);
    }
    private static readonly DateTimeOffset Now =
        new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static async Task Reply(NetworkStream stream, params string[] words) =>
        await stream.WriteAsync(RouterOsSentenceCodec.Encode(words));

    private static async Task<RouterAuthorizedSession> Login(int port, CancellationToken ct)
    {
        var coordinator = RouterLoginCoordinator.CreateForTests(
            () => new LicenseAccess("active", "valid", "online_valid",
                "0123456789ABCDEF0123456789ABCDEF"),
            () => new TrialAccess("locked", "trial_expired", "expired", 0),
            () => new TrialAccess("locked", "trial_expired", "expired", 0));
        var login = await coordinator.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "tester", "secret"),
            Now, ct);
        Assert.True(login.Success);
        return login.Session!;
    }

    private static async Task Serve(TcpListener listener, CancellationToken ct,
        Func<NetworkStream, Task> run)
    {
        using var peer = await listener.AcceptTcpClientAsync(ct);
        using var stream = peer.GetStream();
        Assert.Contains("/login", await RouterOsSentenceCodec.ReadAsync(stream, ct));
        await Reply(stream, "!done");
        Assert.Equal("/system/identity/print",
            Assert.Single(await RouterOsSentenceCodec.ReadAsync(stream, ct)));
        await Reply(stream, "!re", "=name=Router");
        await Reply(stream, "!done");
        await run(stream);
    }

    [Theory]
    [InlineData("hotspot", "/ip/hotspot/user/print")]
    [InlineData("ppp", "/ppp/secret/print")]
    public async Task Restarted_batch_reads_live_accounts_and_saves_only_counts_without_remote_writes(
        string service, string path)
    {
        using var data = new TempDb();
        var journal = new VoucherDeliveryJournal(data.Path);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string endpoint = $"127.0.0.1:{port}";
        long id = journal.Begin("Router", endpoint, service, "weekly", 6);
        journal.MarkDispatching(id);
        journal.Complete(id, "uncertain", "delivery_uncertain", 0);
        journal = new VoucherDeliveryJournal(data.Path); // Simulated app restart.

        var server = Serve(listener, deadline.Token, async stream =>
        {
            var count = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains(path, count);
            Assert.Contains("=count-only=yes", count);
            await Reply(stream, "!done", "=ret=5");
            var projected = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains(path, projected);
            Assert.Contains(service == "ppp" ?
                "=.proplist=name,profile,disabled,service" :
                "=.proplist=name,profile,disabled", projected);
            Assert.DoesNotContain(projected, word => word.Contains("password",
                StringComparison.OrdinalIgnoreCase));

            await Reply(stream, "!re", "=name=one", "=profile=weekly",
                "=disabled=no", "=service=pppoe");
            await Reply(stream, "!re", "=name=two", "=profile=other",
                "=disabled=no", "=service=pppoe");
            await Reply(stream, "!re", "=name=three", "=profile=weekly",
                "=disabled=yes", "=service=pppoe");
            await Reply(stream, "!re", "=name=four", "=disabled=no",
                "=service=pppoe");
            await Reply(stream, "!re", "=name=five", "=profile=weekly",
                "=disabled=no", "=service=sstp");
            await Reply(stream, "!done");
            byte[] extra = new byte[1];
            using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try { Assert.Equal(0, await stream.ReadAsync(extra, quiet.Token)); }
            catch (OperationCanceledException) { }
        });
        await using var session = await Login(port, deadline.Token);
        var result = await new VoucherDeliveryReconciler(journal).ReconcileStandardAsync(
            session, id, ["one", "two", "three", "four", "five", "missing"], deadline.Token);
        Assert.True(result.Available);
        Assert.Equal(service, result.Service);
        Assert.NotNull(result.Check);
        Assert.Equal(6, result.Check!.Counts.Values.Sum());
        Assert.Equal(service == "ppp" ? 1 : 2, result.Check.Counts["profile_matches"]);
        Assert.Equal(1, result.Check.Counts["profile_other"]);
        Assert.Equal(1, result.Check.Counts["disabled"]);
        Assert.Equal(1, result.Check.Counts["profile_unknown"]);
        Assert.Equal(1, result.Check.Counts["not_found"]);
        if (service == "ppp")
            Assert.Equal(1, result.Check.Counts["service_other"]);
        await session.DisposeAsync();
        await server;
        var restarted = new VoucherDeliveryJournal(data.Path);
        Assert.Equal("uncertain", restarted.GetAttempt(id)!.State);
        var saved = Assert.Single(restarted.ReadStandardChecks(id));
        Assert.Equal(service, saved.Service);
        Assert.Equal(6, saved.Counts.Values.Sum());
        string raw = Encoding.UTF8.GetString(File.ReadAllBytes(data.Path));
        Assert.DoesNotContain("one", raw);
        Assert.DoesNotContain("missing", raw);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Incomplete_hotspot_result_is_unavailable_and_cannot_record_missing_card()
    {
        using var data = new TempDb();
        var journal = new VoucherDeliveryJournal(data.Path);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        long id = journal.Begin("Router", $"127.0.0.1:{port}", "hotspot", "weekly", 2);
        journal.MarkDispatching(id);
        var server = Serve(listener, deadline.Token, async stream =>
        {
            var count = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("=count-only=yes", count);
            await Reply(stream, "!done", "=ret=2");
            var entries = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("=.proplist=name,profile,disabled", entries);
            await Reply(stream, "!re", "=name=one", "=profile=weekly", "=disabled=no");
            await Reply(stream, "!done"); // Count mismatch, never infer missing users.
        });
        await using var session = await Login(port, deadline.Token);
        var result = await new VoucherDeliveryReconciler(journal)
            .ReconcileStandardAsync(session, id, ["one", "other"], deadline.Token);
        Assert.False(result.Available);
        Assert.Equal("incomplete", result.Status);
        Assert.Empty(journal.ReadStandardChecks(id));
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public void Standard_checks_reject_wrong_router_service_batch_count_and_unapproved_states()
    {
        using var data = new TempDb();
        var journal = new VoucherDeliveryJournal(data.Path);
        long h = journal.Begin("Router", "10.0.0.1:8728", "hotspot", "weekly", 2);
        journal.MarkDispatching(h);
        long p = journal.Begin("Router", "10.0.0.1:8728", "ppp", "weekly", 2);
        journal.MarkDispatching(p);
        long done = journal.Begin("Router", "10.0.0.1:8728", "hotspot", "weekly", 2);
        journal.MarkDispatching(done);
        journal.Complete(done, "accepted", "published", 2);
        var counts = new Dictionary<string, int> {
            ["profile_matches"] = 1, ["not_found"] = 1
        };
        Assert.Throws<InvalidOperationException>(() =>
            journal.AppendStandardVerification(h, "Other", "10.0.0.1:8728",
                "hotspot", counts));
        Assert.Throws<InvalidOperationException>(() =>
            journal.AppendStandardVerification(h, "Router", "10.0.0.1:8728",
                "ppp", counts));
        Assert.Throws<InvalidOperationException>(() =>
            journal.AppendStandardVerification(done, "Router", "10.0.0.1:8728",
                "hotspot", counts));
        Assert.Throws<InvalidOperationException>(() =>
            journal.AppendStandardVerification(h, "Router", "10.0.0.1:8728",
                "hotspot", new Dictionary<string, int> { ["not_found"] = 1 }));
        Assert.Throws<ArgumentException>(() =>
            journal.AppendStandardVerification(h, "Router", "10.0.0.1:8728",
                "hotspot", new Dictionary<string, int> { ["password=secret"] = 2 }));
        Assert.Throws<ArgumentException>(() =>
            journal.AppendStandardVerification(h, "Router", "10.0.0.1:8728",
                "hotspot", new Dictionary<string, int> { ["service_other"] = 2 }));
        var first = journal.AppendStandardVerification(h, "Router",
            "10.0.0.1:8728", "hotspot", counts);
        var second = journal.AppendStandardVerification(h, "Router",
            "10.0.0.1:8728", "hotspot",
            new Dictionary<string, int> { ["not_found"] = 2 });
        Assert.True(second.Id > first.Id);
        Assert.Equal(new[] { second.Id, first.Id },
            new VoucherDeliveryJournal(data.Path).ReadStandardChecks(h)
                .Select(x => x.Id));
        Assert.Equal("dispatching", journal.GetAttempt(h)!.State);
        Assert.Empty(journal.ReadStandardChecks(p));
    }

    [Fact]
    public async Task Wrong_operator_username_count_and_wrong_router_fail_before_router_read()
    {
        using var data = new TempDb();
        var journal = new VoucherDeliveryJournal(data.Path);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Serve(listener, deadline.Token, async stream =>
        {
            byte[] bytes = new byte[1];
            using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            try { Assert.Equal(0, await stream.ReadAsync(bytes, quiet.Token)); }
            catch (OperationCanceledException) { }
        });
        await using var session = await Login(port, deadline.Token);
        long otherRouter = journal.Begin("other", session.Endpoint, "hotspot", "weekly", 2);
        journal.MarkDispatching(otherRouter);
        long correct = journal.Begin(session.Identity, session.Endpoint, "hotspot", "weekly", 2);
        journal.MarkDispatching(correct);
        var recon = new VoucherDeliveryReconciler(journal);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await recon.ReconcileStandardAsync(session, otherRouter,
                ["one", "two"], deadline.Token));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await recon.ReconcileStandardAsync(session, correct,
                ["one"], deadline.Token));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await recon.ReconcileStandardAsync(session, correct,
                ["one", "one"], deadline.Token));
        Assert.Empty(journal.ReadStandardChecks(correct));
        await session.DisposeAsync();
        await server;
    }
}
