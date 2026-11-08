namespace HadramoutNET.Core.Printing;

/// <summary>
/// Physical sheet settings shared by future WPF preview, PDF renderer, and
/// native printer dispatch. Distances are millimeters at the API boundary.
/// The source-of-truth reference is Python core/printing.py (pinned SHA).
/// </summary>
public sealed record PrintLayoutOptions(
    double CardWidthMm = 50.0,
    double CardHeightMm = 19.0,
    int Rows = 15,
    int Columns = 4,
    string Paper = "A4",
    string Orientation = "portrait",
    double HorizontalGapMm = 0.5,
    double VerticalGapMm = 0.5,
    double MarginMm = 0.5,
    string Order = "left_to_right",
    string Duplex = "single");

public sealed class PrintLayoutException(string message) : ArgumentException(message);

/// <summary>Immutable geometry in PDF points (1 point = 1/72 inch).</summary>
public sealed record PrintLayoutGeometry(
    double PageWidth,
    double PageHeight,
    int Rows,
    int Columns,
    double CardWidth,
    double CardHeight,
    double GapX,
    double GapY,
    double StartX,
    double TopY,
    double GridWidth,
    double GridHeight)
{
    public int PerPage => checked(Rows * Columns);
}

public sealed record PrintSheetPlan(PrintLayoutGeometry Layout, int Quantity, long Sheets, long PdfPages)
{
    public int PerSheet => Layout.PerPage;
}

/// <summary>
/// CardIndex indexes the requested cards; PdfPage is 1-based for the
/// front-then-back PDF order. X/Y are bottom-left PDF coordinates in points.
/// </summary>
public readonly record struct PrintPlacement(
    int CardIndex, long PdfPage, bool IsBack, double X, double Y);

public static class PrintLayouts
{
    public const double PointsPerMillimeter = 72.0 / 25.4;

    public static (int Rows, int Columns) AutoGrid(PrintLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateGeometry(options, validateGridCounts: false);
        var (width, height) = PageDimensions(options.Paper, options.Orientation);
        double availableWidth = width / PointsPerMillimeter - 2.0 * options.MarginMm;
        double availableHeight = height / PointsPerMillimeter - 2.0 * options.MarginMm;
        int columns = (int)Math.Floor(
            (availableWidth + options.HorizontalGapMm) /
            (options.CardWidthMm + options.HorizontalGapMm));
        int rows = (int)Math.Floor(
            (availableHeight + options.VerticalGapMm) /
            (options.CardHeightMm + options.VerticalGapMm));
        return (Math.Max(1, rows), Math.Max(1, columns));
    }

    public static PrintLayoutGeometry Calculate(PrintLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateGeometry(options, validateGridCounts: true);
        ValidateModes(options);

        var (pageWidth, pageHeight) = PageDimensions(options.Paper, options.Orientation);
        double cardWidth = options.CardWidthMm * PointsPerMillimeter;
        double cardHeight = options.CardHeightMm * PointsPerMillimeter;
        double gapX = options.HorizontalGapMm * PointsPerMillimeter;
        double gapY = options.VerticalGapMm * PointsPerMillimeter;
        double margin = options.MarginMm * PointsPerMillimeter;
        double gridWidth = options.Columns * cardWidth + (options.Columns - 1) * gapX;
        double gridHeight = options.Rows * cardHeight + (options.Rows - 1) * gapY;

        // Keep the Python tolerance of 0.1 PDF points; never shrink cards
        // to fit or silently change an operator's requested row/column count.
        if (gridWidth > pageWidth - 2 * margin + 0.1 ||
            gridHeight > pageHeight - 2 * margin + 0.1)
        {
            var suggestion = AutoGrid(options);
            throw new PrintLayoutException(
                $"التخطيط {options.Rows}×{options.Columns} أكبر من مساحة ورقة {options.Paper.ToUpperInvariant()}. " +
                $"المقترح لهذه الأبعاد: {suggestion.Rows} صف × {suggestion.Columns} عمود.");
        }

        return new PrintLayoutGeometry(
            pageWidth, pageHeight, options.Rows, options.Columns,
            cardWidth, cardHeight, gapX, gapY,
            (pageWidth - gridWidth) / 2.0,
            pageHeight - (pageHeight - gridHeight) / 2.0,
            gridWidth, gridHeight);
    }

