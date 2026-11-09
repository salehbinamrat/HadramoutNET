using System.Runtime.Versioning;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Exporting;

namespace HadramoutNET.Core.Printing;

/// <summary>
/// Creates real A3/A4 card sheets with searchable, selectable voucher
/// credentials; unlike a bitmap-only PDF, usernames/passwords remain text.
/// Uses the already-tested Python-parity sheet placement engine. This is
/// an initial red native card template, NOT the full original layer editor.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VoucherPdfExporter
{
    private static readonly object FontsLock = new();

    public static string SavePdf(
        IReadOnlyList<Voucher> cards,
        string destination,
        PrintLayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (cards.Count == 0)
            throw new PrintLayoutException("لا توجد كروت لتصديرها.");

        PrintLayouts.ValidateUsernames(cards.Select(c => c.Username));
        foreach (Voucher card in cards)
        {
            if (card.Username.Length > 64 ||
                card.Password.Length > 64 ||
                card.Username.Any(char.IsControl) ||
                card.Password.Any(char.IsControl))
                throw new PrintLayoutException("تحتوي الكروت على بيانات نصية غير صالحة للطباعة.");
        }

        options ??= new PrintLayoutOptions();
        PrintSheetPlan plan = PrintLayouts.Plan(options, cards.Count);
        PrepareArialFont();

        return AtomicExportFile.Write(destination, staged =>
        {
            using var pdf = new PdfDocument();
            pdf.Info.Title = "Hadramout Network Cards";
            pdf.Info.Author = "Hadramout Network Cards";
            pdf.Info.Subject = "MikroTik voucher cards";

            using var fontName = new XFont("Arial", 8, XFontStyleEx.Bold);
            using var fontCode = new XFont("Arial", 10, XFontStyleEx.Bold);
            using var fontSmall = new XFont("Arial", 6, XFontStyleEx.Regular);
            var red = new XSolidBrush(XColor.FromArgb(125, 15, 36));
            var redLight = new XSolidBrush(XColor.FromArgb(194, 27, 60));
            var cream = new XSolidBrush(XColor.FromArgb(255, 250, 241));
            var gold = new XPen(XColor.FromArgb(197, 159, 99), 0.45);
            var dark = new XSolidBrush(XColor.FromArgb(40, 25, 27));

            XGraphics? graphics = null;
            long currentPage = 0;
            try
            {
                foreach (PrintPlacement placement in PrintLayouts.Placements(options, cards.Count))
                {
                    if (placement.PdfPage != currentPage)
                    {
                        graphics?.Dispose();
                        PdfPage page = pdf.AddPage();
                        page.Width = XUnit.FromPoint(plan.Layout.PageWidth);
                        page.Height = XUnit.FromPoint(plan.Layout.PageHeight);
                        graphics = XGraphics.FromPdfPage(page);
                        currentPage = placement.PdfPage;
                    }

                    // PrintLayouts positions use bottom-left PDF coordinates,
                    // while XGraphics operates top-left by default.
                    double x = placement.X;
                    double y = plan.Layout.PageHeight - placement.Y - plan.Layout.CardHeight;
                    double width = plan.Layout.CardWidth;
                    double height = plan.Layout.CardHeight;

                    // Deliberately simple red template until full fidelity
                    // original customizable design/layer rendering is ported.
                    graphics!.DrawRectangle(gold, cream, x, y, width, height);
                    graphics.DrawRectangle(red, x, y, width, height * 0.23);
                    graphics.DrawRectangle(redLight, x, y + height * 0.23,
                        width, height * 0.06);

                    graphics.DrawString("HADRAMOUT NETWORK", fontName, XBrushes.White,
                        new XRect(x + 2, y, width - 4, height * 0.23),
                        XStringFormats.Center);

                    Voucher card = cards[placement.CardIndex];
                    graphics.DrawString(card.Username, fontCode, dark,
                        new XRect(x + 2, y + height * 0.32,
                            width - 4, height * (card.Password.Length == 0 ? 0.50 : 0.24)),
                        XStringFormats.Center);
                    if (!string.IsNullOrEmpty(card.Password))
                    {
                        graphics.DrawString(card.Password, fontCode, dark,
                            new XRect(x + 2, y + height * 0.58,
                                width - 4, height * 0.23),
                            XStringFormats.Center);
                    }
                    graphics.DrawString("#" + card.Sequence, fontSmall, red,
                        new XRect(x + 2, y + height * 0.83, width - 4, height * 0.12),
                        XStringFormats.Center);
                }
            }
            finally
            {
                graphics?.Dispose();
            }

            if (pdf.PageCount != plan.PdfPages)
                throw new InvalidOperationException("عدد صفحات PDF لا يطابق مخطط الطباعة.");
            pdf.Save(staged);
        });
    }

    private static void PrepareArialFont()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("يتطلب تصدير هذا القالب خطوط Windows.");

        lock (FontsLock)
        {
            if (GlobalFontSettings.FontResolver is null)
                GlobalFontSettings.FontResolver = new WindowsArialResolver();
        }
    }

    /// <summary>
    /// Read Arial from the user's own Windows installation. No Microsoft
    /// font files are committed to GitHub or redistributed with the app.
    /// </summary>
    private sealed class WindowsArialResolver : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(
            string familyName, bool isBold, bool isItalic)
        {
            if (!familyName.Equals("Arial", StringComparison.OrdinalIgnoreCase))
                return null;
            return new FontResolverInfo(isBold ? "hnet-arial-bold" : "hnet-arial",
                mustSimulateBold: false, mustSimulateItalic: isItalic);
        }

        public byte[]? GetFont(string faceName)
        {
            string fileName = faceName switch
            {
                "hnet-arial" => "arial.ttf",
                "hnet-arial-bold" => "arialbd.ttf",
                _ => throw new ArgumentException("Unknown local Windows font.", nameof(faceName))
            };
            string directory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("تعذر تحديد خط Arial الخاص بنظام Windows.");
            return File.ReadAllBytes(Path.Combine(directory, fileName));
        }
    }
}
