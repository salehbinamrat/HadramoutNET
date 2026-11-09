using System.Text.Json;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Data;
using HadramoutNET.Core.Printing;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>
/// Contract based on the retained original Python core/db.py defaults and
/// core/printing.py: settings are per-package and shared by PDF/native/preview.
/// </summary>
public sealed class PrintLayoutPresetTests
{
    [Fact]
    public void Empty_or_old_package_uses_the_exact_original_python_sheet_defaults()
    {
        var old = new Dictionary<string, JsonElement>
        {
            ["create.quantity"] = JsonSerializer.SerializeToElement("25")
        };
        var layout = PrintLayoutPresetCodec.FromSettings(old);
        Assert.Equal(new PrintLayoutOptions(), layout);
        Assert.Equal(60, PrintLayouts.Plan(layout, 61).PerSheet);
        Assert.Equal(2, PrintLayouts.Plan(layout, 61).PdfPages);
    }

    [Fact]
    public void All_geometry_fields_round_trip_and_match_native_print_page_plan()
    {
        var options = new PrintLayoutOptions(
            CardWidthMm: 45.5, CardHeightMm: 18.25,
            Rows: 9, Columns: 3, Paper: "A3",
            Orientation: "landscape", HorizontalGapMm: 1.5,
            VerticalGapMm: 2.25, MarginMm: 4.0,
            Order: "column_right", Duplex: "short-edge");

        var saved = PrintLayoutPresetCodec.ToSettings(options);
        Assert.Equal(11, saved.Count);
        Assert.Equal(options, PrintLayoutPresetCodec.FromSettings(saved));
        var native = NativePrintJobContract.Create(
            [new Voucher("0001", "user01", "pw01"),
             new Voucher("0002", "user02", "pw02")], options);
        var pages = PrintLayouts.Plan(
            PrintLayoutPresetCodec.FromSettings(saved), 2);
        Assert.Equal(native.PageCount, pages.PdfPages);
        Assert.Equal(native.Geometry, pages.Layout);
        Assert.Equal(
            native.Placements.ToArray(),
            PrintLayouts.Placements(options, 2).ToArray());
    }

    [Fact]
    public void Legacy_numeric_string_values_are_accepted_without_losing_geometry()
    {
        var settings = new Dictionary<string, JsonElement>
        {
            ["card.width_mm"] = JsonSerializer.SerializeToElement("49.5"),
            ["card.height_mm"] = JsonSerializer.SerializeToElement("18.0"),
            ["print.rows"] = JsonSerializer.SerializeToElement("10"),
            ["print.columns"] = JsonSerializer.SerializeToElement("4"),
            ["print.gap_h_mm"] = JsonSerializer.SerializeToElement("0.5"),
            ["print.gap_v_mm"] = JsonSerializer.SerializeToElement("0.7"),
            ["print.page_margin_mm"] = JsonSerializer.SerializeToElement("1")
        };
        var layout = PrintLayoutPresetCodec.FromSettings(settings);
        Assert.Equal(49.5, layout.CardWidthMm);
        Assert.Equal(18, layout.CardHeightMm);
        Assert.Equal(10, layout.Rows);
        Assert.Equal(4, layout.Columns);
        Assert.Equal(0.7, layout.VerticalGapMm);
    }

    [Theory]
    [InlineData("print.rows", "\"4.5\"")]
    [InlineData("print.columns", "0")]
    [InlineData("card.width_mm", "0")]
    [InlineData("card.height_mm", "\"NaN\"")]
    [InlineData("print.gap_h_mm", "-1")]
    [InlineData("print.page_margin_mm", "500")]
    [InlineData("print.paper", "\"A0\"")]
    [InlineData("print.orientation", "\"sideways\"")]
    [InlineData("print.order", "\"unlisted\"")]
    [InlineData("print.duplex", "\"flip\"")]
    [InlineData("print.rows", "null")]
    [InlineData("print.columns", "true")]
    public void Corrupt_or_unsupported_package_print_option_is_rejected_not_silently_defaulted(
        string key, string json)
    {
        var bad = new Dictionary<string, JsonElement>
        {
            [key] = JsonDocument.Parse(json).RootElement.Clone()
        };
        Assert.Throws<PrintLayoutException>(() =>
            PrintLayoutPresetCodec.FromSettings(bad));
    }

    [Fact]
    public void Oversized_sheet_is_rejected_before_preset_save_or_printer_submission()
    {
        Assert.Throws<PrintLayoutException>(() =>
            PrintLayoutPresetCodec.ToSettings(new PrintLayoutOptions(
                CardWidthMm: 200, CardHeightMm: 250,
                Rows: 2, Columns: 2)));
        var invalid = PrintLayoutPresetCodec.ToSettings(new PrintLayoutOptions());
        invalid["print.rows"] = JsonSerializer.SerializeToElement(1000);
        Assert.Throws<PrintLayoutException>(() => PrintLayoutPresetCodec.FromSettings(invalid));
    }

    [Fact]
    public void Two_packages_keep_independent_print_geometry_and_restore_it_after_restart()
    {
        string root = Path.Combine(Path.GetTempPath(), "hnet-layout-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "hadramout-net.db");
        try
        {
            var store = new PackageSettingsStore(file);
            var first = new PrintLayoutOptions(Rows: 10, Columns: 4,
                CardWidthMm: 49, CardHeightMm: 18);
            var second = new PrintLayoutOptions(Rows: 6, Columns: 3,
                CardWidthMm: 67, CardHeightMm: 20,
                Orientation: "landscape", Paper: "A4");

            var a = PrintLayoutPresetCodec.ToSettings(first);
            a["create.quantity"] = JsonSerializer.SerializeToElement("100");
            store.SavePackage("bronze", a);
            var b = PrintLayoutPresetCodec.ToSettings(second);
            b["create.quantity"] = JsonSerializer.SerializeToElement("35");
            store.SavePackage("gold", b);

            var restarted = new PackageSettingsStore(file);
            var records = restarted.ListPackages();
            Assert.Equal(2, records.Count);
            Assert.Equal(first, PrintLayoutPresetCodec.FromSettings(
                Assert.Single(records, row => row.Name == "bronze").Settings));
            Assert.Equal(second, PrintLayoutPresetCodec.FromSettings(
                Assert.Single(records, row => row.Name == "gold").Settings));
            Assert.Equal("100", Assert.Single(records, row => row.Name == "bronze")
                .Settings["create.quantity"].GetString());
            Assert.Equal("35", Assert.Single(records, row => row.Name == "gold")
                .Settings["create.quantity"].GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
