namespace HadramoutNET.Core.Printing;

/// <summary>
/// Translate a mouse drag on a WPF preview into the original Python
/// 280×130 card coordinates, preserving the mouse-down anchor.
/// This is geometry only: it never changes a saved design or RouterOS data.
/// </summary>
public static class CardLayerDragGeometry
{
    public const double ReferenceWidth = 280;
    public const double ReferenceHeight = 130;

    public static CardArtPoint Move(
        CardArtPoint origin, double deltaX, double deltaY,
        double canvasWidth, double canvasHeight,
        double markerWidth, double markerHeight)
    {
        if (!double.IsFinite(canvasWidth) || !double.IsFinite(canvasHeight) ||
            !double.IsFinite(markerWidth) || !double.IsFinite(markerHeight) ||
            canvasWidth <= 0 || canvasHeight <= 0 ||
            markerWidth <= 0 || markerHeight <= 0 ||
            markerWidth > canvasWidth || markerHeight > canvasHeight ||
            !double.IsFinite(origin.X) || !double.IsFinite(origin.Y) ||
            origin.X is < 0 or > ReferenceWidth ||
            origin.Y is < 0 or > ReferenceHeight ||
            !double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            throw new PrintLayoutException("أبعاد المعاينة أو حركة عنصر الكرت غير صالحة.");

        // The old Python editor explicitly preserves a no-motion click; a
        // coordinate at the card edge must not drift merely on selection.
        if (deltaX == 0 && deltaY == 0) return origin;

        double cardX = origin.X / ReferenceWidth * canvasWidth;
        double cardY = origin.Y / ReferenceHeight * canvasHeight;
        double movedX = Math.Clamp(cardX + deltaX, 0, canvasWidth - markerWidth);
        double movedY = Math.Clamp(cardY + deltaY, 0, canvasHeight - markerHeight);
        return new CardArtPoint(
            Math.Round(movedX / canvasWidth * ReferenceWidth, 3,
                MidpointRounding.AwayFromZero),
            Math.Round(movedY / canvasHeight * ReferenceHeight, 3,
                MidpointRounding.AwayFromZero));
    }
}
