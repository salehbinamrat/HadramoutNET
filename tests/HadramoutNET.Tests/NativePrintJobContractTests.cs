using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Printing;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class NativePrintJobContractTests
{
    private static readonly Voucher[] Cards =
    [
        new("0001", "voucher1", ""),
        new("0002", "voucher2", "secret2"),
        new("0003", "voucher3", "")
    ];

    [Fact]
    public void Windows_print_is_one_explicit_copy_with_A4_geometry_and_no_automatic_submission()
    {
        var request = NativePrintJobContract.Create(Cards);
        Assert.Equal("A4", request.Paper);
        Assert.Equal("portrait", request.Orientation);
        Assert.Equal("single", request.Duplex);
        Assert.Equal(1, request.Copies);
        Assert.False(request.Submitted);
        Assert.Equal(1, request.PageCount);
        Assert.Equal(3, request.VoucherCount);
        Assert.Equal(PrintLayouts.Plan(new PrintLayoutOptions(), 3).PdfPages, request.PageCount);
    }

    [Fact]
    public void Duplex_A3_and_landscape_preserve_native_page_order()
    {
        var settings = new PrintLayoutOptions(
            CardWidthMm: 50, CardHeightMm: 19,
            Rows: 1, Columns: 2, Paper: "A3",
            Orientation: "landscape", Duplex: "long-edge");
        var request = NativePrintJobContract.Create(Cards, settings);
        Assert.Equal("A3", request.Paper);
        Assert.Equal("landscape", request.Orientation);
        Assert.Equal("long-edge", request.Duplex);
        Assert.Equal(4, request.PageCount);
        Assert.Equal(1, request.Copies);
        Assert.All(request.Placements, p => Assert.InRange(p.CardIndex, 0, 2));
        Assert.Equal(3, request.Placements.Count(p => p.IsBack));
    }

    [Fact]
    public void Invalid_cards_or_geometry_refuse_native_print_before_driver_dialog()
    {
        Assert.Throws<PrintLayoutException>(() => NativePrintJobContract.Create([]));
        Assert.Throws<PrintLayoutException>(() => NativePrintJobContract.Create(
            [new("1", "repeat", ""), new("2", "repeat", "")]));
        Assert.Throws<PrintLayoutException>(() => NativePrintJobContract.Create(
            Cards, new PrintLayoutOptions(Rows: 0)));
        Assert.Throws<PrintLayoutException>(() => NativePrintJobContract.Create(
            Cards, new PrintLayoutOptions(CardWidthMm: 250)));
    }
}
