using System.Globalization;
using HadramoutNET.Core.Cards;
using Microsoft.Data.Sqlite;

namespace HadramoutNET.Core.Data;

/// <summary>
/// Metadata-only write-ahead record of a confirmed voucher upload attempt.
/// No voucher usernames, passwords, license codes or RouterOS trap text are
/// persisted. An interrupted attempt is NOT safe to replay automatically.
/// </summary>
public sealed record VoucherDeliveryAuditEntry(
    long Id, string CreatedAtUtc, string RouterIdentity, string RouterEndpoint,
    string Service, string Profile, int Attempted, int Added,
    string State, string Reason)
{
    public bool RequiresManualReconciliation =>
        State is "started" or "dispatching" or "partial" or "uncertain";

    public string StateLabel => State switch
    {
        "started" => "بدأت العملية — النتيجة غير معروفة",
        "dispatching" => "ربما أُرسلت كروت — يتطلب مراجعة",
        "accepted" => "أقر الراوتر الرفع — ليس تحققاً مستقلاً",
        "partial" => "رفع جزئي — يحتاج مراجعة",
        "uncertain" => "تعذر التأكد من نتيجة الرفع",
        "rejected" => "رفض أو فشل التحقق",
        _ => "حالة غير معروفة"
    };
}

/// <summary>
/// Append-once attempt identifiers with irreversible, narrowly allowed
/// transitions. Stored only in HadramoutNET's isolated SQLite database.
/// </summary>
public sealed class VoucherDeliveryJournal
{
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal)
    {
        "published", "confirmation_required", "not_authorized",
        "invalid_options", "invalid_count", "invalid_cards",
        "duplicate_username", "profile_missing", "preflight_unavailable",
        "customer_required", "customer_mismatch", "partial_failure",
        "delivery_uncertain", "profile_assignment_uncertain", "unexpected_failure"
    };

    private readonly string _path;

    public VoucherDeliveryJournal(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        _path = Path.GetFullPath(targetPath);
        // Refuse legacy Python schemas, including a renamed Python database.
        _ = new PackageSettingsStore(_path);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS voucher_delivery_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc TEXT NOT NULL,
                finished_utc TEXT,
                router_identity TEXT NOT NULL,
                router_endpoint TEXT NOT NULL,
                service TEXT NOT NULL,
                profile TEXT NOT NULL,
                attempted INTEGER NOT NULL CHECK(attempted >= 1 AND attempted <= 10000),
                added INTEGER NOT NULL DEFAULT 0 CHECK(added >= 0),
                state TEXT NOT NULL CHECK(state IN
                    ('started','dispatching','accepted','rejected','partial','uncertain')),
                reason TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_voucher_delivery_recent
                ON voucher_delivery_audit(id DESC);
            """;
        command.ExecuteNonQuery();
    }

    public long Begin(string routerIdentity, string routerEndpoint, string service,
        string profile, int attempted)
    {
        if (!Valid(routerIdentity, 160) || !Valid(routerEndpoint, 256) ||
            service is not ("hotspot" or "ppp" or "userman") ||
            !Valid(profile, 80))
            throw new ArgumentException("هوية الراوتر أو الخدمة أو الباقة غير صالحة للتدقيق.");
        if (attempted is < 1 or > VoucherGenerator.MaxBatchSize)
            throw new ArgumentOutOfRangeException(nameof(attempted));

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO voucher_delivery_audit(
                created_utc,router_identity,router_endpoint,service,profile,
                attempted,added,state,reason)
            VALUES ($time,$identity,$endpoint,$service,$profile,$attempted,
                0,'started','');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$time",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$identity", routerIdentity);
        command.Parameters.AddWithValue("$endpoint", routerEndpoint);
        command.Parameters.AddWithValue("$service", service);
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$attempted", attempted);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// This conservative marker is persisted BEFORE remote preflight or
    /// mutation. On a process crash it is intentionally treated as possibly
    /// dispatched, even if no write was actually sent.
    /// </summary>
    public void MarkDispatching(long id)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE voucher_delivery_audit
            SET state='dispatching' WHERE id=$id AND state='started'
            """;
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("تعذر حفظ حالة إرسال الكروت قبل الرفع.");
    }

    public void Complete(long id, string state, string reason, int added)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        if (state is not ("accepted" or "rejected" or "partial" or "uncertain"))
            throw new ArgumentException("حالة الرفع غير مسموحة.", nameof(state));
        if (string.IsNullOrEmpty(reason) || !Reasons.Contains(reason))
            throw new ArgumentException("نتيجة الرفع يجب أن تكون رمزا معتمدا دون بيانات حساسة.",
                nameof(reason));
        if (added < 0 || added > VoucherGenerator.MaxBatchSize)
            throw new ArgumentOutOfRangeException(nameof(added));

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE voucher_delivery_audit
            SET state=$state,reason=$reason,added=$added,finished_utc=$time
            WHERE id=$id AND state='dispatching' AND $added <= attempted
            AND ($state != 'accepted' OR $added = attempted)
            AND ($state != 'partial' OR ($added > 0 AND $added < attempted))
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$added", added);
        command.Parameters.AddWithValue("$time",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("لا يمكن تغيير نتيجة رفع مكتملة أو غير متطابقة.");
    }

    public IReadOnlyList<VoucherDeliveryAuditEntry> ReadRecent(int limit = 100)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = Open(readOnly: true);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,created_utc,router_identity,router_endpoint,
                   service,profile,attempted,added,state,reason
            FROM voucher_delivery_audit ORDER BY id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var rows = new List<VoucherDeliveryAuditEntry>();
        while (reader.Read())
            rows.Add(new VoucherDeliveryAuditEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetInt32(6), reader.GetInt32(7), reader.GetString(8),
                reader.GetString(9)));
        return rows;
    }

    public IReadOnlyList<VoucherDeliveryAuditEntry> ReadUnresolved(
        string routerIdentity, string routerEndpoint, int limit = 50)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = Open(readOnly: true);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,created_utc,router_identity,router_endpoint,
                   service,profile,attempted,added,state,reason
            FROM voucher_delivery_audit
            WHERE router_identity=$identity AND router_endpoint=$endpoint
              AND state IN ('started','dispatching','partial','uncertain')
            ORDER BY id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$identity", routerIdentity);
        command.Parameters.AddWithValue("$endpoint", routerEndpoint);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var rows = new List<VoucherDeliveryAuditEntry>();
        while (reader.Read())
            rows.Add(new VoucherDeliveryAuditEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetInt32(6), reader.GetInt32(7), reader.GetString(8),
                reader.GetString(9)));
        return rows;
    }

    private SqliteConnection Open(bool readOnly = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false, Cache = SqliteCacheMode.Private,
            ForeignKeys = true, DefaultTimeout = 10
        }.ToString());
        db.Open();
        return db;
    }

    private static bool Valid(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        !value.Any(char.IsControl);
}
