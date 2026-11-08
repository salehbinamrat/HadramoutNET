using HadramoutNET.Core.Printing;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>
/// Exact physical-grid and print-order contracts derived from the pinned
/// Python reference core/printing.py: page_size, auto_grid, calculate_layout,
/// print_plan, _slot and card_pages. No printer is opened by these tests.
/// </summary>
public sealed class PrintLayoutParityTests
{
    private static readonly PrintLayoutOptions Default = new();

    [Fact]
    public void A4_defaults_match_python_reportlab_geometry_in_points()
    {
        var layout = PrintLayouts.Calculate(Default);
        Assert.Equal(595.2755905511812, layout.PageWidth, 8);
        Assert.Equal(841.8897637795277, layout.PageHeight, 8);
        Assert.Equal(60, layout.PerPage);
        Assert.Equal(12.047244094488178, layout.StartX, 8);
        Assert.Equal(834.8031496062993, layout.TopY, 8);

        var slots = PrintLayouts.Placements(Default, 60).ToArray();
        Assert.Equal(60, slots.Length);
        Assert.Equal(12.047244094488178, slots[0].X, 8);
        Assert.Equal(780.9448818897638, slots[0].Y, 8);
        Assert.Equal(441.496062992126, slots[59].X, 8);
        Assert.Equal(7.086614173228327, slots[59].Y, 8);
        Assert.Equal(1L, slots[0].PdfPage);
    }

    [Theory]
    [InlineData("A4", "portrait", 595.2755905511812, 841.8897637795277)]
    [InlineData("A4", "landscape", 841.8897637795277, 595.2755905511812)]
    [InlineData("A3", "portrait", 841.8897637795277, 1190.5511811023623)]
    [InlineData("A3", "landscape", 1190.5511811023623, 841.8897637795277)]
    public void Paper_and_orientation_use_reportlab_compatible_points(
        string paper, string orientation, double width, double height)
    {
        var geometry = PrintLayouts.Calculate(Default with
        {
            Paper = paper, Orientation = orientation, Rows = 1, Columns = 1
        });
        Assert.Equal(width, geometry.PageWidth, 8);
        Assert.Equal(height, geometry.PageHeight, 8);
    }

    [Fact]
    public void Sheet_and_pdf_page_counts_agree_with_duplex()
    {
        var plain = PrintLayouts.Plan(Default, 61);
        var doubleSided = PrintLayouts.Plan(Default with { Duplex = "long-edge" }, 61);
        Assert.Equal(60, plain.PerSheet);
        Assert.Equal(2L, plain.Sheets);
        Assert.Equal(2L, plain.PdfPages);
        Assert.Equal(2L, doubleSided.Sheets);
        Assert.Equal(4L, doubleSided.PdfPages);
        Assert.Equal(0L, PrintLayouts.Plan(Default, 0).PdfPages);
        Assert.Equal(0L, PrintLayouts.Plan(Default, -2).Sheets);
        Assert.Equal(4, PrintLayouts.Placements(Default with
        {
            Rows = 1, Columns = 2, Duplex = "long-edge"
        }, 2).Count());
    }

    [Theory]
    [InlineData("left_to_right", 0, 1, 2, 3)]
    [InlineData("right_to_left", 1, 0, 3, 2)]
    [InlineData("snake", 0, 1, 3, 2)]
    [InlineData("column_left", 0, 2, 1, 3)]
    [InlineData("column_right", 1, 3, 0, 2)]
    public void All_five_original_layout_orders_match_their_physical_cell_order(
        string order, int a, int b, int c, int d)
    {
        var config = Default with { Rows = 2, Columns = 2, Order = order };
        var positions = PrintLayouts.Placements(config, 4).ToArray();
        var grid = PrintLayouts.Calculate(config);
        var left = grid.StartX;
        var right = left + grid.CardWidth + grid.GapX;
        var top = grid.TopY - grid.CardHeight;
        var bottom = top - grid.CardHeight - grid.GapY;
        var expected = new (double X, double Y)[]
        {
            (left, top), (right, top), (left, bottom), (right, bottom)
        };
        int[] cells = [a, b, c, d];
        for (int i = 0; i < cells.Length; i++)
        {
            Assert.Equal(expected[cells[i]].X, positions[i].X, 8);
            Assert.Equal(expected[cells[i]].Y, positions[i].Y, 8);
        }
    }

