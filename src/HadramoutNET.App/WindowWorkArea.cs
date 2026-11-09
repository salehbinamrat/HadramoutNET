using System.Windows;
using HadramoutNET.Core.Visual;

namespace HadramoutNET.App;

internal static class WindowWorkArea
{
    // Run on SourceInitialized, before the first visible frame.
    internal static void Fit(Window window)
    {
        Rect desktop = SystemParameters.WorkArea;
        DesktopWindowSize size = DesktopWindowSizing.Fit(
            window.Width, window.Height, desktop.Width, desktop.Height);
        window.MinWidth = Math.Min(window.MinWidth, size.Width);
        window.MinHeight = Math.Min(window.MinHeight, size.Height);
        window.Width = size.Width;
        window.Height = size.Height;
    }
}
