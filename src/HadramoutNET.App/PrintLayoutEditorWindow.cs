using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HadramoutNET.Core.Printing;

namespace HadramoutNET.App;

/// <summary>
/// Explicit editor for the physical paper/card layout used by both PDF and
/// native Windows printing. Changes are staged until accepted; persistence
/// happens only when the operator saves the selected package.
/// </summary>
internal static class PrintLayoutEditorWindow
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal static PrintLayoutOptions? Edit(Window owner, PrintLayoutOptions current)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(current);
        PrintLayoutOptions? chosen = null;
        var dialog = new Window
        {
            Title = "إعدادات تخطيط طباعة الكروت",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FlowDirection = FlowDirection.RightToLeft,
            Width = 580,
            Height = 700,
            MinWidth = 490,
            MinHeight = 510
        };

        var form = new Grid { Margin = new Thickness(15) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(195) });
        for (int i = 0; i < 11; i++)
            form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });

        static TextBox Numeric(double value) => new()
        {
            Text = value.ToString("G17", CultureInfo.InvariantCulture),
            Height = 32, MinWidth = 160, MaxLength = 30
        };
        static TextBox Integer(int value) => new()
        {
            Text = value.ToString(CultureInfo.InvariantCulture),
            Height = 32, MinWidth = 160, MaxLength = 8
        };

        static ComboBox Choice(string value, params (string Label, string Key)[] options)
        {
            var combo = new ComboBox { Height = 32, MinWidth = 160 };
            foreach ((string label, string key) in options)
            {
                var option = new ComboBoxItem { Content = label, Tag = key };
                combo.Items.Add(option);
                if (value == key) combo.SelectedItem = option;
            }
            return combo;
        }
        static void Add(Grid grid, int row, string name, Control field)
        {
            var caption = new TextBlock
            {
                Text = name,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(caption, row);
            Grid.SetColumn(caption, 0);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            grid.Children.Add(caption);
            grid.Children.Add(field);
        }

        var width = Numeric(current.CardWidthMm);
        var height = Numeric(current.CardHeightMm);
        var rows = Integer(current.Rows);
        var columns = Integer(current.Columns);
        var gapH = Numeric(current.HorizontalGapMm);
        var gapV = Numeric(current.VerticalGapMm);
        var margin = Numeric(current.MarginMm);
        var paper = Choice(current.Paper.ToUpperInvariant(),
            ("A4", "A4"), ("A3", "A3"));
        var orientation = Choice(current.Orientation,
            ("عمودي", "portrait"), ("أفقي", "landscape"));
        var order = Choice(current.Order,
            ("من اليسار إلى اليمين", "left_to_right"),
            ("من اليمين إلى اليسار", "right_to_left"),
            ("متعرج", "snake"),
            ("أعمدة من اليسار", "column_left"),
            ("أعمدة من اليمين", "column_right"));
        var duplex = Choice(current.Duplex,
            ("وجه واحد", "single"),
            ("وجهين - الحافة الطويلة", "long-edge"),
            ("وجهين - الحافة القصيرة", "short-edge"));

        Add(form, 0, "عرض الكرت (مم)", width);
        Add(form, 1, "ارتفاع الكرت (مم)", height);
        Add(form, 2, "عدد الصفوف", rows);
        Add(form, 3, "عدد الأعمدة", columns);
        Add(form, 4, "الورق", paper);
        Add(form, 5, "اتجاه الورق", orientation);
        Add(form, 6, "الفاصل الأفقي (مم)", gapH);
        Add(form, 7, "الفاصل الرأسي (مم)", gapV);
        Add(form, 8, "هامش الورقة (مم)", margin);
        Add(form, 9, "ترتيب توزيع الكروت", order);
        Add(form, 10, "الطباعة على الوجهين", duplex);

        var notice = new TextBlock
        {
            Text = "تطبق الخيارات على معاينة الورقة وPDF والطباعة المباشرة. " +
                   "للحفظ الدائم اضغط «حفظ إعدادات إنشاء الكروت» من صفحة الباقات.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 12, 8, 10)
        };
        var feedback = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DarkRed,
            Margin = new Thickness(8, 0, 8, 8)
        };

        var accept = new Button { Content = "اعتماد التخطيط", Width = 145, Height = 38,
            Margin = new Thickness(4) };
        var cancel = new Button { Content = "إلغاء", Width = 90, Height = 38,
            Margin = new Thickness(4), IsCancel = true };

        static double ParseNumber(TextBox field)
        {
            if (!double.TryParse(field.Text.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double value) ||
                !double.IsFinite(value))
                throw new PrintLayoutException("أدخل قيمة رقمية صحيحة ومحدودة.");
            return value;
        }
        static int ParseCount(TextBox field)
        {
            if (!int.TryParse(field.Text.Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int value))
                throw new PrintLayoutException("أدخل عددا صحيحا للصفوف والأعمدة.");
            return value;
        }
        static string Selected(ComboBox combo) =>
            (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
                throw new PrintLayoutException("اختر قيمة مدعومة من القائمة.");

        accept.Click += (_, _) =>
        {
            try
            {
                var candidate = new PrintLayoutOptions(
                    CardWidthMm: ParseNumber(width),
                    CardHeightMm: ParseNumber(height),
                    Rows: ParseCount(rows),
                    Columns: ParseCount(columns),
                    Paper: Selected(paper),
                    Orientation: Selected(orientation),
                    HorizontalGapMm: ParseNumber(gapH),
                    VerticalGapMm: ParseNumber(gapV),
                    MarginMm: ParseNumber(margin),
                    Order: Selected(order),
                    Duplex: Selected(duplex));
                // The Python-compatible geometry validator refuses overflow,
                // unsupported modes or cards that do not fit the actual paper.
                _ = PrintLayouts.Plan(candidate, 1);
                chosen = candidate;
                dialog.DialogResult = true;
            }
            catch (PrintLayoutException error)
            {
                feedback.Text = error.Message;
            }
        };
        cancel.Click += (_, _) => dialog.DialogResult = false;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);
        var content = new StackPanel();
        content.Children.Add(form);
        content.Children.Add(notice);
        content.Children.Add(feedback);
        content.Children.Add(buttons);
        dialog.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content
        };
        return dialog.ShowDialog() == true ? chosen : null;
    }
}
