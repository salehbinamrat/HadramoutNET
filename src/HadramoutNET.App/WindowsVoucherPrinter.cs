using System.Printing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Printing;

namespace HadramoutNET.App;

/// <summary>
/// Native Windows WPF card printing. Uses FixedDocument and the actual driver,
/// never launches an external PDF viewer or silently submits a job. The
/// requested ticket is merged/validated against the selected printer before
/// a single PrintDocument invocation. No uncertain job is ever retried.
/// </summary>
internal static class WindowsVoucherPrinter
{
    private const double DipPerPoint = 96d / 72d;

    internal sealed record PrintResult(bool Submitted, string Message);

    public static PrintResult Print(Window owner,
        IReadOnlyList<Voucher> cards, PrintLayoutOptions? options = null,
        CardArtOptions? artwork = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        NativePrintJobContract contract = NativePrintJobContract.Create(cards, options);
        CardArtPresetCodec.Validate(artwork ?? new CardArtOptions());
        FixedDocument document = BuildDocument(cards, contract, artwork);

        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
            return new PrintResult(false, "أُلغي أمر الطباعة قبل إرساله إلى Windows.");

        PrintQueue queue = dialog.PrintQueue ??
            throw new InvalidOperationException("لم يتم اختيار طابعة صالحة.");

        PrintTicket requested = new()
        {
            PageMediaSize = new PageMediaSize(
                contract.Paper == "A3" ? PageMediaSizeName.ISOA3 : PageMediaSizeName.ISOA4),
            PageOrientation = contract.Orientation == "landscape" ?
                PageOrientation.Landscape : PageOrientation.Portrait,
            Duplexing = contract.Duplex switch
            {
                "long-edge" => Duplexing.TwoSidedLongEdge,
                "short-edge" => Duplexing.TwoSidedShortEdge,
                _ => Duplexing.OneSided
            },
            CopyCount = 1
        };

        System.Printing.ValidationResult validation = queue.MergeAndValidatePrintTicket(
            queue.DefaultPrintTicket ?? new PrintTicket(), requested);
        PrintTicket valid = validation.ValidatedPrintTicket;
        if (valid.CopyCount != 1 ||
            valid.PageOrientation != requested.PageOrientation ||
            valid.Duplexing != requested.Duplexing ||
            valid.PageMediaSize is null ||
            valid.PageMediaSize.PageMediaSizeName != requested.PageMediaSize?.PageMediaSizeName)
        {
            throw new InvalidOperationException(
                "تعريف الطابعة لم يؤكد مقاس الورق أو اتجاهه أو وضع الوجهين أو نسخة واحدة؛ لم يبدأ الإرسال.");
        }

        // Never rescale cards to fit an unprintable margin. Require the
        // physical driver to accept every requested card rectangle.
        PrintCapabilities capabilities = queue.GetPrintCapabilities(valid);
        PageImageableArea? printable = capabilities.PageImageableArea;
        if (printable is null)
            throw new InvalidOperationException(
                "تعذر قراءة حدود الطباعة الفعلية من التعريف؛ لم تُرسل أي ورقة.");

        foreach (PrintPlacement placement in contract.Placements)
        {
            double left = placement.X * DipPerPoint;
            double top = (contract.Geometry.PageHeight - placement.Y -
                contract.Geometry.CardHeight) * DipPerPoint;
            double right = left + contract.Geometry.CardWidth * DipPerPoint;
            double bottom = top + contract.Geometry.CardHeight * DipPerPoint;
            const double tolerance = 0.3;
            if (left < printable.OriginWidth - tolerance ||
                top < printable.OriginHeight - tolerance ||
                right > printable.OriginWidth + printable.ExtentWidth + tolerance ||
                bottom > printable.OriginHeight + printable.ExtentHeight + tolerance)
                throw new InvalidOperationException(
                    "التخطيط يتجاوز الحدود الفعلية للطابعة؛ زد هامش الورق أو استخدم PDF.");
        }

        // One explicit print invocation; driver acceptance is NOT evidence
        // that the physical sheet has already been printed.
        dialog.PrintTicket = valid;
        dialog.PrintDocument(document.DocumentPaginator, "Hadramout Network Cards");
        return new PrintResult(true,
            "أُرسلت مهمة واحدة إلى قائمة انتظار Windows؛ تحقق من إخراج الطابعة الفعلي.");
    }

