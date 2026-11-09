using System.IO;
using HadramoutNET.Core.Data;
using System.Text.RegularExpressions;

namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// The single-session maintenance action returns accepted/rejected/uncertain,
/// never an unverified claim that a remote user was actually disconnected.
/// </summary>
public sealed record SessionDisconnectResult(
    bool Success, bool Uncertain, string Reason, string Message);

/// <summary>
/// Deliberately narrow write service. It never retries remote writes, never
/// removes by username, and rejects a session if the id/name/IP changed
/// between the UI snapshot and a fresh RouterOS query.
/// </summary>
public sealed class RouterSessionDisconnect
{
    private readonly SessionMaintenanceJournal _journal;

    public RouterSessionDisconnect(SessionMaintenanceJournal journal)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    private static readonly Regex ExactId = new(
        @"^\*[0-9A-Fa-f]+$", RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string> IdentityFields =
        new Dictionary<string, string>
        {
            [".proplist"] = ".id,user,name,address"
        };

    public async Task<SessionDisconnectResult> DisconnectAsync(
        RouterAuthorizedSession session, NetworkActiveSession selected,
        bool confirmed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!confirmed)
            return Denied("confirmation_required",
                "لن تفصل جلسة دون موافقة المستخدم الصريحة.");

        if (selected is null ||
            selected.Service is not ("Hotspot" or "PPPoE") ||
            selected.Id is null || !ExactId.IsMatch(selected.Id) ||
            string.IsNullOrWhiteSpace(selected.Username) ||
            string.IsNullOrWhiteSpace(selected.Address))
            return Denied("invalid_selection",
                "يجب اختيار جلسة صالحة بمعرف ومستخدم وعنوان IP محدد.");

        if (!session.HasCurrentAccess)
            return Denied("authorization_expired",
                "انتهت صلاحية جلسة MikroTik؛ لم يُنفذ الفصل.");

        // Durably register the operator's confirmed attempt BEFORE any
        // RouterOS I/O. A local database failure blocks remote mutation.
        long auditId;
        try
        {
            auditId = _journal.Begin(session.Identity, session.Endpoint, selected);
        }
        catch (Exception)
        {
            return Denied("audit_unavailable",
                "تعذر بدء سجل التدقيق المحلي؛ لم يُرسل أي أمر إلى الراوتر.");
        }

        string printPath = selected.Service == "Hotspot"
            ? "/ip/hotspot/active/print" : "/ppp/active/print";
        string removePath = selected.Service == "Hotspot"
            ? "/ip/hotspot/active/remove" : "/ppp/active/remove";

        RouterOsCommandResult current;
        try
        {
            current = await session.ReadAsync(
                printPath, IdentityFields, queries: ["?.id=" + selected.Id],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // No remote write was attempted, so the event is rejected rather
            // than uncertain. If the audit update fails, its "started" state
            // remains visible after restart for manual reconciliation.
            return Finish(auditId, "rejected", "preflight_unavailable",
                "تعذر فحص الجلسة على الراوتر؛ لم يُرسل أمر فصل.");
        }

        if (current.Rows.Count != 1)
            return Finish(auditId, "rejected", "stale_selection",
                "اختفت الجلسة أو تغيّرت؛ حدّث القائمة قبل أي إجراء.");

        var row = current.Rows[0];
        string id = Field(row, ".id");
        string user = FirstNonEmpty(Field(row, "user"), Field(row, "name"));
        string address = Field(row, "address");
        if (!string.Equals(id, selected.Id, StringComparison.Ordinal) ||
            !string.Equals(user, selected.Username, StringComparison.Ordinal) ||
            !string.Equals(address, selected.Address, StringComparison.Ordinal))
            return Finish(auditId, "rejected", "stale_selection",
                "لم تعد هوية الجلسة مطابقة لعرضها السابق؛ لم تُفصل.");

        if (!session.HasCurrentAccess)
            return Finish(auditId, "rejected", "authorization_expired",
                "انتهت صلاحية الجلسة قبل الإرسال؛ لم يُنفذ الفصل.");

        try
        {
            // Commit the "may have been dispatched" state BEFORE making the
            // remote call. A crash or lost ACK must not erase this evidence.
            _journal.MarkDispatching(auditId);
        }
        catch (Exception)
        {
            return Denied("audit_unavailable",
                "تعذر حفظ مرحلة الإرسال في سجل التدقيق؛ لم يُرسل أمر فصل.");
        }

        // Anything after entering the remote-call boundary may have reached
        // the router. No automatic retry, delete-by-name or silent rollback.
        try
        {
            await session.ExecuteMaintenanceDisconnectAsync(
                removePath, selected.Id, cancellationToken).ConfigureAwait(false);
            return Finish(auditId, "accepted", "router_ack",
                "قبل الراوتر طلب فصل جلسة واحدة. حدّث القائمة للتحقق من انتهائها.",
                success: true);
        }
        catch (RouterOsTrapException)
        {
            return Finish(auditId, "rejected", "router_rejected",
                "رفض الراوتر فصل الجلسة؛ لم تُعد المحاولة.");
        }
        catch (Exception)
        {
            return Finish(auditId, "uncertain", "delivery_uncertain",
                "قد يكون أمر الفصل وصل للراوتر قبل انقطاع الاستجابة. لا تعِد الإرسال؛ حدّث القائمة وتحقق يدويا.",
                uncertain: true);
        }
    }

    private SessionDisconnectResult Finish(
        long auditId, string state, string reason, string message,
        bool success = false, bool uncertain = false)
    {
        try
        {
            _journal.Complete(auditId, state, reason);
            return new SessionDisconnectResult(success, uncertain,
                success ? "accepted" : reason, message);
        }
        catch (Exception)
        {
            // The durable entry remains "started" or "dispatching".
            // A remote command might already have been sent, so never retry.
            return new SessionDisconnectResult(false, true, "audit_unavailable",
                "تعذر حفظ نتيجة الفصل؛ العملية تتطلب مراجعة سجل التدقيق والجلسات يدويا دون إعادة تلقائية.");
        }
    }

    private static SessionDisconnectResult Denied(string reason, string message) =>
        new(false, false, reason, message);

    private static string Field(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out string? value) ? value ?? "" : "";

    private static string FirstNonEmpty(string first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : first;
}
