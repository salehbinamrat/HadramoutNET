using HadramoutNET.Core.Cards;

namespace HadramoutNET.Core.RouterOS;

public sealed record VoucherPublishOptions(
    string Service, string Profile, bool Confirmed,
    string Server = "all");

public sealed record VoucherPublishReport(
    bool Success, string Reason, string Message,
    int Attempted, int Added, bool Uncertain,
    IReadOnlyList<string> AddedUsernames);

/// <summary>
/// Explicit, preflight-checked card delivery to a user-owned RouterOS device.
/// Not a general router shell and never automatically retries a write.
/// Partial/uncertain deliveries require operator reconciliation.
/// </summary>
public sealed class RouterVoucherPublisher
{
    public async Task<VoucherPublishReport> PublishAsync(
        RouterAuthorizedSession session, IReadOnlyList<Voucher> vouchers,
        VoucherPublishOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(vouchers);
        ArgumentNullException.ThrowIfNull(options);

        int attempted = vouchers.Count;
        if (!options.Confirmed)
            return Fail("confirmation_required",
                "يلزم تأكيد المستخدم قبل رفع الكروت إلى الراوتر.", attempted);
        if (!session.HasCurrentAccess)
            return Fail("not_authorized",
                "انتهت صلاحية الجلسة؛ لم يتم رفع الكروت.", attempted);

        if (options.Service is not ("hotspot" or "ppp") ||
            string.IsNullOrWhiteSpace(options.Profile) ||
            options.Profile.Length > 80 ||
            options.Profile.Any(char.IsControl) ||
            options.Server.Length > 128)
            return Fail("invalid_options",
                "نوع الخدمة أو اسم الباقة أو السيرفر غير صالح.", attempted);
        if (attempted is < 1 or > VoucherGenerator.MaxBatchSize)
            return Fail("invalid_count", "عدد الكروت غير صالح.", attempted);

        var usernames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Voucher card in vouchers)
        {
            if (string.IsNullOrWhiteSpace(card.Username) ||
                card.Username.Length > 64 ||
                card.Username.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
                card.Password.Length > 64 ||
                card.Password.Any(char.IsControl) ||
                !usernames.Add(card.Username))
                return Fail("invalid_cards",
                    "المسودة تحتوي أسماء مكررة أو بيانات دخول غير صالحة.",
                    attempted);
        }

        string menu = options.Service == "hotspot" ? "/ip/hotspot/user" : "/ppp/secret";
        string profileMenu = options.Service == "hotspot" ?
            "/ip/hotspot/user/profile" : "/ppp/profile";
        try
        {
            // Count-first AND projected-name checks prevent a truncated or
            // malformed RouterOS listing from falsely proving uniqueness.
            // Never retrieve user passwords or infer missing rows as absence.
            IReadOnlySet<string> existing = await ReadCompleteNamesAsync(
                session, menu + "/print", cancellationToken).ConfigureAwait(false);
            if (usernames.Overlaps(existing))
                return Fail("duplicate_username",
                    "توجد أسماء مستخدمين من المسودة على الراوتر؛ لم يبدأ الرفع.",
                    attempted);

            IReadOnlySet<string> profiles = await ReadCompleteNamesAsync(
                session, profileMenu + "/print", cancellationToken)
                .ConfigureAwait(false);
            if (!profiles.Contains(options.Profile))
                return Fail("profile_missing",
                    "لم يتم العثور على الباقة المطلوبة في الراوتر؛ لم يبدأ الرفع.",
                    attempted);
        }
        catch (RouterOsTrapException)
        {
            return Fail("preflight_unavailable",
                "تعذر التحقق من المستخدمين والباقات. لم يتم رفع أي كرت.", attempted);
        }
        catch (Exception ex) when (ex is IOException or
            InvalidOperationException or OperationCanceledException)
        {
            return Fail("preflight_unavailable",
                "انقطع الاتصال قبل مرحلة الرفع. لم يتم إرسال أي كرت.", attempted);
        }

        var added = new List<string>();
        foreach (Voucher card in vouchers)
        {
            var attributes = new Dictionary<string, string>
            {
                ["name"] = card.Username,
                ["password"] = card.Password,
                ["profile"] = options.Profile
            };
            if (options.Service == "ppp")
                attributes["service"] = "pppoe";
            else if (options.Server != "all" && options.Server.Length != 0)
                attributes["server"] = options.Server;

            try
            {
                await session.ExecuteVoucherAddAsync(menu + "/add",
                    attributes, cancellationToken).ConfigureAwait(false);
                added.Add(card.Username);
            }
            catch (RouterOsTrapException)
            {
                return FailureAfterWrites("partial_failure",
                    "رفض الراوتر إضافة أحد الكروت؛ توقف الرفع دون إعادة محاولة.",
                    attempted, added, uncertain: false);
            }
            catch (Exception ex) when (ex is IOException or
                InvalidOperationException or OperationCanceledException)
            {
                // The packet may have been applied before the connection
                // failed. NEVER retry a possibly delivered add operation.
                return FailureAfterWrites("delivery_uncertain",
                    "انقطع اتصال الراوتر أثناء الرفع. راجع المستخدمين يدويا قبل أي إعادة.",
                    attempted, added, uncertain: true);
            }
        }

        return new VoucherPublishReport(true, "published",
            "اكتمل رفع الكروت بعد التحقق من الأسماء والباقة.",
            attempted, added.Count, false, added.ToArray());
    }

    /// <summary>
    /// A complete snapshot is mandatory before initiating the FIRST add.
    /// Ordinary /print without count-only cannot distinguish a complete
    /// response from a valid but truncated set of names.
    /// </summary>
    private static async Task<IReadOnlySet<string>> ReadCompleteNamesAsync(
        RouterAuthorizedSession session, string path, CancellationToken cancellationToken)
    {
        const int maxPreflightRows = 20000;
        var countReply = await session.ReadAsync(path,
            new Dictionary<string, string> { ["count-only"] = "yes" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!countReply.Done.TryGetValue("ret", out string? countString) ||
            !int.TryParse(countString, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int expected) ||
            expected is < 0 or > maxPreflightRows ||
            countReply.Rows.Count != 0)
            throw new InvalidOperationException(
                "Invalid or excessive RouterOS count-only preflight.");

        var namesReply = await session.ReadAsync(path,
            new Dictionary<string, string> { [".proplist"] = "name" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (namesReply.Rows.Count != expected)
            throw new InvalidOperationException(
                "Incomplete RouterOS preflight: account count changed.");

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in namesReply.Rows)
        {
            if (!row.TryGetValue("name", out string? name) ||
                string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
                name.Any(char.IsControl) || !unique.Add(name))
                throw new InvalidOperationException(
                    "Missing, duplicated, or malformed RouterOS account name.");
        }
        return unique;
    }

    private static VoucherPublishReport Fail(string reason, string message, int attempted) =>
        new(false, reason, message, attempted, 0, false, []);

    private static VoucherPublishReport FailureAfterWrites(
        string reason, string message, int attempted,
        List<string> successes, bool uncertain) =>
        new(false, reason, message, attempted, successes.Count,
            uncertain, successes.ToArray());
}
