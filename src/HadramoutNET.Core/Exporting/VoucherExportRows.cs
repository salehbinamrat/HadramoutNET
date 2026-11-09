using System.Globalization;
using HadramoutNET.Core.Cards;

namespace HadramoutNET.Core.Exporting;

/// <summary>
/// Export the immutable generated batch, never mutable controls edited after generation.
/// All metadata is copied from each voucher rather than silently discarded.
/// </summary>
public static class VoucherExportRows
{
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> Build(
        IReadOnlyList<Voucher> vouchers, string service, string profile,
        string customer, int userManagerVersion, bool published, string server = "all")
    {
        ArgumentNullException.ThrowIfNull(vouchers);
        if (vouchers.Count == 0) throw new ArgumentException("لا توجد كروت للتصدير.");
        if (service is not ("hotspot" or "ppp" or "userman"))
            throw new ArgumentException("خدمة غير معروفة.", nameof(service));
        if (string.IsNullOrWhiteSpace(profile))
            throw new ArgumentException("الباقة غير محددة.", nameof(profile));
        if (server is null || server.Length > 128 || server.Any(char.IsControl) ||
            (service != "hotspot" && server != "all"))
            throw new ArgumentException("سيرفر الكروت غير صالح.", nameof(server));
        if (service == "userman" && userManagerVersion is not (6 or 7))
            throw new ArgumentException("إصدار User Manager غير مؤكد.");
        if (service == "userman" && userManagerVersion == 6 && string.IsNullOrWhiteSpace(customer))
            throw new ArgumentException("مالك User Manager 6 غير محدد.");
        if (vouchers.Any(v => string.IsNullOrWhiteSpace(v.Username) || v.LimitBytes < 0 ||
            v.ValidityDays < 0 || v.Price < 0))
            throw new ArgumentException("بيانات كرت غير صالحة.");
        if (vouchers.Select(v => v.Username).Distinct(StringComparer.Ordinal).Count() != vouchers.Count)
            throw new ArgumentException("توجد أسماء كروت مكررة.");
        return vouchers.Select(v => (IReadOnlyDictionary<string, string?>)
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["sequence"] = v.Sequence,
                ["username"] = v.Username,
                ["password"] = v.Password,
                ["profile"] = profile,
                ["service"] = service,
                ["um_version"] = service == "userman" ? userManagerVersion.ToString(CultureInfo.InvariantCulture) : "",
                ["customer"] = service == "userman" ? customer : "",
                ["server"] = service == "hotspot" ? server : "all",
                ["limit_uptime"] = v.LimitUptime,
                ["limit_bytes"] = v.LimitBytes.ToString(CultureInfo.InvariantCulture),
                ["validity_days"] = v.ValidityDays.ToString(CultureInfo.InvariantCulture),
                ["price"] = v.Price.ToString(CultureInfo.InvariantCulture),
                ["status"] = published ? "published" : "draft",
                ["created_at"] = v.CreatedAt,
                ["comment"] = ""
            }).ToArray();
    }
}
