using System.Text.RegularExpressions;
using HadramoutNET.Core.Cards;

namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// User Manager 6 needs customer + new returned .id, then a distinct
/// create-and-activate-profile call. UM7 needs a user then a user-profile
/// association. Both are non-transactional across the network.
/// </summary>
public sealed record UserManagerPublishOptions(
    string Profile, bool Confirmed, string Customer = "");

/// <summary>
/// Optional durable stage recorder, supplied by the WPF delivery coordinator.
/// When supplied, the publisher MUST prepare before a remote mutation and
/// acknowledge after a successful RouterOS command response. Never retry.
/// </summary>
public interface IUserManagerStageAudit
{
    long Prepare(int ordinal, int version, string stage);
    void Acknowledge(long stageId);
}

/// <summary>
/// Explicit, safe two-phase UM6/UM7 voucher creation. Preflight reads only
/// projected names/profiles, refuses duplicates and unknown owners, then
/// sends each write at most once. Never automatically retries or deletes an
/// account whose profile attachment may have been partially delivered.
/// </summary>
public sealed class RouterUserManagerPublisher
{
    private static readonly Regex ReturnedId =
        new(@"^\*[0-9A-Fa-f]+$", RegexOptions.CultureInvariant);

    public async Task<VoucherPublishReport> PublishAsync(
        RouterAuthorizedSession session, IReadOnlyList<Voucher> vouchers,
        UserManagerPublishOptions options, CancellationToken cancellationToken = default,
        IUserManagerStageAudit? stageAudit = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(vouchers);
        ArgumentNullException.ThrowIfNull(options);

        int count = vouchers.Count;
        if (!options.Confirmed)
            return Fail("confirmation_required", "يلزم تأكيد الرفع يدويا.", count);
        if (!session.HasCurrentAccess)
            return Fail("not_authorized", "الجلسة غير مصرح لها بالرفع.", count);
        if (count is < 1 or > VoucherGenerator.MaxBatchSize)
            return Fail("invalid_count", "عدد الكروت غير صالح.", count);

        string profile = (options.Profile ?? "").Trim();
        string suppliedCustomer = (options.Customer ?? "").Trim();
        if (profile.Length is < 1 or > 80 || profile.Any(char.IsControl) ||
            suppliedCustomer.Length > 80 || suppliedCustomer.Any(char.IsControl))
            return Fail("invalid_options", "اسم الباقة أو مالك User Manager غير صالح.", count);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Voucher card in vouchers)
        {
            if (string.IsNullOrWhiteSpace(card.Username) ||
                card.Username.Length > 64 ||
                card.Username.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
                card.Password.Length > 64 || card.Password.Any(char.IsControl) ||
                !names.Add(card.Username))
                return Fail("invalid_cards", "كروت مكررة أو بيانات دخول غير صالحة.", count);
        }

