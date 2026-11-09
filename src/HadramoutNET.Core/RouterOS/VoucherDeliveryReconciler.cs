using HadramoutNET.Core.Data;

namespace HadramoutNET.Core.RouterOS;

public sealed record VoucherDeliveryReconciliationResult(
    string Status, int Version, VoucherDeliveryCheck? Check)
{
    public bool Available => Status == "recorded";
}

public sealed record StandardVoucherReconciliationResult(
    string Status, string Service, VoucherDeliveryStandardCheck? Check)
{
    public bool Available => Status == "recorded";
}

/// <summary>
/// Explicit, read-only review of ONE historical voucher batch. The
/// operator supplies exact usernames from a separate, trusted export.
/// No usernames or passwords enter the permanent journal; no remote
/// add/set/remove/retry command exists on this path.
/// </summary>
public sealed class VoucherDeliveryReconciler(VoucherDeliveryJournal journal)
{
    private readonly VoucherDeliveryJournal _journal =
        journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly UserManagerVoucherVerifier _verifier = new();
    private readonly RouterStandardVoucherVerifier _standardVerifier = new();

    public async Task<VoucherDeliveryReconciliationResult> ReconcileAsync(
        RouterAuthorizedSession session, long attemptId,
        IReadOnlyList<string> operatorUsernames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(operatorUsernames);

        // Verify the immutable LOCAL identity and batch size BEFORE touching
        // RouterOS. Otherwise one router's results could be attributed to
        // another router or an unrelated upload attempt.
        VoucherDeliveryAuditEntry attempt = _journal.GetAttempt(attemptId) ??
            throw new InvalidOperationException("محاولة رفع الكروت غير موجودة.");

        if (attempt.RouterIdentity != session.Identity ||
            attempt.RouterEndpoint != session.Endpoint ||
            attempt.Service != "userman" ||
            !attempt.RequiresManualReconciliation)
            throw new InvalidOperationException(
                "هذه المحاولة ليست غير محسومة في User Manager لهذا الراوتر.");

        if (operatorUsernames.Count != attempt.Attempted ||
            operatorUsernames.Count is < 1 or > 5000)
            throw new ArgumentException(
                "عدد الأسماء المطلوب فحصها لا يطابق عدد الكروت في سجل المحاولة.");

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string username in operatorUsernames)
        {
            if (string.IsNullOrWhiteSpace(username) || username.Length > 64 ||
                username.Any(char.IsControl) ||
                username.Any(char.IsWhiteSpace) ||
                !unique.Add(username))
                throw new ArgumentException(
                    "يجب إدخال أسماء مستخدمين غير مكررة دون مسافات أو كلمات مرور.");
        }

        UserManagerVoucherVerificationResult observation =
            await _verifier.VerifyAsync(session, operatorUsernames,
                attempt.Profile, cancellationToken).ConfigureAwait(false);

        if (!observation.Available)
            return new VoucherDeliveryReconciliationResult(
                observation.Status, observation.Version, null);

        // Persist only allowlisted state labels and counts. The username
        // projection remains in memory and is not serialised or logged.
        var counts = observation.Cards
            .GroupBy(item => item.State, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(),
                StringComparer.Ordinal);

        VoucherDeliveryCheck check = _journal.AppendVerification(
            attemptId, session.Identity, session.Endpoint,
            observation.Version, counts);
        return new VoucherDeliveryReconciliationResult("recorded",
            observation.Version, check);
    }

    /// <summary>
    /// Read-only reconciliation for historical Hotspot or PPPoE deliveries.
    /// The supplied usernames come from a separate trusted operator export,
    /// never from SQLite; only aggregate counts are written to the journal.
    /// </summary>
    public async Task<StandardVoucherReconciliationResult> ReconcileStandardAsync(
        RouterAuthorizedSession session, long attemptId,
        IReadOnlyList<string> operatorUsernames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(operatorUsernames);

        VoucherDeliveryAuditEntry attempt = _journal.GetAttempt(attemptId) ??
            throw new InvalidOperationException("محاولة رفع الكروت غير موجودة.");
        if (attempt.RouterIdentity != session.Identity ||
            attempt.RouterEndpoint != session.Endpoint ||
            attempt.Service is not ("hotspot" or "ppp") ||
            !attempt.RequiresManualReconciliation)
            throw new InvalidOperationException(
                "هذه المحاولة ليست غير محسومة في Hotspot أو PPP لهذا الراوتر.");

        if (operatorUsernames.Count != attempt.Attempted ||
            operatorUsernames.Count is < 1 or > RouterStandardVoucherVerifier.MaxRows)
            throw new ArgumentException(
                "يجب إدخال نفس عدد أسماء الكروت المسجل في المحاولة.");

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in operatorUsernames)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64 ||
                name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
                !unique.Add(name))
                throw new ArgumentException(
                    "الأسماء غير صالحة أو مكررة أو تحتوي مسافات.");
        }

        StandardVoucherVerificationResult state = await _standardVerifier.VerifyAsync(
            session, attempt.Service, operatorUsernames, attempt.Profile,
            cancellationToken).ConfigureAwait(false);
        if (!state.Available)
            return new StandardVoucherReconciliationResult(
                state.Status, attempt.Service, null);

        var counts = state.Cards
            .GroupBy(card => card.State, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(),
                StringComparer.Ordinal);
        VoucherDeliveryStandardCheck saved = _journal.AppendStandardVerification(
            attemptId, session.Identity, session.Endpoint, attempt.Service, counts);
        return new StandardVoucherReconciliationResult(
            "recorded", attempt.Service, saved);
    }
}
