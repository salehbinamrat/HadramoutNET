using System.Globalization;
using System.Text.RegularExpressions;
using HadramoutNET.Core.RouterOS;
using Microsoft.Data.Sqlite;

namespace HadramoutNET.Core.Data;

/// <summary>
/// Durable local audit metadata, NOT a router password, license state or
/// guarantee that a remote disconnect was actually completed.
/// </summary>
public sealed record SessionMaintenanceAuditEntry(
    long Id, string CreatedAtUtc, string RouterIdentity,
    string RouterEndpoint, string Service, string RecordId,
    string Username, string Address, string State, string Outcome,
    string LastObservation = "not_checked", string LastObservedUtc = "")
{
    /// <summary>Lost ACK or process termination requires human inspection.</summary>
    public bool RequiresManualReconciliation =>
        State is "started" or "dispatching" or "uncertain";

    public string LastObservationLabel => LastObservation switch
    {
        "observed_absent" => "الجلسة لم تعد ظاهرة — السبب غير مؤكد",
        "observed_present" => "الجلسة ما زالت متصلة",
        "observed_changed" => "تغيرت هوية الجلسة أو معرفها",
        "unavailable" => "تعذرت قراءة الحالة",
        _ => "لم يُجرَ تحقق بعد"
    };

    public string StateLabel => State switch
    {
        "started" => "بدأت ولم تكتمل",
        "dispatching" => "قد يكون الأمر أُرسل",
        "accepted" => "قبل الراوتر الطلب",
        "rejected" => "مرفوض أو لم يُرسل",
        "uncertain" => "نتيجة غير مؤكدة",
        _ => "حالة غير معروفة"
    };
}

public sealed record SessionAuditCheck(
    long Id, long AttemptId, string ObservedUtc, string Observation);

/// <summary>
/// Append-only attempt identities with restrictive atomic state transitions.
/// This is a dedicated table in the independent HadramoutNET SQLite database.
/// It never opens or mutates Python's nidi_cards.db or license stores.
///
/// Every dispatch is recorded durably BEFORE calling the remote API. A crash
/// may leave a row in "dispatching"; it must remain visible and is NEVER
/// automatically retried or treated as confirmed completed.
/// </summary>
public sealed class SessionMaintenanceJournal
{
    private static readonly Regex RecordIdPattern = new(
        @"^\*[0-9A-Fa-f]+$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AllowedOutcomes = new(StringComparer.Ordinal)
    {
        "preflight_unavailable", "stale_selection", "authorization_expired",
        "router_rejected", "router_ack", "delivery_uncertain",
        "audit_unavailable", "internal_error"
    };

    private readonly string _path;

    public SessionMaintenanceJournal(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        _path = Path.GetFullPath(targetPath);

        // Reuse the already validated separate .NET database and refuse
        // an original Python DB before attempting any write.
        _ = new PackageSettingsStore(_path);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS maintenance_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc TEXT NOT NULL,
                finished_utc TEXT,
                router_identity TEXT NOT NULL,
                router_endpoint TEXT NOT NULL,
                service TEXT NOT NULL,
                record_id TEXT NOT NULL,
                username TEXT NOT NULL,
                address TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN
                    ('started','dispatching','accepted','rejected','uncertain')),
                outcome TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_maintenance_audit_recent
                ON maintenance_audit(id DESC);
            CREATE TABLE IF NOT EXISTS maintenance_reconciliation (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                attempt_id INTEGER NOT NULL REFERENCES maintenance_audit(id),
                observed_utc TEXT NOT NULL,
                observation TEXT NOT NULL CHECK(observation IN
                    ('observed_absent','observed_present','observed_changed','unavailable'))
            );
            CREATE INDEX IF NOT EXISTS ix_maintenance_reconciliation_attempt
                ON maintenance_reconciliation(attempt_id,id DESC);
            """;
        command.ExecuteNonQuery();
    }

    public long Begin(string routerIdentity, string routerEndpoint,
        NetworkActiveSession selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (!Valid(routerIdentity, 160) || !Valid(routerEndpoint, 256) ||
            selected.Service is not ("Hotspot" or "PPPoE") ||
            string.IsNullOrWhiteSpace(selected.Id) ||
            !RecordIdPattern.IsMatch(selected.Id) ||
            !Valid(selected.Username, 90) || !Valid(selected.Address, 80))
            throw new ArgumentException("هوية الراوتر أو الجلسة غير صالحة للتدقيق.");

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO maintenance_audit(
                created_utc,router_identity,router_endpoint,
                service,record_id,username,address,state,outcome)
            VALUES ($when,$router,$endpoint,$service,$id,$user,$address,'started','');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$when", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$router", routerIdentity);
        command.Parameters.AddWithValue("$endpoint", routerEndpoint);
        command.Parameters.AddWithValue("$service", selected.Service);
        command.Parameters.AddWithValue("$id", selected.Id);
        command.Parameters.AddWithValue("$user", selected.Username);
        command.Parameters.AddWithValue("$address", selected.Address);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void MarkDispatching(long id)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE maintenance_audit SET state='dispatching'
            WHERE id=$id AND state='started'
            """;
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("سجل التدقيق لم يعد في حالة ما قبل الإرسال.");
    }

