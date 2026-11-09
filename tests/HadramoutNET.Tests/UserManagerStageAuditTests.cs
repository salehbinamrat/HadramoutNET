using System.Net;
using System.Net.Sockets;
using System.Text;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Data;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>Exercises real RouterOS TCP wires and durable SQLite boundaries.</summary>
public sealed class UserManagerStageAuditTests
{
    private sealed class TempDb : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "hnet-um-stage-" + Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(Root, "hadramout-net.db");
        public TempDb() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static readonly Voucher[] Cards =
        [new("1", "example-username-secret", "example-password-secret")];

    private static async Task Reply(NetworkStream stream, params string[] words) =>
        await stream.WriteAsync(RouterOsSentenceCodec.Encode(words));

    private static async Task<RouterAuthorizedSession> Login(int port, CancellationToken token)
    {
        var auth = RouterLoginCoordinator.CreateForTests(
            () => new LicenseAccess("active", "ok", "online_valid",
                "0123456789ABCDEF0123456789ABCDEF"),
            () => new TrialAccess("locked", "trial_expired", "expired", 0),
            () => new TrialAccess("locked", "trial_expired", "expired", 0));
        var result = await auth.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "tester", "pwd"),
            new DateTimeOffset(2026, 11, 9, 15, 0, 0, TimeSpan.Zero), token);
        Assert.True(result.Success);
        return result.Session!;
    }

    private static async Task Peer(TcpListener listener, CancellationToken token,
        Func<NetworkStream, Task> serve)
    {
        using var socket = await listener.AcceptTcpClientAsync(token);
        using var stream = socket.GetStream();
        Assert.Contains("/login", await RouterOsSentenceCodec.ReadAsync(stream, token));
        await Reply(stream, "!done");
        Assert.Equal("/system/identity/print",
            Assert.Single(await RouterOsSentenceCodec.ReadAsync(stream, token)));
        await Reply(stream, "!re", "=name=Router");
        await Reply(stream, "!done");
        await serve(stream);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task Each_um_write_has_persisted_prepare_before_wire_and_ack_before_next_write(int version)
    {
        using var temp = new TempDb();
        var journal = new VoucherDeliveryJournal(temp.FilePath);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        string endpoint = $"127.0.0.1:{port}";
        var server = Peer(listener, deadline.Token, async stream =>
        {
            var profiles = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/user-manager/profile/print", profiles);
            if (version == 6)
            {
                await Reply(stream, "!trap", "=message=no such command");
                await Reply(stream, "!done");
                Assert.Contains("/tool/user-manager/profile/print",
                    await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            }
            await Reply(stream, "!re", "=name=weekly",
                version == 6 ? "=owner=admin" : "=placeholder=x");
            await Reply(stream, "!done");
            Assert.Contains(version == 6 ? "/tool/user-manager/user/print" :
                "/user-manager/user/print",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!done");

            var add = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains(version == 6 ? "/tool/user-manager/user/add" :
                "/user-manager/user/add", add);
            var step1 = Assert.Single(new VoucherDeliveryJournal(temp.FilePath)
                .ReadUserManagerStages(1));
            Assert.Equal("create_user", step1.Stage);
            Assert.Equal("prepared", step1.State);
            Assert.Equal(1, step1.Ordinal);
            Assert.Equal(version, step1.Version);
            await Reply(stream, "!done", "=ret=*A");

            var bind = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains(version == 6 ?
                "/tool/user-manager/user/create-and-activate-profile" :
                "/user-manager/user-profile/add", bind);
            var steps = new VoucherDeliveryJournal(temp.FilePath)
                .ReadUserManagerStages(1);
            Assert.Equal(2, steps.Count);
            Assert.Equal("bind_profile", steps[0].Stage);
            Assert.Equal("prepared", steps[0].State);
            Assert.Equal("create_user", steps[1].Stage);
            Assert.Equal("acknowledged", steps[1].State);
            await Reply(stream, "!done");
        });
        await using var session = await Login(port, deadline.Token);
        var result = await new VoucherDeliveryCoordinator(journal)
            .PublishAsync(session, Cards, "userman", "weekly", "",
                confirmed: true, deadline.Token);
        Assert.True(result.Success);
        await session.DisposeAsync();
        await server;
        var reopened = new VoucherDeliveryJournal(temp.FilePath);
        Assert.Equal("accepted", reopened.GetAttempt(1)!.State);
        var saved = reopened.ReadUserManagerStages(1);
        Assert.Equal(new[] { "bind_profile", "create_user" },
            saved.Select(s => s.Stage));
        Assert.All(saved, step => Assert.Equal("acknowledged", step.State));
        string raw = Encoding.UTF8.GetString(File.ReadAllBytes(temp.FilePath));
        Assert.DoesNotContain("example-username-secret", raw);
        Assert.DoesNotContain("example-password-secret", raw);
    }

    [Fact]
    public async Task Bind_trap_leaves_only_created_account_acknowledged_and_binding_unconfirmed()
    {
        using var temp = new TempDb();
        var journal = new VoucherDeliveryJournal(temp.FilePath);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = Peer(listener, deadline.Token, async stream =>
        {
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!re", "=name=weekly");
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!done");
            Assert.Contains("/user-manager/user/add",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!done", "=ret=*A");
            Assert.Contains("/user-manager/user-profile/add",
                await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token));
            await Reply(stream, "!trap", "=message=profile rejected");
            await Reply(stream, "!done");
        });
        await using var session = await Login(port, deadline.Token);
        var result = await new VoucherDeliveryCoordinator(journal)
            .PublishAsync(session, Cards, "userman", "weekly", "",
                confirmed: true, deadline.Token);
        Assert.False(result.Success);
        Assert.True(result.Uncertain);
        Assert.Equal("profile_assignment_uncertain", result.Reason);
        await session.DisposeAsync();
        await server;
        var reopened = new VoucherDeliveryJournal(temp.FilePath);
        Assert.Equal("uncertain", reopened.GetAttempt(1)!.State);
        var stages = reopened.ReadUserManagerStages(1);
        Assert.Equal(new[] { "prepared", "acknowledged" },
            stages.Select(step => step.State));
        Assert.False(stages[0].CanAssumeCompleted);
    }

    [Fact]
    public void Stage_sequence_is_immutable_scoped_and_requires_previous_ack()
    {
        using var temp = new TempDb();
        var journal = new VoucherDeliveryJournal(temp.FilePath);
        long attempt = journal.Begin("Router", "10.0.0.1:8728", "userman", "weekly", 2);
        journal.MarkDispatching(attempt);
        long create = journal.PrepareUserManagerStage(attempt, 1, 6, "create_user");
        Assert.Throws<InvalidOperationException>(() =>
            journal.PrepareUserManagerStage(attempt, 1, 6, "create_user"));
        Assert.Throws<InvalidOperationException>(() =>
            journal.PrepareUserManagerStage(attempt, 1, 6, "bind_profile"));
        Assert.Throws<InvalidOperationException>(() =>
            journal.PrepareUserManagerStage(attempt, 2, 6, "create_user"));
        journal.AcknowledgeUserManagerStage(create);
        Assert.Throws<InvalidOperationException>(() => journal.AcknowledgeUserManagerStage(create));
        long bind = journal.PrepareUserManagerStage(attempt, 1, 6, "bind_profile");
        Assert.Throws<InvalidOperationException>(() =>
            journal.PrepareUserManagerStage(attempt, 2, 6, "create_user"));
        journal.AcknowledgeUserManagerStage(bind);
        long next = journal.PrepareUserManagerStage(attempt, 2, 6, "create_user");
        Assert.Equal("prepared", journal.ReadUserManagerStages(attempt)[0].State);
        Assert.Throws<InvalidOperationException>(() =>
            journal.PrepareUserManagerStage(attempt, 2, 7, "bind_profile"));
        Assert.Throws<InvalidOperationException>(() =>
            journal.AcknowledgeUserManagerStage(-5));
        Assert.True(next > bind);
        long hotspot = journal.Begin("Router", "10.0.0.1:8728", "hotspot", "weekly", 1);
        journal.MarkDispatching(hotspot);
        Assert.Throws<InvalidOperationException>(() =>
            journal.PrepareUserManagerStage(hotspot, 1, 6, "create_user"));
        Assert.Throws<ArgumentException>(() =>
            journal.PrepareUserManagerStage(attempt, 2, 6, "password=SECRET"));
        Assert.Equal(3, new VoucherDeliveryJournal(temp.FilePath)
            .ReadUserManagerStages(attempt).Count);
    }

    [Fact]
    public async Task Missing_stage_audit_storage_blocks_add_even_after_preflight()
    {
        using var temp = new TempDb();
        var journal = new VoucherDeliveryJournal(temp.FilePath);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = temp.FilePath, Pooling = false
        }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE voucher_delivery_um_stages";
            cmd.ExecuteNonQuery();
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = Peer(listener, deadline.Token, async stream =>
        {
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!re", "=name=weekly");
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!done");
            byte[] extra = new byte[1];
            using var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(450));
            try { Assert.Equal(0, await stream.ReadAsync(extra, shortWait.Token)); }
            catch (OperationCanceledException) { }
        });
        await using var session = await Login(port, deadline.Token);
        var result = await new VoucherDeliveryCoordinator(journal).PublishAsync(
            session, Cards, "userman", "weekly", "", confirmed: true, deadline.Token);
        Assert.False(result.Success);
        Assert.True(result.Uncertain);
        await session.DisposeAsync();
        await server;
        Assert.Equal("uncertain", journal.GetAttempt(1)!.State);
    }
}
