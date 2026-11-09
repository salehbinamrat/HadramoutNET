using HadramoutNET.Core.Cards;

namespace HadramoutNET.Core.Printing;

/// <summary>
/// A pure testable contract for native Windows print jobs. This object cannot
/// submit a printer job. The WPF printer adapter must show an explicit dialog,
/// validate the driver ticket, and only then spool a single confirmed copy.
/// </summary>
public sealed record NativePrintJobContract(
    string Paper,
    string Orientation,
    string Duplex,
    int Copies,
    bool Submitted,
    int VoucherCount,
    long PageCount,
    PrintLayoutGeometry Geometry,
    IReadOnlyList<PrintPlacement> Placements)
{
    public static NativePrintJobContract Create(
        IReadOnlyList<Voucher> vouchers,
        PrintLayoutOptions? settings = null)
    {
        ArgumentNullException.ThrowIfNull(vouchers);
        if (vouchers.Count == 0)
            throw new PrintLayoutException("لا توجد كروت لإرسالها إلى الطابعة.");

        PrintLayouts.ValidateUsernames(vouchers.Select(v => v.Username));
        if (vouchers.Any(v => v.Username.Length > 64 || v.Password.Length > 64))
            throw new PrintLayoutException("تحتوي الكروت على نصوص أطول من الحد المسموح للطباعة.");

        settings ??= new PrintLayoutOptions();
        PrintSheetPlan plan = PrintLayouts.Plan(settings, vouchers.Count);
        var placements = PrintLayouts.Placements(settings, vouchers.Count).ToArray();
        return new NativePrintJobContract(
            settings.Paper.ToUpperInvariant(),
            settings.Orientation,
            settings.Duplex,
            Copies: 1,
            Submitted: false,
            vouchers.Count,
            plan.PdfPages,
            plan.Layout,
            placements);
    }
}