    public static PrintSheetPlan Plan(PrintLayoutOptions options, int quantity)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateModes(options);
        var geometry = Calculate(options);
        quantity = Math.Max(quantity, 0); // Original print_plan treats negatives as empty.
        long count = quantity == 0 ? 0 : ((long)quantity + geometry.PerPage - 1) / geometry.PerPage;
        long pages = count * (options.Duplex == "single" ? 1 : 2);
        return new PrintSheetPlan(geometry, quantity, count, pages);
    }

    /// <summary>
    /// Pure streaming page/slot generator. No PDF files are written and no
    /// printer jobs are started. Work is O(cards) with constant extra memory.
    /// </summary>
    public static IEnumerable<PrintPlacement> Placements(PrintLayoutOptions options, int quantity)
    {
        var plan = Plan(options, quantity);
        var layout = plan.Layout;
        int sideCount = options.Duplex == "single" ? 1 : 2;
        for (long start = 0, sheet = 0; start < plan.Quantity; start += layout.PerPage, ++sheet)
        {
            int cardsOnSheet = (int)Math.Min(layout.PerPage, (long)plan.Quantity - start);
            for (int back = 0; back < sideCount; back++)
            {
                for (int index = 0; index < cardsOnSheet; index++)
                {
                    var (x, y) = Slot(layout, index, options.Order);
                    if (back == 1)
                    {
                        // Python card_pages: long-edge portrait = horizontal mirror;
                        // long-edge landscape = vertical mirror, short-edge opposite.
                        bool mirrorX =
                            (options.Duplex == "long-edge") == (options.Orientation == "portrait");
                        if (mirrorX)
                            x = layout.PageWidth - x - layout.CardWidth;
                        else
                            y = layout.PageHeight - y - layout.CardHeight;
                    }
                    yield return new PrintPlacement(
                        checked((int)(start + index)),
                        sheet * sideCount + back + 1, back != 0, x, y);
                }
            }
        }
    }

    public static void ValidateUsernames(IEnumerable<string?> usernames)
    {
        ArgumentNullException.ThrowIfNull(usernames);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? name in usernames)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                throw new PrintLayoutException(
                    "توجد أسماء مستخدمين فارغة أو مكررة في طلب الطباعة؛ لم تبدأ الطباعة.");
        }
    }

    private static (double X, double Y) Slot(PrintLayoutGeometry layout, int index, string order)
    {
        int row = index / layout.Columns;
        int column = index % layout.Columns;
        if (order is "column_left" or "column_right")
        {
            column = index / layout.Rows;
            row = index % layout.Rows;
            if (order == "column_right")
                column = layout.Columns - 1 - column;
        }

        if (order == "right_to_left" || (order == "snake" && row % 2 == 1))
            column = layout.Columns - 1 - column;

        return (
            layout.StartX + column * (layout.CardWidth + layout.GapX),
            layout.TopY - layout.CardHeight - row * (layout.CardHeight + layout.GapY));
    }

    private static void ValidateModes(PrintLayoutOptions options)
    {
        if (options.Order is not ("left_to_right" or "right_to_left" or "snake"
            or "column_left" or "column_right"))
            throw new PrintLayoutException("ترتيب الكروت غير صالح.");

        if (options.Duplex is not ("single" or "long-edge" or "short-edge"))
            throw new PrintLayoutException("إعداد الطباعة على الوجهين غير صالح.");
    }

    private static void ValidateGeometry(PrintLayoutOptions options, bool validateGridCounts)
    {
        Numeric(options.CardWidthMm, "عرض الكرت", 1, 300);
        Numeric(options.CardHeightMm, "ارتفاع الكرت", 1, 300);
        Numeric(options.HorizontalGapMm, "الفاصل الأفقي", 0, 300);
        Numeric(options.VerticalGapMm, "الفاصل الرأسي", 0, 300);
        Numeric(options.MarginMm, "هامش الورقة", 0, 300);

        if (validateGridCounts &&
            (options.Rows is < 1 or > 1000 || options.Columns is < 1 or > 1000))
            throw new PrintLayoutException("عدد الصفوف أو الأعمدة غير صالح.");

        _ = PageDimensions(options.Paper, options.Orientation);
    }

    private static void Numeric(double value, string label, double minimum, double maximum)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new PrintLayoutException("قيمة غير صالحة: " + label);
    }

    private static (double Width, double Height) PageDimensions(string paper, string orientation)
    {
        if (paper is null || (paper.ToUpperInvariant() is not ("A4" or "A3")) ||
            orientation is not ("portrait" or "landscape"))
            throw new PrintLayoutException("حجم الورق أو اتجاهه غير صالح.");

        // ReportLab sizes: A4 (210 x 297 mm), A3 (297 x 420 mm).
        double widthMm = paper.Equals("A3", StringComparison.OrdinalIgnoreCase) ? 297 : 210;
        double heightMm = paper.Equals("A3", StringComparison.OrdinalIgnoreCase) ? 420 : 297;
        if (orientation == "landscape")
            (widthMm, heightMm) = (heightMm, widthMm);

        return (widthMm * PointsPerMillimeter, heightMm * PointsPerMillimeter);
    }
}
