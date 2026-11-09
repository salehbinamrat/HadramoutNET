using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace HadramoutNET.Core.Exporting;

/// <summary>
/// Generates a real offline Office Open XML workbook matching the pinned
/// Python export_excel layout. Credentials remain inline text even when they
/// look like numbers or Excel formulas. No Excel installation is required.
/// </summary>
public static class ExcelExporter
{
    private static readonly double[] Widths =
        [12, 20, 18, 18, 15, 15, 16, 14, 12, 14, 22, 28, 15];
    private static readonly HashSet<string> NumericColumns =
        ["limit_bytes", "validity_days", "price"];

    public static string SaveExcel(
        IEnumerable<IReadOnlyDictionary<string, string?>> cards,
        string path,
        string title = "Cards")
    {
        ArgumentNullException.ThrowIfNull(cards);
        // Consume all rows before opening the destination or its staging file.
        var rows = cards.ToList();
        foreach (var card in rows) ArgumentNullException.ThrowIfNull(card);
        if (rows.Count > 1_048_575)
            throw new ArgumentOutOfRangeException(nameof(cards), "Excel worksheet exceeds the row limit.");
        string sheetName = SanitizeSheetName(title);
        return AtomicExportFile.Write(path, staged => WriteWorkbook(staged, sheetName, rows));
    }

    public static string SanitizeSheetName(string? title)
    {
        string input = string.IsNullOrEmpty(title) ? "Cards" : title;
        string clean = new(input.Where(character => !"\\/*?:[]".Contains(character)).Take(31).ToArray());
        return string.IsNullOrEmpty(clean) ? "Cards" : clean;
    }

    private static void WriteWorkbook(
        string path, string sheetName,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> cards)
    {
        using SpreadsheetDocument doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        WorkbookPart bookPart = doc.AddWorkbookPart();
        bookPart.Workbook = new Workbook();

        WorkbookStylesPart stylesPart = bookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = CreateStyles();
        stylesPart.Stylesheet.Save();

        WorksheetPart worksheetPart = bookPart.AddNewPart<WorksheetPart>();

        var sheetView = new SheetView { WorkbookViewId = 0U, RightToLeft = true };
        sheetView.Append(new Pane
        {
            VerticalSplit = 1D,
            TopLeftCell = "A2",
            ActivePane = PaneValues.BottomLeft,
            State = PaneStateValues.Frozen
        });
        var columns = new Columns();
        for (int index = 0; index < CardExports.CardHeaders.Count; index++)
        {
            columns.Append(new Column
            {
                Min = (uint)(index + 1),
                Max = (uint)(index + 1),
                Width = Widths[index],
                CustomWidth = true
            });
        }

        var data = new SheetData();
        var header = new Row { RowIndex = 1U };
        for (int index = 0; index < CardExports.CardHeaders.Count; index++)
        {
            var cell = TextCell(index, 1U, CardExports.CardHeaders[index]);
            cell.StyleIndex = 1U;
            header.Append(cell);
        }
        data.Append(header);

        for (int rowIndex = 0; rowIndex < cards.Count; rowIndex++)
        {
            uint excelRow = checked((uint)rowIndex + 2);
            var row = new Row { RowIndex = excelRow };
            var card = cards[rowIndex];
            for (int column = 0; column < CardExports.CardHeaders.Count; column++)
            {
                string key = CardExports.CardHeaders[column];
                string value = card.TryGetValue(key, out var text) ? text ?? "" : "";
                row.Append(NumericColumns.Contains(key) &&
                    decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)
                    ? new Cell
                    {
                        CellReference = Coordinate(column, excelRow),
                        DataType = CellValues.Number,
                        CellValue = new CellValue(number.ToString(CultureInfo.InvariantCulture))
                    }
                    : TextCell(column, excelRow, value));
            }
            data.Append(row);
        }

        worksheetPart.Worksheet = new Worksheet(
            new SheetViews(sheetView),
            columns,
            data,
            new AutoFilter { Reference = $"A1:M{cards.Count + 1}" });
        worksheetPart.Worksheet.Save();

        var sheets = new Sheets();
        sheets.Append(new Sheet
        {
            Id = bookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = sheetName
        });
        bookPart.Workbook.Append(sheets);
        bookPart.Workbook.Save();
    }

    private static string Coordinate(int column, uint row) =>
        ((char)('A' + column)).ToString() + row.ToString(CultureInfo.InvariantCulture);

    private static Cell TextCell(int column, uint row, string text) => new()
    {
        CellReference = Coordinate(column, row),
        DataType = CellValues.InlineString,
        InlineString = new InlineString(
            new Text(text) { Space = SpaceProcessingModeValues.Preserve })
    };

    private static Stylesheet CreateStyles()
    {
        var fonts = new Fonts(
            new Font(),
            new Font(new Bold(), new Color { Rgb = "FFFFFFFF" })) { Count = 2U };
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(
                new ForegroundColor { Rgb = "FF008F78" })
                { PatternType = PatternValues.Solid })) { Count = 3U };
        var borders = new Borders(new Border()) { Count = 1U };
        var formats = new CellFormats(
            new CellFormat(),
            new CellFormat
            {
                FontId = 1U,
                FillId = 2U,
                BorderId = 0U,
                ApplyFont = true,
                ApplyFill = true,
                ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center }
            }) { Count = 2U };
        return new Stylesheet(
            fonts, fills, borders,
            new CellStyleFormats(new CellFormat()) { Count = 1U },
            formats);
    }
}
