using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Data;
using HadramoutNET.Core.Exporting;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Microsoft.Win32;

namespace HadramoutNET.App;

/// <summary>
/// First functional .NET workspace; source Python application is untouched.
/// All data reads require the currently authenticated, authorized RouterOS
/// session. Draft voucher exports are intentionally offline and reviewed.
/// </summary>
public partial class WorkspaceWindow : Window
{
    private readonly RouterAuthorizedSession _session;
    private readonly PaidLicenseCoordinator _paid;
    private readonly TrialStateManager _trial;
    private readonly Stopwatch _uptime;
    private readonly RouterDashboardReader _dashboard = new();
    private readonly DispatcherTimer _heartbeat = new();
    private readonly CancellationTokenSource _cancel = new();
    private IReadOnlyList<Voucher> _generated = [];
    private PackageSettingsStore? _packages;
    private bool _checking;
    private bool _refreshing;

    private sealed record HotspotRow(
        string Name, string Profile, string Disabled, string LimitUptime, string LimitBytes);

    public WorkspaceWindow(RouterAuthorizedSession session,
        PaidLicenseCoordinator paid, TrialStateManager trial, Stopwatch uptime)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _paid = paid ?? throw new ArgumentNullException(nameof(paid));
        _trial = trial ?? throw new ArgumentNullException(nameof(trial));
        _uptime = uptime ?? throw new ArgumentNullException(nameof(uptime));
        InitializeComponent();
        RouterCaption.Text = $"{session.Identity} — {session.Endpoint}";
        _heartbeat.Interval = TimeSpan.FromSeconds(20);
        _heartbeat.Tick += Heartbeat_Tick;
    }

    private async void WorkspaceLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _packages = new PackageSettingsStore(PackageSettingsStore.DefaultPath(basePath));
            ReloadPackages();
        }
        catch (Exception)
        {
            WorkspaceStatus.Text = "تعذر قراءة إعدادات الباقات؛ لم تُغير البيانات.";
        }

        _heartbeat.Start();
        await LoadDashboardAsync();
    }

    private void ShowDashboard_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 0;
    private void ShowCards_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 1;
    private void ShowHotspot_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 2;
    private void ShowPackages_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 3;
    private void Logout_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshDashboard_Click(object sender, RoutedEventArgs e) =>
        _ = LoadDashboardAsync();
    private void RefreshHotspot_Click(object sender, RoutedEventArgs e) =>
        _ = LoadHotspotAsync();

    private async Task LoadDashboardAsync()
    {
        if (_refreshing || !IsLoaded) return;
        _refreshing = true;
        DashboardNotice.Text = "جارٍ تحديث البيانات من الراوتر...";
        try
        {
            RouterDashboardSnapshot data = await _dashboard.ReadAsync(
                _session, _cancel.Token);
            if (!IsLoaded) return;
            CpuValue.Text = FormatPercent(data.CpuPercent);
            RamValue.Text = FormatPercent(data.MemoryPercent);
            HotspotTotalValue.Text = FormatCount(data.HotspotUsers);
            HotspotActiveValue.Text = FormatCount(data.HotspotActive);
            PppActiveValue.Text = FormatCount(data.PppActive);
            UserManagerValue.Text = FormatCount(data.UserManagerUsers);
            RouterVersionText.Text = "إصدار RouterOS: " + data.RouterOsVersion;
            RouterUptimeText.Text = "وقت تشغيل الراوتر: " + data.Uptime;
            DashboardNotice.Text = data.UnavailableServices.Count == 0 ?
                "تم تحديث بيانات الشبكة بنجاح." :
                "خدمات غير متاحة: " + string.Join("، ", data.UnavailableServices) +
                " (ظهرت بشرطة، لا بصفر).";
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (InvalidOperationException)
        {
            if (IsLoaded) LockWorkspace();
        }
        catch (Exception)
        {
            if (IsLoaded)
                DashboardNotice.Text = "تعذر قراءة البيانات؛ لم يتم تغيير أي إعدادات في MikroTik.";
        }
        finally { _refreshing = false; }
    }

    private async Task LoadHotspotAsync()
    {
        if (!IsLoaded) return;
        WorkspaceStatus.Text = "جارٍ قراءة مستخدمي Hotspot...";
        try
        {
            var read = await _session.ReadAsync("/ip/hotspot/user/print",
                new Dictionary<string, string>
                {
                    [".proplist"] = "name,profile,disabled,limit-uptime,limit-bytes-total"
                }, cancellationToken: _cancel.Token);
            if (!IsLoaded) return;
            UsersGrid.ItemsSource = read.Rows.Select(row => new HotspotRow(
                Field(row, "name"), Field(row, "profile"), Field(row, "disabled"),
                Field(row, "limit-uptime"), Field(row, "limit-bytes-total"))).ToArray();
            WorkspaceStatus.Text = "تم تحميل " +
                read.Rows.Count.ToString(CultureInfo.InvariantCulture) + " مستخدم Hotspot.";
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (InvalidOperationException)
        {
            if (IsLoaded) LockWorkspace();
        }
        catch (Exception)
        {
            if (IsLoaded)
                WorkspaceStatus.Text = "خدمة Hotspot غير متاحة، أو لا تملك صلاحية قراءة المستخدمين.";
        }
    }

    private static string Field(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out string? value) ? value : "";

    private async void Heartbeat_Tick(object? sender, EventArgs e)
    {
        if (_checking || !IsLoaded) return;
        _checking = true;
        try
        {
            if (_paid.NeedsCheck(_uptime.Elapsed))
            {
                var result = await _paid.CheckStoredAsync(DateTimeOffset.UtcNow,
                    _uptime.Elapsed, _cancel.Token);
                if (!result.Permitted)
                    WorkspaceStatus.Text = result.Message;
            }

            _ = _trial.AdvanceVerifiedClock(DateTimeOffset.UtcNow);
            if (!_session.HasCurrentAccess && IsLoaded)
                LockWorkspace();
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_session.HasCurrentAccess && IsLoaded)
                LockWorkspace();
        }
        finally { _checking = false; }
    }

    private void LockWorkspace()
    {
        WorkspaceStatus.Text = "انتهت صلاحية الجلسة. يجب إعادة التحقق وتسجيل الدخول.";
        _heartbeat.Stop();
        Close();
    }

    private void GenerateCards_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmCurrentAccess()) return;
        try
        {
            if (!int.TryParse(QtyBox.Text, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int quantity) ||
                !int.TryParse(CardLengthBox.Text, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int length))
                throw new ArgumentException("الكمية والطول يجب أن يكونا أرقاما إنجليزية صحيحة.");
            string mode = Choice(CardFormatCombo);
            var options = new CardGenerationOptions(
                quantity, length, CardAlphabetBox.Text, mode,
                PasswordLength: 6, PasswordCharacters: "abcdefghijklmnopqrstuvwxyz0123456789");

            // This is a LOCAL draft. A remote uniqueness and profile check is
            // mandatory before actually importing it to the router.
            _generated = VoucherGenerator.Generate(options, _ => false);
            CardGrid.ItemsSource = _generated;
            CardsNotice.Text = $"تم إنشاء {_generated.Count} مسودة؛ لم ترفع إلى MikroTik.";
            WorkspaceStatus.Text = "راجع الأرقام وكلمات المرور قبل تصدير أي ملف.";
        }
        catch (Exception ex) when (ex is CardGenerationException or ArgumentException)
        {
            CardsNotice.Text = ex.Message;
        }
    }

    private static string Choice(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    private IReadOnlyList<IReadOnlyDictionary<string, string?>> CardRows()
    {
        if (_generated.Count == 0)
            throw new InvalidOperationException("أنشئ مسودة كروت قبل التصدير.");
        string service = Choice(CardServiceCombo);
        string profile = CardProfileBox.Text.Trim();
        return _generated.Select(voucher => (IReadOnlyDictionary<string, string?>)
            new Dictionary<string, string?>
            {
                ["sequence"] = voucher.Sequence,
                ["username"] = voucher.Username,
                ["password"] = voucher.Password,
                ["profile"] = profile,
                ["service"] = service,
                ["server"] = "all",
                ["status"] = "draft",
                ["created_at"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz",
                    CultureInfo.InvariantCulture)
            }).ToList();
    }

    private void ExportExcel_Click(object sender, RoutedEventArgs e) =>
        ExportFile("Excel|*.xlsx", ".xlsx", (data, path) =>
            ExcelExporter.SaveExcel(data, path, "كروت حضرموت"));
    private void ExportCsv_Click(object sender, RoutedEventArgs e) =>
        ExportFile("CSV|*.csv", ".csv", CardExports.SaveCsv);
    private void ExportScript_Click(object sender, RoutedEventArgs e) =>
        ExportFile("RouterOS|*.rsc", ".rsc", CardExports.SaveScript);

    private void ExportFile(string filter, string extension,
        Func<IEnumerable<IReadOnlyDictionary<string, string?>>, string, string> writer)
    {
        if (!ConfirmCurrentAccess()) return;
        try
        {
            var cards = CardRows();
            var chooser = new SaveFileDialog
            {
                Title = "حفظ كروت حضرموت",
                FileName = "كروت حضرموت",
                Filter = filter,
                DefaultExt = extension,
                AddExtension = true,
                OverwritePrompt = true
            };
            if (chooser.ShowDialog(this) != true) return;
            writer(cards, chooser.FileName);
            CardsNotice.Text = "تم حفظ الملف بنجاح — لم يتم رفع الكروت إلى الراوتر.";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or
            InvalidOperationException or UnauthorizedAccessException)
        {
            CardsNotice.Text = "تعذر التصدير: " + ex.Message;
        }
    }

    private void SavePackage_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmCurrentAccess() || _packages is null) return;
        try
        {
            var settings = new Dictionary<string, JsonElement>
            {
                ["create.quantity"] = JsonSerializer.SerializeToElement(QtyBox.Text),
                ["create.code_length"] = JsonSerializer.SerializeToElement(CardLengthBox.Text),
                ["create.characters"] = JsonSerializer.SerializeToElement(CardAlphabetBox.Text),
                ["create.format"] = JsonSerializer.SerializeToElement(Choice(CardFormatCombo)),
                ["create.service"] = JsonSerializer.SerializeToElement(Choice(CardServiceCombo)),
                ["create.profile"] = JsonSerializer.SerializeToElement(CardProfileBox.Text)
            };
            _packages.SavePackage(PackageNameBox.Text, settings);
            ReloadPackages();
            WorkspaceStatus.Text = "حُفظت إعدادات هذه الباقة في HadramoutNET فقط.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or
            InvalidOperationException)
        {
            WorkspaceStatus.Text = "تعذر حفظ الباقة: " + ex.Message;
        }
    }

    private void ReloadPackages_Click(object sender, RoutedEventArgs e) => ReloadPackages();

    private void ReloadPackages()
    {
        if (_packages is null) return;
        try { PackagesGrid.ItemsSource = _packages.ListPackages(); }
        catch (Exception) { WorkspaceStatus.Text = "تعذر قراءة الباقات المحلية."; }
    }

    private void PackagesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PackagesGrid.SelectedItem is not PackagePreset selected) return;
        PackageNameBox.Text = selected.Name;
        var values = selected.Settings;
        if (values.TryGetValue("create.quantity", out var q) &&
            q.ValueKind == JsonValueKind.String) QtyBox.Text = q.GetString();
        if (values.TryGetValue("create.code_length", out var len) &&
            len.ValueKind == JsonValueKind.String) CardLengthBox.Text = len.GetString();
        if (values.TryGetValue("create.characters", out var chars) &&
            chars.ValueKind == JsonValueKind.String) CardAlphabetBox.Text = chars.GetString();
        if (values.TryGetValue("create.profile", out var profile) &&
            profile.ValueKind == JsonValueKind.String) CardProfileBox.Text = profile.GetString();
        if (values.TryGetValue("create.format", out var format) &&
            format.ValueKind == JsonValueKind.String) SelectChoice(CardFormatCombo, format.GetString());
        if (values.TryGetValue("create.service", out var service) &&
            service.ValueKind == JsonValueKind.String) SelectChoice(CardServiceCombo, service.GetString());
        WorkspaceStatus.Text = "تم استعادة خيارات الباقة دون تغيير الكروت الحالية.";
    }

    private static void SelectChoice(ComboBox combo, string? tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
            if (item.Tag?.ToString() == tag)
            {
                combo.SelectedItem = item;
                return;
            }
    }

    private bool ConfirmCurrentAccess()
    {
        if (_session.HasCurrentAccess) return true;
        LockWorkspace();
        return false;
    }

    private static string FormatPercent(int? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) + "%" : "—";
    private static string FormatCount(int? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "—";

    protected override void OnClosed(EventArgs e)
    {
        _heartbeat.Stop();
        _cancel.Cancel();
        base.OnClosed(e);
    }
}
