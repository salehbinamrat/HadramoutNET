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

public sealed class VoucherDeliveryJournalTests
{
    private sealed class TempDb : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hnet-delivery-" + Guid.NewGuid().ToString("N"));
        public string PathName => Path.Combine(Root, "hadramout-net.db");
        public TempDb() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static readonly Voucher[] Cards = [new("0001", "voucher001", "SECRET_NOT_FOR_JOURNAL")];

    [Fact]
    public void Incomplete_attempt_survives_restart_and_is_never_replayed_or_marked_success()
    {
        using var tmp = new TempDb();
        var writer = new VoucherDeliveryJournal(tmp.PathName);
        long id = writer.Begin("Router", "10.0.0.1:8728", "hotspot", "default", 1);
        Assert.Equal("started", Assert.Single(writer.ReadRecent()).State);
        writer.MarkDispatching(id);
        var reopened = new VoucherDeliveryJournal(tmp.PathName);
        var record = Assert.Single(reopened.ReadRecent());
        Assert.Equal("dispatching", record.State);
        Assert.True(record.RequiresManualReconciliation);
        Assert.Equal(0, record.Added);
        Assert.Throws<InvalidOperationException>(() => reopened.MarkDispatching(id));
        Assert.Equal("dispatching", Assert.Single(reopened.ReadRecent()).State);
    }

    [Fact]
    public void Terminal_states_are_immutable_and_usernames_passwords_and_router_errors_are_not_saved()
    {
        using var tmp = new TempDb();
        var journal = new VoucherDeliveryJournal(tmp.PathName);
        long id = journal.Begin("Router", "10.0.0.1:8728", "userman", "daily", 2);
        Assert.Throws<InvalidOperationException>(() => journal.Complete(id, "accepted", "published", 2));
        journal.MarkDispatching(id);
        Assert.Throws<ArgumentException>(() =>
            journal.Complete(id, "uncertain", "failed: password=SECRET_NOT_FOR_JOURNAL", 0));
        journal.Complete(id, "partial", "partial_failure", 1);
        Assert.Throws<InvalidOperationException>(() => journal.Complete(id, "accepted", "published", 2));
        var row = Assert.Single(new VoucherDeliveryJournal(tmp.PathName).ReadRecent());
        Assert.Equal("partial", row.State);
        Assert.Equal("partial_failure", row.Reason);
        Assert.True(row.RequiresManualReconciliation);
        Assert.Equal(1, row.Added);
        Assert.DoesNotContain("SECRET_NOT_FOR_JOURNAL", Encoding.UTF8.GetString(File.ReadAllBytes(tmp.PathName)));
        Assert.DoesNotContain("voucher001", Encoding.UTF8.GetString(File.ReadAllBytes(tmp.PathName)));
    }

    [Fact]
    public void Rejects_original_python_database_and_invalid_metadata_before_writing()
    {
        using var tmp = new TempDb();
        string original = Path.Combine(tmp.Root, "nidi_cards.db");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = original, Pooling = false
        }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT);";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new VoucherDeliveryJournal(original));
        var journal = new VoucherDeliveryJournal(tmp.PathName);
        Assert.Throws<ArgumentException>(() => journal.Begin("Router", "10.0.0.1:8728",
            "invalid", "default", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => journal.Begin("Router",
            "10.0.0.1:8728", "hotspot", "default", 10001));
        Assert.Empty(journal.ReadRecent());
    }

    private static async Task Send(NetworkStream stream, params string[] words) =>
        await stream.WriteAsync(RouterOsSentenceCodec.Encode(words));

    [Fact]
    public async Task Real_router_wire_publication_is_audited_before_write_without_persisting_credentials()
    {
        using var tmp = new TempDb();
        var journal = new VoucherDeliveryJournal(tmp.PathName);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token); // login
            await Send(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token); // identity
            await Send(stream, "!re", "=name=Router");
            await Send(stream, "!done");
            var users = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ip/hotspot/user/print", users);
            Assert.Equal("dispatching", Assert.Single(new VoucherDeliveryJournal(tmp.PathName).ReadRecent()).State);
            await Send(stream, "!done");
            var profiles = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ip/hotspot/user/profile/print", profiles);
            await Send(stream, "!re", "=name=default");
            await Send(stream, "!done");
            var add = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/ip/hotspot/user/add", add);
            Assert.Contains("=name=voucher001", add);
            await Send(stream, "!done", "=ret=*1");
        }, deadline.Token);
        var auth = RouterLoginCoordinator.CreateForTests(
            () => new LicenseAccess("active", "ok", "online_valid", "0123456789ABCDEF0123456789ABCDEF"),
            () => new TrialAccess("locked", "trial_expired", "expired", 0),
            () => new TrialAccess("locked", "trial_expired", "expired", 0));
        var login = await auth.LoginAsync(new RouterLoginRequest(
            "127.0.0.1", port.ToString(), "tester", "pwd"),
            new DateTimeOffset(2026, 11, 9, 11, 0, 0, TimeSpan.Zero), deadline.Token);
        Assert.True(login.Success);
        await using var session = login.Session!;
        var coordinator = new VoucherDeliveryCoordinator(journal);
        var result = await coordinator.PublishAsync(session, Cards, "hotspot", "default",
            "", confirmed: true, deadline.Token);
        Assert.True(result.Success);
        await server;
        var completed = Assert.Single(new VoucherDeliveryJournal(tmp.PathName).ReadRecent());
        Assert.Equal("accepted", completed.State);
        Assert.Equal(1, completed.Added);
        Assert.False(completed.RequiresManualReconciliation);
        Assert.DoesNotContain("SECRET_NOT_FOR_JOURNAL", Encoding.UTF8.GetString(File.ReadAllBytes(tmp.PathName)));
    }
}
