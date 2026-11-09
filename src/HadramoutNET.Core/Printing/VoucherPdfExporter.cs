using System.Runtime.Versioning;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Exporting;

namespace HadramoutNET.Core.Printing;

/// <summary>
/// Card PDF uses the SAME physical artwork coordinates, text and QR modules
/// as WindowsVoucherPrinter. Output is atomic and never opens a printer.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VoucherPdfExporter
{
    private static readonly object FontsLock = new();

    public static string SavePdf(
        IReadOnlyList<Voucher> cards,
        string destination,
        PrintLayoutOptions? options = null,
        CardArtOptions? artwork = null)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (cards.Count == 0)
            throw new PrintLayoutException("لا توجد كروت لتصديرها.");

        PrintLayouts.ValidateUsernames(cards.Select(c => c.Username));
        options ??= new PrintLayoutOptions();
        artwork ??= new CardArtOptions();
        CardArtPresetCodec.Validate(artwork);
        PrintSheetPlan plan = PrintLayouts.Plan(options, cards.Count);

        // Preflight EVERY voucher before touching a preexisting destination.
        // Sensitive data are never written to audit logs or exceptions.
        foreach (Voucher card in cards)
            _ = CardArtwork.Create(card, options.CardWidthMm,
                options.CardHeightMm, artwork);
        PrepareArialFont();

        CardBackgroundPng? bgAsset = CardBackgroundAsset.Decode(artwork.BackgroundData);
        return AtomicExportFile.Write(destination, staged =>
        {
            using var backgroundStream = bgAsset is null ? null :
                new MemoryStream(bgAsset.Png, writable: false);
            using XImage? backgroundImage = backgroundStream is null ?
                null : XImage.FromStream(backgroundStream);
            using var pdf = new PdfDocument();
            pdf.Info.Title = "Hadramout Network Cards";
            pdf.Info.Author = "Hadramout Network Cards";
            pdf.Info.Subject = "MikroTik voucher cards";

            var fonts = new Dictionary<(double Size, bool Bold), XFont>();
            XFont GetFont(double size, bool bold)
            {
                var key = (size, bold);
                if (!fonts.TryGetValue(key, out XFont? font))
                {
                    font = new XFont("Arial", size,
                        bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
                    fonts.Add(key, font);
                }
                return font;
            }
            static XColor Color(string code)
            {
                byte[] components = Convert.FromHexString(code.AsSpan(1));
                return XColor.FromArgb(components[0], components[1], components[2]);
            }

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
                    double x = placement.X;
                    double y = plan.Layout.PageHeight - placement.Y - plan.Layout.CardHeight;
                    var art = CardArtwork.Create(cards[placement.CardIndex],
                        options.CardWidthMm, options.CardHeightMm, artwork);
                    const double mm = PrintLayouts.PointsPerMillimeter;
                    double width = plan.Layout.CardWidth, height = plan.Layout.CardHeight;
                    var bg = new XSolidBrush(Color(art.Background));
                    var accent = new XSolidBrush(Color(art.Accent));
                    var gold = new XPen(Color(art.Border), 0.45);

                    graphics!.DrawRectangle(gold, bg, x, y, width, height);
                    if (backgroundImage is not null && bgAsset is not null)
                        DrawBackground(graphics, backgroundImage, bgAsset,
                            artwork.BackgroundMode, x, y, width, height);
                    graphics.DrawRectangle(accent, x, y, Math.Min(width, 1.1 * mm), height);

                    foreach (CardArtText t in art.Texts)
                    {
                        graphics.DrawString(t.Value, GetFont(t.FontPt, t.Bold),
                            new XSolidBrush(Color(t.Color)),
                            new XRect(x + t.X * mm, y + t.Y * mm,
                                t.Width * mm, t.Height * mm),
                            XStringFormats.TopLeft);
                    }
                    if (art.Qr is CardArtQr qr)
                    {
                        int count = qr.Modules.GetLength(0);
                        double module = qr.Size * mm / count;
                        double sx = x + qr.X * mm, sy = y + qr.Y * mm;
                        graphics.DrawRectangle(XBrushes.White, sx, sy,
                            qr.Size * mm, qr.Size * mm);
                        for (int row = 0; row < count; row++)
                            for (int col = 0; col < count; col++)
                                if (qr.Modules[row, col])
                                    graphics.DrawRectangle(XBrushes.Black,
                                        sx + col * module, sy + row * module,
                                        module + 0.005, module + 0.005);
                    }
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

    /// <summary>
    /// Same physical image aspect and 300 dpi legacy tile reference as WPF.
    /// Drawing is clipped to the card rectangle for fill and tile modes.
    /// The PNG is decoded only once per PDF document.
    /// </summary>
    private static void DrawBackground(XGraphics g, XImage image,
        CardBackgroundPng asset, string mode,
        double x, double y, double width, double height)
    {
        double ratio = (double)asset.Width / asset.Height;
        if (mode == "stretch")
        {
            g.DrawImage(image, x, y, width, height);
            return;
        }
        if (mode == "fit")
        {
            double w = width, h = width / ratio;
            if (h > height) { h = height; w = height * ratio; }
            g.DrawImage(image, x + (width - w) / 2,
                y + (height - h) / 2, w, h);
            return;
        }

        var state = g.Save();
        try
        {
            g.IntersectClip(new XRect(x, y, width, height));
            if (mode == "fill")
            {
                double w = width, h = width / ratio;
                if (h < height) { h = height; w = height * ratio; }
                g.DrawImage(image, x + (width - w) / 2,
                    y + (height - h) / 2, w, h);
            }
            else if (mode == "tile")
            {
                double w = asset.Width * 72.0 / 300.0;
                double h = asset.Height * 72.0 / 300.0;
                long cols = (long)Math.Ceiling(width / w);
                long rows = (long)Math.Ceiling(height / h);
                if (rows * cols > 10000)
                    throw new PrintLayoutException(
                        "صورة الخلفية صغيرة جدا لتكرارها على مساحة الكرت.");
                for (long row = 0; row < rows; row++)
                    for (long col = 0; col < cols; col++)
                        g.DrawImage(image, x + col * w, y + row * h, w, h);
            }
            else
                throw new PrintLayoutException("وضع صورة خلفية الكرت غير مدعوم.");
        }
        finally { g.Restore(state); }
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
