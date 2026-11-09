using System.IO;
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
        catch (RouterOsTrapException)
        {
            return Denied("preflight_unavailable",
                "رفض الراوتر فحص الجلسة؛ لم يُرسل أمر فصل.");
        }

        if (current.Rows.Count != 1)
            return Denied("stale_selection",
                "اختفت الجلسة أو تغيّرت؛ حدّث القائمة قبل أي إجراء.");

        var row = current.Rows[0];
        string id = Field(row, ".id");
        string user = FirstNonEmpty(Field(row, "user"), Field(row, "name"));
        string address = Field(row, "address");
        if (!string.Equals(id, selected.Id, StringComparison.Ordinal) ||
            !string.Equals(user, selected.Username, StringComparison.Ordinal) ||
            !string.Equals(address, selected.Address, StringComparison.Ordinal))
            return Denied("stale_selection",
                "لم تعد هوية الجلسة مطابقة لعرضها السابق؛ لم تُفصل.");

        if (!session.HasCurrentAccess)
            return Denied("authorization_expired",
                "انتهت الصلاحية قبل الإرسال؛ لم يُنفذ الفصل.");

        // Anything after entering this call may have reached the router.
        // A timeout, closed socket or cancelled wait is therefore UNCERTAIN.
        // Never retry or substitute a username-based removal.
        try
        {
            await session.ExecuteMaintenanceDisconnectAsync(
                removePath, selected.Id, cancellationToken).ConfigureAwait(false);
            return new SessionDisconnectResult(true, false, "accepted",
                "قبل الراوتر طلب فصل جلسة واحدة. حدّث القائمة للتحقق من انتهائها.");
        }
        catch (RouterOsTrapException)
        {
            return Denied("router_rejected",
                "رفض الراوتر فصل الجلسة؛ لم تُعد المحاولة.");
        }
        catch (Exception)
        {
            return new SessionDisconnectResult(false, true, "delivery_uncertain",
                "قد يكون أمر الفصل وصل للراوتر قبل انقطاع الاستجابة. لا تعِد الإرسال؛ حدّث القائمة وتحقق يدويا.");
        }
    }

    private static SessionDisconnectResult Denied(string reason, string message) =>
        new(false, false, reason, message);

    private static string Field(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out string? value) ? value ?? "" : "";

    private static string FirstNonEmpty(string first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : first;
}