    /// <summary>
    /// On-screen first-sheet preview of exactly the same WPF card rectangles
    /// and PrintLayouts positions used by the native print job. There is NO
    /// PrintDialog, PrintDocument, printer command bar or auto-spooling path.
    /// </summary>
    public static void Preview(Window owner,
        IReadOnlyList<Voucher> cards, PrintLayoutOptions? options = null,
        CardArtOptions? artwork = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(cards);
        if (cards.Count == 0)
            throw new PrintLayoutException("أنشئ مسودة كروت قبل معاينة الورقة.");
        options ??= new PrintLayoutOptions();
        CardArtPresetCodec.Validate(artwork ?? new CardArtOptions());

        // Validate the entire batch first, not just the visible first sheet.
        PrintLayouts.ValidateUsernames(cards.Select(x => x.Username));
        PrintSheetPlan plan = PrintLayouts.Plan(options, cards.Count);
        var firstSheet = cards.Take(Math.Min(plan.PerSheet, cards.Count)).ToArray();
        NativePrintJobContract contract = NativePrintJobContract.Create(firstSheet, options);

        var pages = BuildPages(firstSheet, contract, artwork);
        var stack = new StackPanel
        {
            Background = new SolidColorBrush(Color.FromRgb(224, 224, 224)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        foreach (FixedPage page in pages)
        {
            double displayedWidth = 690;
            var viewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Width = displayedWidth,
                Height = displayedWidth * contract.Geometry.PageHeight / contract.Geometry.PageWidth,
                Child = page,
                Margin = new Thickness(10, 12, 10, 12)
            };
            stack.Children.Add(viewbox);
        }

        var description = new TextBlock
        {
            Text = $"معاينة أول ورقة فقط ({firstSheet.Length} كرت). " +
                $"إجمالي الأوراق {plan.Sheets}، وعدد صفحات الطباعة {plan.PdfPages}. " +
                "الطباعة لم تبدأ. تستخدم المعاينة نفس تصميم الطباعة المباشرة وPDF.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 12, 16, 12),
            FontWeight = FontWeights.SemiBold
        };
        var contents = new DockPanel();
        DockPanel.SetDock(description, Dock.Top);
        contents.Children.Add(description);
        contents.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stack
        });
        var window = new Window
        {
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = "معاينة تخطيط كروت حضرموت — دون إرسال إلى الطابعة",
            FlowDirection = FlowDirection.RightToLeft,
            Width = 850,
            Height = 810,
            MinWidth = 570,
            MinHeight = 490,
            Content = contents
        };
        window.ShowDialog();
    }

    private static FixedDocument BuildDocument(
        IReadOnlyList<Voucher> cards, NativePrintJobContract contract,
        CardArtOptions? artwork = null)
    {
        var doc = new FixedDocument();
        foreach (FixedPage page in BuildPages(cards, contract, artwork))
        {
            var content = new PageContent();
            ((IAddChild)content).AddChild(page);
            doc.Pages.Add(content);
        }
        if (doc.Pages.Count != contract.PageCount)
            throw new InvalidOperationException("تعذر إنشاء عدد صفحات الطباعة الصحيح.");
        return doc;
    }

    // Both native printing and preview render through this one renderer.
    // Preview supplies a bounded first-sheet contract only.
    private static IReadOnlyList<FixedPage> BuildPages(
        IReadOnlyList<Voucher> cards, NativePrintJobContract contract,
        CardArtOptions? artwork = null)
    {
        var pages = new List<FixedPage>();
        Brush? background = LoadBackground(artwork);
        long pageNumber = 0;
        FixedPage? fixedPage = null;
        foreach (PrintPlacement placement in contract.Placements)
        {
            if (pageNumber != placement.PdfPage)
            {
                fixedPage = new FixedPage
                {
                    Width = contract.Geometry.PageWidth * DipPerPoint,
                    Height = contract.Geometry.PageHeight * DipPerPoint,
                    Background = Brushes.White
                };
                pages.Add(fixedPage);
                pageNumber = placement.PdfPage;
            }

            FrameworkElement card = RenderCard(
                cards[placement.CardIndex],
                contract.Geometry.CardWidth * DipPerPoint,
                contract.Geometry.CardHeight * DipPerPoint, artwork, background);
            FixedPage.SetLeft(card, placement.X * DipPerPoint);
            FixedPage.SetTop(card,
                (contract.Geometry.PageHeight - placement.Y -
                 contract.Geometry.CardHeight) * DipPerPoint);
            fixedPage!.Children.Add(card);
        }
        if (pages.Count != contract.PageCount)
            throw new InvalidOperationException("عدد صفحات معاينة الطباعة لا يطابق المخطط.");
        return pages;
    }

    private static Brush? LoadBackground(CardArtOptions? artwork)
    {
        CardBackgroundPng? asset = CardBackgroundAsset.Decode(artwork?.BackgroundData);
        if (asset is null) return null;
        using var stream = new MemoryStream(asset.Png, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            Stretch = artwork!.BackgroundMode switch
            {
                "fit" => Stretch.Uniform,
                "fill" or "crop" => Stretch.UniformToFill,
                "tile" => Stretch.Fill,
                _ => Stretch.Fill
            },
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center
        };
        if (artwork.BackgroundMode == "tile")
        {
            brush.TileMode = TileMode.Tile;
            brush.ViewportUnits = BrushMappingMode.Absolute;
            // Matches the original 300-dpi card art reference pixel density.
            brush.Viewport = new Rect(0, 0,
                Math.Max(1, bitmap.PixelWidth * 96.0 / 300.0),
                Math.Max(1, bitmap.PixelHeight * 96.0 / 300.0));
        }
        brush.Freeze();
        return brush;
    }

    private static FrameworkElement RenderCard(
        Voucher voucher, double width, double height, CardArtOptions? artwork,
        Brush? background)
    {
        const double dipPerMm = 96.0 / 25.4;
        CardArtScene art = CardArtwork.Create(voucher,
            width / dipPerMm, height / dipPerMm, artwork ?? new CardArtOptions());
        static SolidColorBrush ColorBrush(string hex) => new(
            (Color)ColorConverter.ConvertFromString(hex));

        var canvas = new Canvas { Width = width, Height = height, ClipToBounds = true };
        canvas.Children.Add(new Rectangle
        {
            Width = width, Height = height,
            Fill = background ?? ColorBrush(art.Background)
        });
        canvas.Children.Add(new Rectangle
        {
            Width = Math.Min(width, 1.1 * dipPerMm), Height = height,
            Fill = ColorBrush(art.Accent)
        });

        foreach (CardArtText element in art.Texts)
        {
            var label = new TextBlock
            {
                Text = element.Value,
                FontFamily = new FontFamily(artwork?.FontFamily ?? "Arial"),
                FontWeight = element.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontSize = element.FontPt * DipPerPoint,
                Foreground = ColorBrush(element.Color),
                Width = element.Width * dipPerMm,
                Height = element.Height * dipPerMm,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.None,
                FlowDirection = FlowDirection.LeftToRight,
                TextAlignment = TextAlignment.Left
            };
            Canvas.SetLeft(label, element.X * dipPerMm);
            Canvas.SetTop(label, element.Y * dipPerMm);
            canvas.Children.Add(label);
        }

        if (art.Qr is CardArtQr qr)
        {
            int count = qr.Modules.GetLength(0);
            var squares = new GeometryGroup();
            for (int row = 0; row < count; row++)
                for (int col = 0; col < count; col++)
                    if (qr.Modules[row, col])
                        squares.Children.Add(new RectangleGeometry(
                            new Rect(col, row, 1, 1)));
            // Preserve the full QR module matrix INCLUDING the white quiet
            // zone. GeometryDrawing alone crops to dark-module bounds and
            // could distort the encoded data when the image is stretched.
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(Brushes.White, null,
                new RectangleGeometry(new Rect(0, 0, count, count))));
            group.Children.Add(new GeometryDrawing(Brushes.Black, null, squares));
            var drawing = new DrawingImage(group);
            drawing.Freeze();
            var image = new Image
            {
                Source = drawing,
                Stretch = Stretch.Fill,
                Width = qr.Size * dipPerMm,
                Height = qr.Size * dipPerMm,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(image, qr.X * dipPerMm);
            Canvas.SetTop(image, qr.Y * dipPerMm);
            canvas.Children.Add(image);
        }

        return new Border
        {
            Width = width, Height = height,
            Background = background ?? ColorBrush(art.Background),
            BorderBrush = ColorBrush(art.Border),
            BorderThickness = new Thickness(art.BorderWidthMm * dipPerMm),
            CornerRadius = new CornerRadius(art.BorderRadiusMm * dipPerMm),
            Child = canvas,
            ClipToBounds = true,
            SnapsToDevicePixels = true
        };
    }
}
