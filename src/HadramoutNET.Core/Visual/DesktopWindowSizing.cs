namespace HadramoutNET.Core.Visual;

/// <summary>A WPF-device-independent window size, in display units (DIP).</summary>
public readonly record struct DesktopWindowSize(double Width, double Height);

/// <summary>
/// Clamp initial window bounds within the currently available desktop work area,
/// leaving an inset so the window frame and taskbar never hide actions.
/// </summary>
public static class DesktopWindowSizing
{
    public static DesktopWindowSize Fit(double preferredWidth, double preferredHeight,
        double availableWidth, double availableHeight)
    {
        if (!Valid(preferredWidth) || !Valid(preferredHeight) ||
            !Valid(availableWidth) || !Valid(availableHeight))
            throw new ArgumentOutOfRangeException(nameof(availableWidth),
                "Window and work-area dimensions must be finite and positive.");
        return new DesktopWindowSize(
            Math.Min(preferredWidth, Math.Max(1, availableWidth - 24)),
            Math.Min(preferredHeight, Math.Max(1, availableHeight - 24)));
    }

    private static bool Valid(double value) => double.IsFinite(value) && value > 0;
}
