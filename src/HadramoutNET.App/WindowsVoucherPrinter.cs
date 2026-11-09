using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
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
        IReadOnlyList<Voucher> cards, PrintLayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        NativePrintJobContract contract = NativePrintJobContract.Create(cards, options);
        FixedDocument document = BuildDocument(cards, contract);

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

    private static FixedDocument BuildDocument(
        IReadOnlyList<Voucher> cards, NativePrintJobContract contract)
    {
        var doc = new FixedDocument();
        long pageNumber = 0;
        FixedPage? fixedPage = null;
        var background = Brushes.White;

        foreach (PrintPlacement placement in contract.Placements)
        {
            if (pageNumber != placement.PdfPage)
            {
                fixedPage = new FixedPage
                {
                    Width = contract.Geometry.PageWidth * DipPerPoint,
                    Height = contract.Geometry.PageHeight * DipPerPoint,
                    Background = background
                };
                var content = new PageContent();
                ((IAddChild)content).AddChild(fixedPage);
                doc.Pages.Add(content);
                pageNumber = placement.PdfPage;
            }

            FrameworkElement card = RenderCard(
                cards[placement.CardIndex],
                contract.Geometry.CardWidth * DipPerPoint,
                contract.Geometry.CardHeight * DipPerPoint);
            FixedPage.SetLeft(card, placement.X * DipPerPoint);
            FixedPage.SetTop(card,
                (contract.Geometry.PageHeight - placement.Y -
                 contract.Geometry.CardHeight) * DipPerPoint);
            fixedPage!.Children.Add(card);
        }
        if (doc.Pages.Count != contract.PageCount)
            throw new InvalidOperationException("تعذر إنشاء عدد صفحات الطباعة الصحيح.");
        return doc;
    }

    private static FrameworkElement RenderCard(Voucher voucher, double width, double height)
    {
        var navyReplacement = new SolidColorBrush(Color.FromRgb(125, 15, 36));
        var cream = new SolidColorBrush(Color.FromRgb(255, 250, 241));
        var border = new Border
        {
            Width = width,
            Height = height,
            BorderBrush = new SolidColorBrush(Color.FromRgb(197, 159, 99)),
            BorderThickness = new Thickness(0.6),
            Background = cream,
            SnapsToDevicePixels = true,
            ClipToBounds = true
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.28, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.31, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.26, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.15, GridUnitType.Star) });

        var heading = new Border { Background = navyReplacement };
        var title = NewText("شبكة حضرموت", 11, Brushes.White, true);
        heading.Child = title;
        Grid.SetRow(heading, 0);
        grid.Children.Add(heading);

        var username = NewText(voucher.Username, 13, Brushes.Black, true);
        Grid.SetRow(username, 1);
        grid.Children.Add(username);

        var password = NewText(voucher.Password, 12, Brushes.Black, true);
        Grid.SetRow(password, 2);
        grid.Children.Add(password);

        var serial = NewText("#" + voucher.Sequence, 9, navyReplacement, false);
        Grid.SetRow(serial, 3);
        grid.Children.Add(serial);

        border.Child = grid;
        return border;
    }

    private static TextBlock NewText(
        string label, double fontSize, Brush foreground, bool bold) =>
        new()
        {
            Text = label,
            FontFamily = new FontFamily("Arial"),
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            FontSize = fontSize,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FlowDirection = FlowDirection.RightToLeft,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
}
