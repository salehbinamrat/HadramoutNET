using System.Runtime.Versioning;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Printing;
using PdfSharp.Pdf.IO;
using Xunit;

namespace HadramoutNET.Tests;

[SupportedOSPlatform("windows")]
public sealed class VoucherPdfExporterTests
{
    private static readonly Voucher[] Cards =
    [
        new("0001", "12345678", ""),
        new("0002", "12345679", "p1234567"),
        new("0003", "12345680", ""),
        new("0004", "12345681", ""),
        new("0005", "12345682", "")
    ];

    private static string FilePath(string name) =>
        Path.Combine(Path.GetTempPath(), "hnet-pdf-" + Guid.NewGuid().ToString("N"), name);

    [Fact]
    public void Creates_real_multi_page_pdf_with_original_A4_card_positions()
    {
        string path = FilePath("كروت حضرموت.pdf");
        try
        {
            var layout = new PrintLayoutOptions(Rows: 2, Columns: 2);
            string result = VoucherPdfExporter.SavePdf(Cards, path, layout);
            Assert.Equal(Path.GetFullPath(path), result);
            Assert.True(File.Exists(path));
            byte[] bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 500);
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
            using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            Assert.Equal(2, pdf.PageCount);
            var original = PrintLayouts.Calculate(layout);
            foreach (var page in pdf.Pages)
            {
                Assert.InRange(page.Width.Point, original.PageWidth - 0.1, original.PageWidth + 0.1);
                Assert.InRange(page.Height.Point, original.PageHeight - 0.1, original.PageHeight + 0.1);
            }
            Assert.Equal(2, PrintLayouts.Plan(layout, Cards.Length).PdfPages);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void Duplex_uses_original_front_back_page_count_and_landscape_geometry()
    {
        string path = FilePath("duplex.pdf");
        try
        {
            var layout = new PrintLayoutOptions(Rows: 1, Columns: 2,
                Duplex: "short-edge", Orientation: "landscape", Paper: "A3");
            VoucherPdfExporter.SavePdf(Cards.Take(3).ToArray(), path, layout);
            using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            Assert.Equal(4, pdf.PageCount);
            Assert.True(pdf.Pages[0].Width.Point > pdf.Pages[0].Height.Point);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void Duplicate_or_blank_usernames_fail_before_touching_existing_pdf()
    {
        string path = FilePath("protected.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        try
        {
            Assert.Throws<PrintLayoutException>(() => VoucherPdfExporter.SavePdf(
                [new Voucher("01", "repeat", ""), new Voucher("02", "repeat", "")],
                path));
            Assert.Throws<PrintLayoutException>(() => VoucherPdfExporter.SavePdf(
                [new Voucher("01", "", "")], path));
            Assert.Throws<PrintLayoutException>(() => VoucherPdfExporter.SavePdf(
                [], path));
            Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(path));
            Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(path)!),
                f => Path.GetFileName(f).StartsWith(".nidi-export-", StringComparison.Ordinal));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void Invalid_geometry_never_overwrites_original_file()
    {
        string path = FilePath("existing.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "KEEP");
        try
        {
            Assert.Throws<PrintLayoutException>(() => VoucherPdfExporter.SavePdf(
                Cards, path, new PrintLayoutOptions(
                    CardWidthMm: 250, Rows: 2, Columns: 2)));
            Assert.Equal("KEEP", File.ReadAllText(path));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}