    /// <summary>
    /// Only rejected is allowed from "started", since no write was dispatched.
    /// All remote outcomes are allowed only after persistent "dispatching".
    /// A terminal entry can never be rewritten, deleted, or replayed here.
    /// </summary>
    public void Complete(long id, string state, string outcome)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        if (state is not ("accepted" or "rejected" or "uncertain"))
            throw new ArgumentException("حالة التدقيق غير مسموحة.", nameof(state));
        if (!AllowedOutcomes.Contains(outcome))
            throw new ArgumentException("رمز نتيجة التدقيق غير مسموح.", nameof(outcome));

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE maintenance_audit
            SET state=$state,outcome=$outcome,finished_utc=$when
            WHERE id=$id AND
            ((state='started' AND $state='rejected') OR state='dispatching')
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$when", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("لا يمكن إكمال سجل تدقيق منتهي أو في حالة خاطئة.");
    }

    /// <summary>
    /// Append a factual read observation without changing the original
    /// disconnect's accepted/rejected/uncertain outcome. No automatic replay.
    /// </summary>
    public void AppendCheck(long attemptId, string observation)
    {
        if (attemptId <= 0) throw new ArgumentOutOfRangeException(nameof(attemptId));
        if (observation is not ("observed_absent" or "observed_present" or
            "observed_changed" or "unavailable"))
            throw new ArgumentException("رمز الملاحظة غير مصرح به.", nameof(observation));

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO maintenance_reconciliation(attempt_id,observed_utc,observation)
            SELECT id,$time,$observation FROM maintenance_audit WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", attemptId);
        command.Parameters.AddWithValue("$time",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$observation", observation);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("سجل العملية المحدد غير موجود.");
    }

    public IReadOnlyList<SessionAuditCheck> ReadChecks(long attemptId, int limit = 20)
    {
        if (attemptId <= 0) throw new ArgumentOutOfRangeException(nameof(attemptId));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = Open(readOnly: true);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,attempt_id,observed_utc,observation
            FROM maintenance_reconciliation
            WHERE attempt_id=$id ORDER BY id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$id", attemptId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<SessionAuditCheck>();
        while (reader.Read())
            result.Add(new SessionAuditCheck(reader.GetInt64(0),reader.GetInt64(1),
                reader.GetString(2),reader.GetString(3)));
        return result;
    }

    private const string EntrySelection = """
        SELECT a.id,a.created_utc,a.router_identity,a.router_endpoint,
               a.service,a.record_id,a.username,a.address,a.state,a.outcome,
               COALESCE((SELECT x.observation FROM maintenance_reconciliation x
                   WHERE x.attempt_id=a.id ORDER BY x.id DESC LIMIT 1),'not_checked'),
               COALESCE((SELECT x.observed_utc FROM maintenance_reconciliation x
                   WHERE x.attempt_id=a.id ORDER BY x.id DESC LIMIT 1),'')
        FROM maintenance_audit a
        """;

    public SessionMaintenanceAuditEntry? GetAttempt(long attemptId)
    {
        if (attemptId <= 0) return null;
        using var db = Open(readOnly: true);
        using var command = db.CreateCommand();
        command.CommandText = EntrySelection + " WHERE a.id=$id";
        command.Parameters.AddWithValue("$id", attemptId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    /// <summary>
    /// Bounded read. Each row includes only the latest observation; individual
    /// reconciliation events remain append-only in the separate table.
    /// </summary>
    public IReadOnlyList<SessionMaintenanceAuditEntry> ReadRecent(int limit = 100)
    {
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = Open(readOnly: true);
        using var command = db.CreateCommand();
        command.CommandText = EntrySelection + " ORDER BY a.id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var entries = new List<SessionMaintenanceAuditEntry>();
        while (reader.Read()) entries.Add(ReadEntry(reader));
        return entries;
    }

    private static SessionMaintenanceAuditEntry ReadEntry(SqliteDataReader reader) =>
        new(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),
            reader.GetString(3),reader.GetString(4),reader.GetString(5),
            reader.GetString(6),reader.GetString(7),reader.GetString(8),
            reader.GetString(9),reader.GetString(10),reader.GetString(11));

    private SqliteConnection Open(bool readOnly = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            DefaultTimeout = 10
        }.ToString());
        db.Open();
        return db;
    }

    private static bool Valid(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        !value.Any(char.IsControl);
}
