using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HadramoutNET.Core.Printing;
using HadramoutNET.Core.Visual;

namespace HadramoutNET.App;

/// <summary>
/// Interactive position preview for the original card artwork editor.
/// Dragging changes the same X/Y fields used by package persistence and all
/// WPF/PDF/physical print renderers; nothing is saved until Accept is clicked.
/// </summary>
internal static class CardArtworkDragPreview
{
    private const double CanvasWidth = 560;
    private const double CanvasHeight = 260;
    private const double MarkerWidth = 80;
    private const double MarkerHeight = 20;

    public static FrameworkElement Create(
        IReadOnlyDictionary<string, (TextBox X, TextBox Y)> positions,
        IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(labels);

        var host = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
        host.Children.Add(new TextBlock
        {
            Text = "معاينة تفاعلية لمواضع العناصر — اختر العنصر واسحبه بالماوس",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 3, 0, 7)
        });
        var selector = new ComboBox
        {
            Width = 260, Height = 34, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(3, 0, 3, 7)
        };
        foreach ((string key, string label) in labels)
        {
            if (positions.ContainsKey(key))
                selector.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        }

        var canvas = new Canvas
        {
            Width = CanvasWidth, Height = CanvasHeight,
            FlowDirection = FlowDirection.LeftToRight,
            Background = new SolidColorBrush(Color.FromRgb(255, 250, 245)),
            ClipToBounds = true
        };
        var gridLines = new[] { 0d, 70d, 140d, 210d, 280d };
        foreach (double at in gridLines)
        {
            var line = new System.Windows.Shapes.Line
            {
                X1 = at * 2, Y1 = 0, X2 = at * 2, Y2 = CanvasHeight,
                Stroke = new SolidColorBrush(Color.FromRgb(236, 221, 213)),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            canvas.Children.Add(line);
        }

        var marker = new Border
        {
            Width = MarkerWidth, Height = MarkerHeight,
            Background = new SolidColorBrush(Color.FromArgb(243, 255, 220, 230)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(140, 16, 44)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Cursor = Cursors.SizeAll
        };
        var markerLabel = new TextBlock
        {
            FontFamily = new FontFamily("Arial"),
            FontWeight = FontWeights.Bold,
            FontSize = 10,
            FlowDirection = FlowDirection.RightToLeft,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(110, 12, 37))
        };
        marker.Child = markerLabel;
        canvas.Children.Add(marker);

        var border = new Border
        {
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(182, 126, 139)),
            CornerRadius = new CornerRadius(4),
            Child = canvas,
            HorizontalAlignment = HorizontalAlignment.Right,
            Width = CanvasWidth + 4,
            Height = CanvasHeight + 4
        };
        var notice = new TextBlock
        {
            Text = "هذه معاينة إرشادية لموضع عنصر واحد وليست محاكاة كاملة للطباعة. " +
                   "تُطبّق الإحداثيات على المعاينة الفعلية وPDF والطابعة بعد اعتماد التصميم. " +
                   "النقر دون سحب لا يغيّر الموضع.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 3)
        };

        string? activeKey = null;
        bool dragging = false;
        Point down = default;
        CardArtPoint original = default;

        void Refresh()
        {
            if (selector.SelectedItem is not ComboBoxItem selected ||
                selected.Tag is not string key || !positions.TryGetValue(key, out var fields))
            {
                activeKey = null;
                marker.Visibility = Visibility.Collapsed;
                return;
            }
            activeKey = key;
            markerLabel.Text = labels[key];
            marker.ToolTip = "اسحب لتعديل " + labels[key];
            if (!TechnicalNumberParser.TryFiniteDouble(fields.X.Text, out double x) ||
                !TechnicalNumberParser.TryFiniteDouble(fields.Y.Text, out double y) ||
                !double.IsFinite(x) || !double.IsFinite(y) ||
                x is < 0 or > CardLayerDragGeometry.ReferenceWidth ||
                y is < 0 or > CardLayerDragGeometry.ReferenceHeight)
            {
                marker.Visibility = Visibility.Collapsed;
                notice.Text = "إحداثيات العنصر المختار غير صالحة؛ صحح حقلي X وY ثم أعد السحب.";
                return;
            }
            marker.Visibility = Visibility.Visible;
            Canvas.SetLeft(marker, x / CardLayerDragGeometry.ReferenceWidth * CanvasWidth);
            Canvas.SetTop(marker, y / CardLayerDragGeometry.ReferenceHeight * CanvasHeight);
            notice.Text = "موضع العنصر المختار X=" + x.ToString("G", CultureInfo.InvariantCulture) +
                "، Y=" + y.ToString("G", CultureInfo.InvariantCulture) +
                ". المعاينة إرشادية؛ استخدم معاينة الورقة للتحقق من الطباعة الكاملة.";
        }

        void ApplyDrag(Point pointer)
        {
            if (!dragging || activeKey is null ||
                !positions.TryGetValue(activeKey, out var fields)) return;
            double dx = pointer.X - down.X;
            double dy = pointer.Y - down.Y;
            if (dx == 0 && dy == 0) return;
            try
            {
                CardArtPoint next = CardLayerDragGeometry.Move(original, dx, dy,
                    CanvasWidth, CanvasHeight, MarkerWidth, MarkerHeight);
                fields.X.Text = next.X.ToString("0.###", CultureInfo.InvariantCulture);
                fields.Y.Text = next.Y.ToString("0.###", CultureInfo.InvariantCulture);
            }
            catch (PrintLayoutException)
            {
                // Invalid mouse geometry never becomes a saved coordinate.
            }
        }

        selector.SelectionChanged += (_, _) =>
        {
            dragging = false;
            if (marker.IsMouseCaptured) marker.ReleaseMouseCapture();
            Refresh();
        };
        foreach ((string key, (TextBox x, TextBox y)) in positions)
        {
            x.TextChanged += (_, _) => { if (activeKey == key) Refresh(); };
            y.TextChanged += (_, _) => { if (activeKey == key) Refresh(); };
        }
        marker.MouseLeftButtonDown += (_, e) =>
        {
            if (activeKey is null || !positions.TryGetValue(activeKey, out var fields) ||
                !TechnicalNumberParser.TryFiniteDouble(fields.X.Text, out double x) ||
                !TechnicalNumberParser.TryFiniteDouble(fields.Y.Text, out double y) ||
                x is < 0 or > CardLayerDragGeometry.ReferenceWidth ||
                y is < 0 or > CardLayerDragGeometry.ReferenceHeight) return;
            original = new CardArtPoint(x, y);
            down = e.GetPosition(canvas);
            dragging = marker.CaptureMouse();
            e.Handled = dragging;
        };
        marker.MouseMove += (_, e) =>
        {
            if (dragging && e.LeftButton == MouseButtonState.Pressed)
                ApplyDrag(e.GetPosition(canvas));
        };
        marker.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            ApplyDrag(e.GetPosition(canvas));
            dragging = false;
            if (marker.IsMouseCaptured) marker.ReleaseMouseCapture();
            e.Handled = true;
        };
        marker.LostMouseCapture += (_, _) => dragging = false;

        host.Children.Add(selector);
        host.Children.Add(border);
        host.Children.Add(notice);
        if (selector.Items.Count > 0) selector.SelectedIndex = 0;
        return host;
    }
}
