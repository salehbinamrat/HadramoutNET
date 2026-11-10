using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Exporting;
using HadramoutNET.Core.Printing;

namespace HadramoutNET.App;

/// <summary>
/// Export one sample card to a standalone PNG using the exact WPF print
/// renderer. The preview is generated locally and NEVER saves real voucher
/// credentials or initiates RouterOS operations.
/// </summary>
internal static class CardPreviewPngExporter
{
    private const double PreviewDpi = 300;
    private const double MmPerInch = 25.4;
    private const double DipPerInch = 96;

    public static string Save(string destination, Voucher sample,
        PrintLayoutOptions layout, CardArtOptions artwork)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(artwork);

        FrameworkElement card = WindowsVoucherPrinter.BuildInlineCard(
            sample, layout, artwork);
        double widthDips = layout.CardWidthMm * DipPerInch / MmPerInch;
        double heightDips = layout.CardHeightMm * DipPerInch / MmPerInch;
        int widthPixels = checked((int)Math.Ceiling(layout.CardWidthMm * PreviewDpi / MmPerInch));
        int heightPixels = checked((int)Math.Ceiling(layout.CardHeightMm * PreviewDpi / MmPerInch));
        if (widthPixels <= 0 || heightPixels <= 0 ||
            (long)widthPixels * heightPixels > CardBackgroundAsset.MaxPixels)
            throw new PrintLayoutException("أبعاد صورة PNG أكبر من الحد الآمن.");

        card.Measure(new Size(widthDips, heightDips));
        card.Arrange(new Rect(0, 0, widthDips, heightDips));
        card.UpdateLayout();

        var pixels = new RenderTargetBitmap(widthPixels, heightPixels,
            PreviewDpi, PreviewDpi, PixelFormats.Pbgra32);
        pixels.Render(card);
        pixels.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(pixels));

        return AtomicExportFile.Write(destination, staged =>
        {
            using FileStream output = File.Create(staged);
            encoder.Save(output);
        });
    }
}
