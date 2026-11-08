using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using HadramoutNET.Core.Exporting;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class ExcelExportParityTests
{
    private static IReadOnlyDictionary<string, string?> Row(params (string Key, string? Value)[] entries) =>
        entries.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    [Fact]
    public void Excel_roundtrip_preserves_credentials_Arabic_and_numeric_card_fields()
    {
        string folder = Path.Combine(Path.GetTempPath(), "HNET-xlsx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "cards.xlsx");
            ExcelExporter.SaveExcel([
                Row(("sequence", "0001"), ("username", "007123"), ("password", "=2+2"),
                    ("profile", "باقة يوم"), ("limit_bytes", "1048576"), ("price", "1500"))
            ], path);
            using var document = SpreadsheetDocument.Open(path, false);
            var worksheet = document.WorkbookPart!.WorksheetParts.Single().Worksheet;
            var cells = worksheet.Descendants<Cell>().ToDictionary(cell => cell.CellReference!.Value!);
            Assert.Equal("sequence", cells["A1"].InlineString?.Text?.Text);
            Assert.Equal("0001", cells["A2"].InlineString?.Text?.Text);
            Assert.Equal("007123", cells["B2"].InlineString?.Text?.Text);
            Assert.Equal("=2+2", cells["C2"].InlineString?.Text?.Text);
            Assert.Equal("باقة يوم", cells["D2"].InlineString?.Text?.Text);
            Assert.Null(cells["C2"].CellFormula);
            Assert.Equal(CellValues.InlineString, cells["C2"].DataType?.Value);
            Assert.Equal(CellValues.Number, cells["G2"].DataType?.Value);
            Assert.Equal("1048576", cells["G2"].CellValue?.Text);
            Assert.Equal("1500", cells["I2"].CellValue?.Text);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Excel_has_RTL_freeze_A2_filter_original_headers_and_styled_widths()
    {
        string folder = Path.Combine(Path.GetTempPath(), "HNET-xlsx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "cards.xlsx");
            ExcelExporter.SaveExcel([Row(("username", "1")), Row(("username", "2"))], path, "A?B/C\\D:E*F[G]H");
            using var document = SpreadsheetDocument.Open(path, false);
            var book = document.WorkbookPart!;
            Assert.Equal("ABCDEFGH", book.Workbook.Descendants<Sheet>().Single().Name!.Value);
            var worksheet = book.WorksheetParts.Single().Worksheet;
            Assert.True(worksheet.Descendants<SheetView>().Single().RightToLeft!.Value);
            var pane = worksheet.Descendants<Pane>().Single();
            Assert.Equal(PaneStateValues.Frozen, pane.State?.Value);
            Assert.Equal("A2", pane.TopLeftCell?.Value);
            Assert.Equal("A1:M3", worksheet.Descendants<AutoFilter>().Single().Reference?.Value);
            var headers = worksheet.Descendants<Row>().First().Elements<Cell>().ToArray();
            Assert.Equal(13, headers.Length);
            Assert.Equal("service", headers[^1].InlineString?.Text?.Text);
            Assert.Equal((uint)1, headers[0].StyleIndex?.Value);
            var columns = worksheet.Descendants<Column>().ToArray();
            Assert.Equal(13, columns.Length);
            Assert.Equal(20D, columns[1].Width?.Value);
            Assert.Equal(28D, columns[11].Width?.Value);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Export_failure_does_not_replace_old_workbook_or_leave_staged_data()
    {
        string folder = Path.Combine(Path.GetTempPath(), "HNET-xlsx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        static IEnumerable<IReadOnlyDictionary<string, string?>> BrokenCards()
        {
            yield return Row(("username", "a"));
            throw new InvalidOperationException("Source interrupted");
        }

        try
        {
            string path = Path.Combine(folder, "cards.xlsx");
            byte[] oldFile = [0x41, 0x42, 0x43, 0x44];
            File.WriteAllBytes(path, oldFile);
            Assert.Throws<InvalidOperationException>(() => ExcelExporter.SaveExcel(BrokenCards(), path));
            Assert.Equal(oldFile, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
