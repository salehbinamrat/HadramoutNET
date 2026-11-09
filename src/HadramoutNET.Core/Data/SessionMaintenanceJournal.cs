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
    string Username, string Address, string State, string Outcome)
{
    /// <summary>Lost ACK or process termination requires human inspection.</summary>
    public bool RequiresManualReconciliation =>
        State is "started" or "dispatching" or "uncertain";

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

    /// <summary>Bounded read only; does not return credentials or raw exceptions.</summary>
    public IReadOnlyList<SessionMaintenanceAuditEntry> ReadRecent(int limit = 100)
    {
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = Open(readOnly: true);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,created_utc,router_identity,router_endpoint,
                   service,record_id,username,address,state,outcome
            FROM maintenance_audit ORDER BY id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var entries = new List<SessionMaintenanceAuditEntry>();
        while (reader.Read())
        {
            entries.Add(new SessionMaintenanceAuditEntry(
                reader.GetInt64(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9)));
        }
        return entries;
    }

    private SqliteConnection Open(bool readOnly = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 10
        }.ToString());
        db.Open();
        return db;
    }

    private static bool Valid(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        !value.Any(char.IsControl);
}
