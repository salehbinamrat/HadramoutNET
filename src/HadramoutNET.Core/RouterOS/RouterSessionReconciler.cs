using System.Text.RegularExpressions;
using HadramoutNET.Core.Data;

namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// One read-only verification of a historic session-disconnect attempt.
/// It NEVER claims that the original remove command caused any disappearance.
/// </summary>
public sealed record SessionReconciliationResult(
    bool Checked, string Observation, string Message);

public sealed class RouterSessionReconciler
{
    private readonly SessionMaintenanceJournal _journal;
    private static readonly Regex RecordId = new(
        @"^\*[0-9A-Fa-f]+$", RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, string> IdentityFields =
        new Dictionary<string, string>
        {
            [".proplist"] = ".id,user,name,address"
        };

    public RouterSessionReconciler(SessionMaintenanceJournal journal) =>
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));

    public async Task<SessionReconciliationResult> CheckAsync(
        RouterAuthorizedSession session, long auditId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.HasCurrentAccess)
            return Denied("authorization_expired",
                "انتهت صلاحية جلسة MikroTik؛ لا يمكن التحقق من السجل.");

        SessionMaintenanceAuditEntry? attempt;
        try { attempt = _journal.GetAttempt(auditId); }
        catch (Exception)
        {
            return Denied("audit_unavailable",
                "تعذر قراءة سجل التدقيق المحلي؛ لم يُرسل أي طلب إلى الراوتر.");
        }
        if (attempt is null)
            return Denied("not_found", "السجل المحدد غير موجود.");

        // The historic record must belong to this exact authenticated router.
        // Same identity alone is insufficient to distinguish distinct routers.
        if (!string.Equals(attempt.RouterIdentity, session.Identity,
                StringComparison.Ordinal) ||
            !string.Equals(attempt.RouterEndpoint, session.Endpoint,
                StringComparison.Ordinal))
            return Denied("router_mismatch",
                "هذا السجل يخص راوترا آخر؛ افتح جلسة الراوتر الأصلي للتحقق.");

        if (attempt.Service is not ("Hotspot" or "PPPoE") ||
            !RecordId.IsMatch(attempt.RecordId) ||
            string.IsNullOrWhiteSpace(attempt.Username) ||
            string.IsNullOrWhiteSpace(attempt.Address))
            return Denied("invalid_record",
                "بيانات سجل التدقيق غير صالحة للتحقق الآمن.");

        string command = attempt.Service == "Hotspot"
            ? "/ip/hotspot/active/print" : "/ppp/active/print";
        string observation;
        try
        {
            var result = await session.ReadAsync(command, IdentityFields,
                queries: ["?.id=" + attempt.RecordId],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.Rows.Count == 0)
            {
                observation = "observed_absent";
            }
            else if (result.Rows.Count != 1)
            {
                observation = "observed_changed";
            }
            else
            {
                var row = result.Rows[0];
                string currentId = Field(row, ".id");
                string username = FirstNonEmpty(Field(row, "user"), Field(row, "name"));
                string address = Field(row, "address");
                observation = string.Equals(currentId, attempt.RecordId, StringComparison.Ordinal) &&
                    string.Equals(username, attempt.Username, StringComparison.Ordinal) &&
                    string.Equals(address, attempt.Address, StringComparison.Ordinal)
                    ? "observed_present" : "observed_changed";
            }
        }
        catch (Exception)
        {
            // A permission failure, timeout or transport problem is UNKNOWN,
            // never evidence of a disconnected session.
            observation = "unavailable";
        }

        // Persist observation separately; the historic dispatch result is
        // never modified. Failure to save the check is explicitly reported.
        try { _journal.AppendCheck(auditId, observation); }
        catch (Exception)
        {
            return Denied("audit_unavailable",
                "تعذر حفظ نتيجة التحقق محليا؛ لا تعتبر الجلسة منتهية أو متصلة بناء على هذه المحاولة.");
        }

        return new SessionReconciliationResult(true, observation,
            observation switch
            {
                "observed_absent" =>
                    "الجلسة لم تعد ظاهرة بالمعرف السابق؛ لا يمكن الجزم بأن أمر الفصل السابق هو السبب.",
                "observed_present" =>
                    "الجلسة نفسها ما زالت ظاهرة على الراوتر وقت التحقق؛ لم يُرسل أي أمر فصل جديد.",
                "observed_changed" =>
                    "المعرف لم يعد يطابق المستخدم وIP الأصليين، أو أعاد الراوتر بيانات غير متطابقة.",
                _ =>
                    "تعذر قراءة حالة الجلسة؛ لا يُفسَّر غياب البيانات بأنه فصل ناجح."
            });
    }

    private static string Field(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var value) ? value ?? "" : "";

    private static string FirstNonEmpty(string a, string b) =>
        string.IsNullOrWhiteSpace(a) ? b : a;

    private static SessionReconciliationResult Denied(string reason, string message) =>
        new(false, reason, message);
}
