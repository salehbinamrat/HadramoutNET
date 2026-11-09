using HadramoutNET.Core.Data;
using HadramoutNET.Core.RouterOS;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class SessionMaintenanceJournalTests
{
    private sealed class TempDb : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hnet-maintenance-" + Guid.NewGuid().ToString("N"));
        public string PathName => Path.Combine(Root, "hadramout-net.db");
        public TempDb() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static readonly NetworkActiveSession Selected =
        new("*A", "Hotspot", "00123", "10.0.0.3");

    [Fact]
    public void Journal_survives_restart_and_never_contains_passwords_or_raw_router_errors()
    {
        using var fixture = new TempDb();
        var journal = new SessionMaintenanceJournal(fixture.PathName);
        long id = journal.Begin("Router", "10.0.0.1:8728", Selected);
        Assert.Equal("started", Assert.Single(journal.ReadRecent()).State);

        journal.MarkDispatching(id);
        Assert.Equal("dispatching", Assert.Single(new SessionMaintenanceJournal(fixture.PathName).ReadRecent()).State);
        journal.Complete(id, "rejected", "router_rejected");

        var row = Assert.Single(new SessionMaintenanceJournal(fixture.PathName).ReadRecent());
        Assert.Equal(id, row.Id);
        Assert.Equal("rejected", row.State);
        Assert.Equal("router_rejected", row.Outcome);
        Assert.Equal("00123", row.Username);
        Assert.Equal("10.0.0.3", row.Address);
        Assert.Equal("*A", row.RecordId);
        Assert.DoesNotContain("password", row.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", row.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Router", row.RouterIdentity);
        Assert.Equal("10.0.0.1:8728", row.RouterEndpoint);
    }

    [Fact]
    public void Interrupted_dispatch_remains_visible_after_restart_as_unresolved_and_never_autocompletes()
    {
        using var fixture = new TempDb();
        var journal = new SessionMaintenanceJournal(fixture.PathName);
        long id = journal.Begin("Router", "127.0.0.1:8728", Selected);
        journal.MarkDispatching(id);
        var reopened = new SessionMaintenanceJournal(fixture.PathName);
        var unresolved = Assert.Single(reopened.ReadRecent());
        Assert.Equal(id, unresolved.Id);
        Assert.Equal("dispatching", unresolved.State);
        Assert.True(unresolved.RequiresManualReconciliation);
        Assert.Throws<InvalidOperationException>(() => reopened.MarkDispatching(id));
        Assert.Equal("dispatching", Assert.Single(reopened.ReadRecent()).State);
    }

    [Fact]
    public void Illegal_status_transitions_and_duplicate_completion_are_rejected_without_mutation()
    {
        using var fixture = new TempDb();
        var journal = new SessionMaintenanceJournal(fixture.PathName);
        long id = journal.Begin("Router", "127.0.0.1:8728", Selected);
        Assert.Throws<InvalidOperationException>(() => journal.Complete(id, "accepted", "router_ack"));
        Assert.Throws<ArgumentException>(() => journal.Complete(id, "invented", "invalid_code"));
        journal.Complete(id, "rejected", "stale_selection");
        Assert.Throws<InvalidOperationException>(() => journal.MarkDispatching(id));
        Assert.Throws<InvalidOperationException>(() => journal.Complete(id, "accepted", "router_ack"));
        Assert.Equal("rejected", Assert.Single(journal.ReadRecent()).State);
    }

    [Fact]
    public void Journal_is_separate_from_original_python_database()
    {
        using var fixture = new TempDb();
        string python = Path.Combine(fixture.Root, "nidi_cards.db");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = python,
            Pooling = false
        }.ToString()))
        {
            db.Open();
            using var query = db.CreateCommand();
            query.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT); INSERT INTO meta VALUES('schema_version','8');";
            query.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() => new SessionMaintenanceJournal(python));
        using (var check = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = python,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()))
        {
            check.Open();
            using var command = check.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='maintenance_audit'";
            Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
        }
    }

    [Fact]
    public void Recent_entries_are_ordered_and_bounded_and_include_all_outcome_types()
    {
        using var fixture = new TempDb();
        var journal = new SessionMaintenanceJournal(fixture.PathName);
        long first = journal.Begin("Router", "endpoint", Selected);
        journal.Complete(first, "rejected", "stale_selection");
        long second = journal.Begin("Router", "endpoint", Selected with { Username = "002" });
        journal.MarkDispatching(second);
        journal.Complete(second, "accepted", "router_ack");
        long third = journal.Begin("Router", "endpoint", Selected with { Username = "003" });
        journal.MarkDispatching(third);
        journal.Complete(third, "uncertain", "delivery_uncertain");

        var page = journal.ReadRecent(2);
        Assert.Equal(2, page.Count);
        Assert.Equal(new[] { "uncertain", "accepted" }, page.Select(x => x.State));
        Assert.True(page[0].RequiresManualReconciliation);
        Assert.False(page[1].RequiresManualReconciliation);
        Assert.Throws<ArgumentOutOfRangeException>(() => journal.ReadRecent(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => journal.ReadRecent(501));
    }

    [Fact]
    public void Invalid_identity_is_refused_before_creating_any_audit_entry()
    {
        using var fixture = new TempDb();
        var journal = new SessionMaintenanceJournal(fixture.PathName);
        Assert.Throws<ArgumentException>(() => journal.Begin(
            "Router", "endpoint", Selected with { Id = "invalid" }));
        Assert.Throws<ArgumentException>(() => journal.Begin(
            "Router", "endpoint", Selected with { Service = "All" }));
        Assert.Empty(journal.ReadRecent());
    }
}