        UserManagerCatalogResult catalog;
        try
        {
            catalog = await new UserManagerCatalog().ReadAsync(session, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or
            RouterOsTrapException or InvalidOperationException or OperationCanceledException)
        {
            return Fail("preflight_unavailable", "تعذر التحقق من بيانات User Manager، ولم يبدأ الرفع.", count);
        }

        if (!catalog.UsersComplete || catalog.Version is not (6 or 7))
            return Fail("preflight_unavailable",
                "لا يمكن التحقق من جميع مستخدمي User Manager وباقاته؛ لم يبدأ الرفع.", count);
        UserManagerProfileRow? chosen = catalog.Profiles.FirstOrDefault(
            row => string.Equals(row.Name, profile, StringComparison.Ordinal));
        if (chosen is null)
            return Fail("profile_missing", "الباقة غير موجودة على الراوتر.", count);
        if (catalog.Users.Any(user => names.Contains(user.Username)))
            return Fail("duplicate_username", "رقم مستخدم موجود مسبقا؛ لم يبدأ الرفع.", count);

        string owner = "";
        if (catalog.Version == 6)
        {
            owner = suppliedCustomer.Length != 0 ? suppliedCustomer : chosen.Owner;
            if (string.IsNullOrWhiteSpace(owner))
                return Fail("customer_required",
                    "يجب تحديد حساب مالك User Manager v6 قبل إنشاء الكروت.", count);
            if (!string.IsNullOrEmpty(chosen.Owner) &&
                !string.Equals(owner, chosen.Owner, StringComparison.Ordinal))
                return Fail("customer_mismatch",
                    "مالك الباقة لا يطابق الحساب المحدد؛ لم يبدأ الرفع.", count);
        }
        else if (suppliedCustomer.Length != 0)
            return Fail("invalid_options",
                "إصدار User Manager 7 لا يستخدم حساب المالك في هذا المسار.", count);

        string addPath = catalog.Version == 7 ?
            "/user-manager/user/add" : "/tool/user-manager/user/add";
        string bindPath = catalog.Version == 7 ?
            "/user-manager/user-profile/add" :
            "/tool/user-manager/user/create-and-activate-profile";

        var successes = new List<string>();
        int ordinal = 0;
        foreach (Voucher card in vouchers)
        {
            ordinal++;
            var addAttributes = new Dictionary<string, string>
            {
                ["password"] = card.Password
            };
            if (catalog.Version == 7)
                addAttributes["name"] = card.Username;
            else
            {
                addAttributes["username"] = card.Username;
                addAttributes["customer"] = owner;
            }

            RouterOsCommandResult created;
            try
            {
                long stageId = stageAudit?.Prepare(ordinal, catalog.Version, "create_user") ?? 0;
                created = await session.ExecuteUserManagerProvisionAsync(
                    addPath, addAttributes, cancellationToken).ConfigureAwait(false);
                if (stageAudit is not null) stageAudit.Acknowledge(stageId);
            }
            catch (RouterOsTrapException)
            {
                // Rejection is confirmed, but previous cards may be live.
                return Failed("partial_failure",
                    "رفض الراوتر إنشاء المستخدم، وتوقف الرفع دون إعادة محاولة.",
                    count, successes, uncertain: false);
            }
            catch (Exception ex) when (ex is IOException or
                InvalidOperationException or OperationCanceledException or
                Microsoft.Data.Sqlite.SqliteException)
            {
                return Failed("delivery_uncertain",
                    "تعذر تأكيد إنشاء أحد المستخدمين؛ راجع User Manager قبل أي إعادة.",
                    count, successes, uncertain: true);
            }

            var binding = new Dictionary<string, string> { ["profile"] = profile };
            if (catalog.Version == 7)
                binding["user"] = card.Username;
            else
            {
                if (!created.Done.TryGetValue("ret", out string? newId) ||
                    !ReturnedId.IsMatch(newId ?? ""))
                    return Failed("delivery_uncertain",
                        "أُقر إنشاء المستخدم دون إرجاع معرّف صالح؛ لم يُرسل ربط الباقة.",
                        count, successes, uncertain: true);
                binding["numbers"] = newId!; // Already validated as a single *HEX RouterOS ID.
                binding["customer"] = owner;
            }

            try
            {
                long stageId = stageAudit?.Prepare(ordinal, catalog.Version, "bind_profile") ?? 0;
                await session.ExecuteUserManagerProvisionAsync(
                    bindPath, binding, cancellationToken).ConfigureAwait(false);
                if (stageAudit is not null) stageAudit.Acknowledge(stageId);
                successes.Add(card.Username);
            }
            catch (Exception ex) when (ex is RouterOsTrapException or IOException
                or InvalidOperationException or OperationCanceledException or
                Microsoft.Data.Sqlite.SqliteException)
            {
                // Account creation succeeded but profile attachment is NOT
                // confirmed: no retry, compensating delete, or misleading
                // "success" count. Operator must reconcile the remote user.
                return Failed("profile_assignment_uncertain",
                    "قد يكون المستخدم أُنشئ دون تفعيل الباقة؛ راجع User Manager قبل إعادة الإرسال.",
                    count, successes, uncertain: true);
            }
        }

        return new VoucherPublishReport(true, "published",
            "اكتمل إنشاء المستخدمين وربط الباقات بعد التحقق.", count,
            successes.Count, false, successes.ToArray());
    }

    private static VoucherPublishReport Fail(
        string reason, string message, int attempted) =>
        new(false, reason, message, attempted, 0, false, []);

    private static VoucherPublishReport Failed(
        string reason, string message, int attempted,
        List<string> added, bool uncertain) =>
        new(false, reason, message, attempted, added.Count,
            uncertain, added.ToArray());
}
