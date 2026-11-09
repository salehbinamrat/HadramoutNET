using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using HadramoutNET.Core.RouterOS;

namespace HadramoutNET.App;

/// <summary>
/// Source-matching read-only active/expired voucher detail window.
/// Search and page turns are local, never a separate MikroTik request.
/// No deletion is exposed without a fresh server-verified removal workflow.
/// </summary>
internal sealed class UserManagerVoucherStatusWindow : Window
{
    private readonly UserManagerVoucherStatusPager _pager;
    private readonly DataGrid _table;
    private readonly TextBlock _count;
    private readonly TextBlock _pages;
    private readonly Button _previous;
    private readonly Button _next;
    private int _page;

    public UserManagerVoucherStatusWindow(Window owner,
        UserManagerLifecycleSnapshot snapshot, string status,
        string limitsWarning = "")
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(snapshot);
        _pager = new UserManagerVoucherStatusPager(snapshot.Rows, status);
        string name = status == "expired" ? "الكروت المنتهية" : "الكروت النشطة";

        Title = "حالة الكروت - " + name;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = FlowDirection.RightToLeft;
        FontFamily = new FontFamily("Arial");
        FontSize = 13;
        Width = 1040;
        Height = 650;
        MinWidth = 820;
        MinHeight = 500;
        Background = new SolidColorBrush(Color.FromRgb(248, 235, 209));
        ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(9) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var heading = new Border
        {
            Background = Application.Current.Resources["GlossyRed"] as Brush,
            CornerRadius = new CornerRadius(7), Padding = new Thickness(14, 9, 14, 9)
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);
        var title = new DockPanel();
        heading.Child = title;
        var titleLabel = new TextBlock
        {
            Text = name, FontSize = 19, FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(titleLabel, Dock.Right);
        title.Children.Add(titleLabel);
        _count = new TextBlock
        {
            Foreground = Brushes.White, TextAlignment = TextAlignment.Left,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.Children.Add(_count);

        var filterLine = new DockPanel
        {
            Margin = new Thickness(3, 10, 3, 9), LastChildFill = false
        };
        Grid.SetRow(filterLine, 1);
        root.Children.Add(filterLine);
        var text = new TextBlock
        {
            Text = "بحث بالرقم أو الباقة:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6)
        };
        DockPanel.SetDock(text, Dock.Right);
        filterLine.Children.Add(text);
        var search = new TextBox { Width = 320, Height = 34,
            Margin = new Thickness(5) };
        DockPanel.SetDock(search, Dock.Right);
        filterLine.Children.Add(search);
        search.TextChanged += (_, _) =>
        {
            _pager.Search = search.Text;
            _page = 0;
            RenderPage();
        };
        var close = new Button
        {
            Content = "إغلاق", Width = 85, Height = 34,
            Margin = new Thickness(5)
        };
        DockPanel.SetDock(close, Dock.Left);
        close.Click += (_, _) => Close();
        filterLine.Children.Add(close);

        _table = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            SelectionMode = DataGridSelectionMode.Single
        };
        VirtualizingPanel.SetIsVirtualizing(_table, true);
        VirtualizingPanel.SetVirtualizationMode(_table,
            VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_table, true);
        _table.Columns.Add(new DataGridTextColumn
        {
            Header = "اسم المستخدم", Binding = new Binding("Username"),
            Width = new DataGridLength(2, DataGridLengthUnitType.Star)
        });
        _table.Columns.Add(new DataGridTextColumn
        {
            Header = "الباقة", Binding = new Binding("Profile"),
            Width = new DataGridLength(1.5, DataGridLengthUnitType.Star)
        });
        _table.Columns.Add(new DataGridTextColumn
        {
            Header = "سبب الحالة", Binding = new Binding("Reason"),
            Width = new DataGridLength(3, DataGridLengthUnitType.Star)
        });
        _table.Columns.Add(new DataGridTextColumn
        {
            Header = "البيانات المستخدمة (بايت)",
            Binding = new Binding("UsedBytes") { StringFormat = "N0" },
            Width = new DataGridLength(1.5, DataGridLengthUnitType.Star)
        });
        _table.Columns.Add(new DataGridTextColumn
        {
            Header = "Transfer Limit (بايت)",
            Binding = new Binding("TransferLimit") { StringFormat = "N0" },
            Width = new DataGridLength(1.5, DataGridLengthUnitType.Star)
        });
        Grid.SetRow(_table, 2);
        root.Children.Add(_table);

        var footer = new DockPanel { Margin = new Thickness(4, 11, 4, 3) };
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        var warning = new TextBlock
        {
            Text = string.IsNullOrEmpty(limitsWarning) ?
                "عرض فقط من User Manager. انتهاء الوقت وحده لا يعني انتهاء الكرت." :
                "تنبيه: حدود باقات User Manager 6 غير مكتملة. تحقق من صلاحيات الراوتر.",
            Foreground = new SolidColorBrush(Color.FromRgb(124, 15, 36)),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(warning, Dock.Right);
        footer.Children.Add(warning);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        DockPanel.SetDock(controls, Dock.Left);
        footer.Children.Add(controls);

        _previous = new Button { Content = "السابق", Width = 80,
            Height = 35, Margin = new Thickness(5, 0, 5, 0) };
        _previous.Click += (_, _) => { _page--; RenderPage(); };
        _next = new Button { Content = "التالي", Width = 80,
            Height = 35, Margin = new Thickness(5, 0, 5, 0) };
        _next.Click += (_, _) => { _page++; RenderPage(); };
        _pages = new TextBlock
        {
            Width = 155, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        controls.Children.Add(_previous);
        controls.Children.Add(_pages);
        controls.Children.Add(_next);
        RenderPage();
    }

    private void RenderPage()
    {
        _page = Math.Clamp(_page, 0, Math.Max(0, _pager.Pages - 1));
        _table.ItemsSource = _pager.Page(_page);
        _count.Text = $"{_pager.Count:N0} كرت";
        _pages.Text = _pager.Pages == 0 ? "لا توجد نتائج" :
            $"صفحة {_page + 1:N0} من {_pager.Pages:N0}";
        _previous.IsEnabled = _page > 0;
        _next.IsEnabled = _page + 1 < _pager.Pages;
    }
}
