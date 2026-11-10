using HadramoutNET.Core.Printing;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class CardLayerDragGeometryTests
{
    [Fact]
    public void Drag_maps_canvas_pixels_to_original_280_by_130_coordinates()
    {
        CardArtPoint moved = CardLayerDragGeometry.Move(
            new CardArtPoint(50, 35), deltaX: 20, deltaY: 26,
            canvasWidth: 560, canvasHeight: 260, markerWidth: 80, markerHeight: 20);
        Assert.Equal(60, moved.X);
        Assert.Equal(48, moved.Y);
    }

    [Fact]
    public void Drag_clamps_entire_selection_marker_inside_the_preview()
    {
        CardArtPoint moved = CardLayerDragGeometry.Move(
            new CardArtPoint(220, 115), deltaX: 500, deltaY: 500,
            canvasWidth: 560, canvasHeight: 260, markerWidth: 80, markerHeight: 20);
        Assert.Equal(240, moved.X);
        Assert.Equal(120, moved.Y);

        CardArtPoint before = CardLayerDragGeometry.Move(
            new CardArtPoint(12, 13), deltaX: -500, deltaY: -500,
            canvasWidth: 560, canvasHeight: 260, markerWidth: 80, markerHeight: 20);
        Assert.Equal(0, before.X);
        Assert.Equal(0, before.Y);
    }

    [Fact]
    public void Clicking_without_drag_must_not_shift_even_an_old_edge_position()
    {
        CardArtPoint original = new(280, 130);
        Assert.Equal(original, CardLayerDragGeometry.Move(original, 0, 0,
            560, 260, 80, 20));
    }

    [Theory]
    [InlineData(0, 260, 80, 20)]
    [InlineData(560, 0, 80, 20)]
    [InlineData(560, 260, 561, 20)]
    [InlineData(560, 260, 80, 261)]
    [InlineData(double.NaN, 260, 80, 20)]
    public void Invalid_preview_geometry_fails_before_writing_coordinates(
        double width, double height, double markerWidth, double markerHeight)
    {
        Assert.Throws<PrintLayoutException>(() =>
            CardLayerDragGeometry.Move(new CardArtPoint(20, 40), 1, 2,
                width, height, markerWidth, markerHeight));
    }

    [Fact]
    public void Non_finite_mouse_offset_is_rejected()
    {
        Assert.Throws<PrintLayoutException>(() =>
            CardLayerDragGeometry.Move(new CardArtPoint(20, 40),
                double.PositiveInfinity, 0, 560, 260, 80, 20));
    }
}