    [Theory]
    [InlineData("portrait", "long-edge", true)]
    [InlineData("portrait", "short-edge", false)]
    [InlineData("landscape", "long-edge", false)]
    [InlineData("landscape", "short-edge", true)]
    public void Duplex_back_mirrors_the_expected_axis(
        string orientation, string duplex, bool mirrorX)
    {
        var config = Default with
        {
            Rows = 1, Columns = 2, Orientation = orientation, Duplex = duplex
        };
        var geometry = PrintLayouts.Calculate(config);
        var places = PrintLayouts.Placements(config, 2).ToArray();
        Assert.Equal(4, places.Length);
        for (int i = 0; i < 2; i++)
        {
            var front = places[i];
            var back = places[i + 2];
            Assert.False(front.IsBack);
            Assert.True(back.IsBack);
            Assert.Equal(1L, front.PdfPage);
            Assert.Equal(2L, back.PdfPage);
            Assert.Equal(front.CardIndex, back.CardIndex);
            Assert.Equal(mirrorX ? geometry.PageWidth - front.X - geometry.CardWidth : front.X,
                         back.X, 8);
            Assert.Equal(mirrorX ? front.Y : geometry.PageHeight - front.Y - geometry.CardHeight,
                         back.Y, 8);
        }
    }

    [Fact]
    public void Second_sheet_keeps_group_positions_and_pdf_numbering()
    {
        var config = Default with { Rows = 1, Columns = 2, Duplex = "short-edge" };
        var places = PrintLayouts.Placements(config, 3).ToArray();
        Assert.Equal(6, places.Length);
        Assert.Equal(new long[] { 1, 1, 2, 2, 3, 4 }, places.Select(p => p.PdfPage).ToArray());
        Assert.Equal(new int[] { 0, 1, 0, 1, 2, 2 }, places.Select(p => p.CardIndex).ToArray());
        Assert.Equal(places[0].X, places[4].X, 8);
        Assert.Equal(places[0].Y, places[4].Y, 8);
    }

    [Fact]
    public void Auto_grid_returns_same_max_fitting_rows_and_columns()
    {
        var grid = PrintLayouts.AutoGrid(Default);
        Assert.Equal(15, grid.Rows);
        Assert.Equal(4, grid.Columns);
        Assert.Equal((1, 1), PrintLayouts.AutoGrid(Default with
        {
            CardWidthMm = 300, CardHeightMm = 300
        }));
    }

    [Theory]
    [InlineData(0, 19, 1, 1, 0.5, 0.5, 0.5)]
    [InlineData(50, 0, 1, 1, 0.5, 0.5, 0.5)]
    [InlineData(50, 19, 0, 1, 0.5, 0.5, 0.5)]
    [InlineData(50, 19, 1, 1001, 0.5, 0.5, 0.5)]
    [InlineData(50, 19, 1, 1, -0.1, 0.5, 0.5)]
    [InlineData(50, 19, 1, 1, 0.5, 300.1, 0.5)]
    [InlineData(50, 19, 1, 1, 0.5, 0.5, -1)]
    public void Unsafe_numbers_are_rejected_before_printing(
        double width, double height, int rows, int columns,
        double gapX, double gapY, double margin)
    {
        Assert.Throws<PrintLayoutException>(() => PrintLayouts.Calculate(
            Default with
            {
                CardWidthMm = width, CardHeightMm = height, Rows = rows,
                Columns = columns, HorizontalGapMm = gapX,
                VerticalGapMm = gapY, MarginMm = margin
            }));
    }

    [Fact]
    public void Nonfinite_and_oversized_grids_are_rejected()
    {
        Assert.Throws<PrintLayoutException>(() => PrintLayouts.Calculate(
            Default with { HorizontalGapMm = double.NaN }));
        var large = Assert.Throws<PrintLayoutException>(() => PrintLayouts.Calculate(
            Default with { CardWidthMm = 300, Rows = 2, Columns = 2 }));
        Assert.Contains("المقترح", large.Message);
    }

    [Theory]
    [InlineData("invalid", "portrait", "left_to_right", "single")]
    [InlineData("A4", "reverse", "left_to_right", "single")]
    [InlineData("A4", "portrait", "unlisted", "single")]
    [InlineData("A4", "portrait", "left_to_right", "flip-top")]
    public void Unsupported_modes_are_rejected(
        string paper, string orientation, string order, string duplex)
    {
        Assert.Throws<PrintLayoutException>(() => PrintLayouts.Plan(Default with
        {
            Paper = paper, Orientation = orientation, Order = order, Duplex = duplex
        }, 1));
    }

    [Fact]
    public void Duplicate_and_blank_credentials_are_refused_before_physical_print()
    {
        Assert.Throws<PrintLayoutException>(() =>
            PrintLayouts.ValidateUsernames(["a", "b", "a"]));
        Assert.Throws<PrintLayoutException>(() =>
            PrintLayouts.ValidateUsernames(["a", " "]));
        PrintLayouts.ValidateUsernames(["a", "b", "A"]);
    }
}
