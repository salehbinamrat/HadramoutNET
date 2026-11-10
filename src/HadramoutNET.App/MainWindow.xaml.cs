using System.Threading;
using System.Diagnostics;
using System.Globalization;
using HadramoutNET.Core.Licensing;
using System.Windows;
using System.Windows.Controls;
using HadramoutNET.Core.RouterOS;

namespace HadramoutNET.App;

/// <summary>
/// Protected router login: signed paid license or original opt-in 480-hour trial.
/// A read-only probe never grants access or starts a free trial.
/// </summary>
public partial class MainWindow : Window
{
    private readonly RouterConnectionProbe _probe = new();
    private readonly Stopwatch _licenseClock = Stopwatch.StartNew();
    private readonly SupabaseLicenseClient _supabaseClient = new();
    private readonly CancellationTokenSource _windowCancel = new();
    private TrialStateManager? _trialStore;
    private RouterLoginCoordinator? _routerLogin;
    private WorkspaceWindow? _workspace;
    private bool _trialOptedIn;
    private bool _loggingIn;
    private PaidLicenseCoordinator? _paidLicense;
    private bool _licenseBusy;
    private CancellationTokenSource? _probeCancellation;
    private bool _renewalOpen;
    private RouterConnectionHistoryStore? _history;
    private bool _loadingHistory;
    private bool _changingMode;
    private string _mode = "local";
    private readonly Dictionary<string, (string Host, string Port, bool Ssl, bool Verify)> _drafts = new()
    {
        ["local"] = ("192.168.88.1", "8728", false, true),
        ["remote"] = ("", "", false, true)
    };

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowWorkArea.Fit(this);
        RefreshMode();
        Loaded += LoginWindow_Loaded;
        try
        {
            _paidLicense = new PaidLicenseCoordinator(
                PaidCodeStore.ForCurrentUser(), _supabaseClient);
            _trialStore = TrialStateManager.ForCurrentWindowsUser();
            _routerLogin = new RouterLoginCoordinator(_paidLicense, _trialStore,
                () => _licenseClock.Elapsed);
            RefreshTrialStatus();
            UpdateLicenseControls(_paidLicense.StatusAt(_licenseClock.Elapsed));
        }
        catch (Exception)
        {
            LicenseStatusText.Text = "تعذر فتح مخزن الترخيص المحمي؛ تحقق من ملفات المستخدم.";
            ActivateLicenseButton.IsEnabled = false;
            CheckLicenseButton.IsEnabled = false;
            LicenseDetailsButton.IsEnabled = false;
            RenewLicenseButton.IsEnabled = false;
        }
        try
        {
            _history = RouterConnectionHistoryStore.ForCurrentUser();
            ReloadHistory();
        }
        catch (Exception)
        {
            RouterProbeStatus.Text = "تعذر فتح محفوظات الاتصالات المحمية؛ يمكنك الدخول يدويا.";
        }
    }

    private void RouterNumericInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox field) return;
        string normalized = AsciiNumerals.Normalize(field.Text);
        if (normalized == field.Text) return;
        int caret = field.CaretIndex;
        field.Text = normalized;
        field.CaretIndex = Math.Min(caret, normalized.Length);
    }

    private void RefreshTrialStatus()
    {
        if (_trialStore is null)
        {
            TrialStartButton.IsEnabled = false;
            TrialStatusText.Text = "تعذر الوصول إلى سجل التجربة المحمي.";
            return;
        }
        TrialAccess trial = _trialStore.Preview(DateTimeOffset.UtcNow);
        TrialStartButton.IsEnabled = trial.Permitted;
        TrialStatusText.Text = trial.Message;
        TrialStartButton.Content = _trialOptedIn ? "✓ التجربة محددة — اضغط تسجيل الدخول" :
            trial.Mode == "trial" ? "متابعة التجربة المجانية" : "اختيار التجربة المجانية — 20 يوما";
    }

    private void TrialStart_Click(object sender, RoutedEventArgs e)
    {
        if (_trialStore is null) return;
        TrialAccess status = _trialStore.Preview(DateTimeOffset.UtcNow);
        if (!status.Permitted)
        {
            TrialStatusText.Text = status.Message;
            return;
        }
        _trialOptedIn = true;
        RefreshTrialStatus();
        TrialStatusText.Text = "تم اختيار التجربة. يبدأ حساب المدة فقط بعد تسجيل دخول ناجح إلى MikroTik.";
    }

    private async void RouterLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_loggingIn || _routerLogin is null || _paidLicense is null ||
            _trialStore is null || _workspace is not null) return;
        _loggingIn = true;
        RouterLoginButton.IsEnabled = false;
        string enteredPassword = ReadRouterPassword();
        bool remember = SaveLoginCheck.IsChecked == true;
        bool savePassword = remember && SavePasswordCheck.IsChecked == true;
        var request = new RouterLoginRequest(
            RouterHostBox.Text, RouterPortBox.Text, RouterUsernameBox.Text,
            AsciiNumerals.PasswordForRouter(enteredPassword,
                NormalizePasswordDigitsCheck.IsChecked == true),
            TrialOptIn: _trialOptedIn,
            UseSsl: UseSslCheck.IsChecked == true,
            VerifySsl: VerifySslCheck.IsChecked == true,
            ConnectionMode: RemoteModeRadio.IsChecked == true ? "remote" : "local");
        ClearRouterPassword();
        RouterProbeStatus.Text = "جارٍ التحقق من الترخيص ومصادقة MikroTik...";
        try
        {
            RouterLoginResult result = await _routerLogin.LoginAsync(
                request, DateTimeOffset.UtcNow, _windowCancel.Token);
            if (!IsLoaded) return;
            if (!result.Success || result.Session is null)
            {
                RouterProbeStatus.Text = result.Message;
                RefreshTrialStatus();
                return;
            }
            RefreshTrialStatus();
            if (remember) RememberConnection(request, savePassword);
            _workspace = new WorkspaceWindow(result.Session, _paidLicense,
                _trialStore, _licenseClock);
            _workspace.Closed += Workspace_Closed;
            Hide();
            _workspace.Show();
        }
        catch (Exception)
        {
            if (IsLoaded)
                RouterProbeStatus.Text = "تعذر إتمام تسجيل الدخول بصورة آمنة؛ لم تُفتح لوحة الإدارة.";
        }
        finally
        {
            _loggingIn = false;
            if (IsLoaded) RouterLoginButton.IsEnabled = true;
        }
    }

    private async void Workspace_Closed(object? sender, EventArgs e)
    {
        _workspace = null;
        try
        {
            if (_routerLogin is not null) await _routerLogin.LogoutAsync();
        }
        catch (Exception) { /* The router transport is closed best-effort. */ }
        _trialOptedIn = false;
        TrialStartButton.Content = "اختيار التجربة المجانية — 20 يوما";
        RefreshTrialStatus();
        if (_paidLicense is not null) UpdateLicenseControls(_paidLicense.StatusAt(_licenseClock.Elapsed));
        if (IsLoaded && !_windowCancel.IsCancellationRequested)
        {
            RouterProbeStatus.Text = "تم تسجيل الخروج من الراوتر، ولم تُحذف بيانات الترخيص.";
            Show();
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_probeCancellation is not null) return;
        var request = new RouterProbeRequest(
            RouterHostBox.Text,
            RouterPortBox.Text,
            RouterUsernameBox.Text,
            AsciiNumerals.PasswordForRouter(ReadRouterPassword(),
                NormalizePasswordDigitsCheck.IsChecked == true),
            UseSsl: UseSslCheck.IsChecked == true,
            VerifySsl: VerifySslCheck.IsChecked == true,
            ConnectionMode: RemoteModeRadio.IsChecked == true ? "remote" : "local");

        // The password is only used for this explicit connection attempt.
        // Do not save or log it; the original trial mirrors stay untouched.
        ClearRouterPassword();
        var cancellation = new CancellationTokenSource();
        _probeCancellation = cancellation;
        RouterProbeButton.IsEnabled = false;
        RouterProbeStatus.Text = "جارٍ فحص تسجيل الدخول وقراءة هوية MikroTik...";

        try
        {
            RouterProbeResult result = await _probe.TestAsync(request, cancellation.Token);
            if (!IsLoaded) return;
            RouterProbeStatus.Text = result.Success
                ? "نجح الاختبار: " + result.Identity +
                  " — أُغلق الاتصال دون فتح جلسة إدارة أو بدء التجربة."
                : result.Message;
        }
        catch (Exception)
        {
            if (IsLoaded)
                RouterProbeStatus.Text = "تعذر إتمام الاختبار. لم تُحفظ أي بيانات دخول.";
        }
        finally
        {
            if (ReferenceEquals(_probeCancellation, cancellation))
                _probeCancellation = null;
            cancellation.Dispose();
            if (IsLoaded) RouterProbeButton.IsEnabled = true;
        }
    }

    private async void CheckLicense_Click(object sender, RoutedEventArgs e) =>
        await VerifyLicenseAsync(activate: false);

    private async void ActivateLicense_Click(object sender, RoutedEventArgs e) =>
        await VerifyLicenseAsync(activate: true);

    private async Task VerifyLicenseAsync(bool activate)
    {
        if (_licenseBusy || _paidLicense is null) return;
        _licenseBusy = true;
        ActivateLicenseButton.IsEnabled = false;
        CheckLicenseButton.IsEnabled = false;
        // An entered code is processed ONLY in the explicit activation path.
        string enteredCode = activate ? LicenseCodeBox.Password : string.Empty;
        LicenseCodeBox.Clear();
        LicenseStatusText.Text = activate ? "جارٍ التحقق من الرمز وإجراء التفعيل عند الحاجة..." :
            "جارٍ فحص الترخيص المحفوظ من الخدمة الموقعة...";
        try
        {
            LicenseAccess result = activate
                ? await _paidLicense.ActivateEnteredCodeAsync(
                    enteredCode, DateTimeOffset.UtcNow, _licenseClock.Elapsed, _windowCancel.Token)
                : await _paidLicense.CheckStoredAsync(
                    DateTimeOffset.UtcNow, _licenseClock.Elapsed, _windowCancel.Token);
            if (!IsLoaded) return;
            LicenseStatusText.Text = result.Message;
            if (result.Permitted) _trialOptedIn = false;
            _renewalOpen = false;
            UpdateLicenseControls(result);
            RefreshTrialStatus();
        }
        catch (Exception)
        {
            if (IsLoaded)
                LicenseStatusText.Text = "تعذر التحقق من الترخيص؛ لم تُمنح صلاحية دخول.";
        }
        finally
        {
            _licenseBusy = false;
            if (IsLoaded)
            {
                ActivateLicenseButton.IsEnabled = true;
                CheckLicenseButton.IsEnabled = true;
                if (_paidLicense is not null) UpdateLicenseControls(_paidLicense.StatusAt(_licenseClock.Elapsed));
            }
        }
    }


    private async void LoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_paidLicense is null) return;
        try
        {
            if (_paidLicense.NeedsCheck(_licenseClock.Elapsed))
                await VerifyLicenseAsync(activate: false);
        }
        catch (Exception)
        {
            if (IsLoaded) LicenseStatusText.Text = "تعذر فحص الترخيص المحفوظ؛ أعد المحاولة يدويا.";
        }
    }

    private void UpdateLicenseControls(LicenseAccess access)
    {
        bool verified = access.Permitted && access.Reason == "online_valid";
        LicenseActivationPanel.Visibility = !verified || _renewalOpen ? Visibility.Visible : Visibility.Collapsed;
        RenewLicenseButton.Visibility = verified && !_renewalOpen ? Visibility.Visible : Visibility.Collapsed;
        TrialStartButton.Visibility = verified ? Visibility.Collapsed : Visibility.Visible;
        TrialStatusText.Visibility = verified ? Visibility.Collapsed : Visibility.Visible;
        TrialExplanation.Visibility = verified ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenewLicense_Click(object sender, RoutedEventArgs e)
    {
        if (_licenseBusy || _paidLicense is null) return;
        _renewalOpen = true;
        LicenseCodeBox.Clear();
        UpdateLicenseControls(_paidLicense.StatusAt(_licenseClock.Elapsed));
        LicenseStatusText.Text = "أدخل رمز التجديد؛ لن يُقبل إلا بعد التحقق الموقّع.";
        LicenseCodeBox.Focus();
    }

    private void LicenseDetails_Click(object sender, RoutedEventArgs e)
    {
        LicenseAccess? paid = _paidLicense?.StatusAt(_licenseClock.Elapsed);
        TrialAccess? trial = _trialStore?.Preview(DateTimeOffset.UtcNow);
        string device;
        try { device = WindowsLicenseIdentity.ReadCurrent().PaidDeviceCode; }
        catch (Exception) { device = "غير متاح"; }
        string message = "الترخيص: " + (paid?.Message ?? "غير متاح")
            + "\nرقم الجهاز: " + device
            + "\nرمز الترخيص: " + (string.IsNullOrWhiteSpace(paid?.LicenseId) ? "غير مفعّل" : paid.LicenseId)
            + "\nتاريخ الانتهاء: " + (string.IsNullOrWhiteSpace(paid?.Expires) ? "غير محدد" : paid.Expires)
            + "\nالتجربة: " + (trial?.Message ?? "غير متاحة")
            + "\nمدة التجربة الأصلية: 20 يوما (480 ساعة).";
        MessageBox.Show(this, message, "تفاصيل الترخيص", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private string ReadRouterPassword() =>
        ShowRouterPasswordCheck.IsChecked == true ? RouterPasswordVisible.Text : RouterPasswordBox.Password;

    private void ClearRouterPassword()
    {
        RouterPasswordBox.Clear();
        RouterPasswordVisible.Clear();
    }

    private void ShowRouterPassword_Checked(object sender, RoutedEventArgs e)
    {
        if (RouterPasswordBox is null || RouterPasswordVisible is null) return;
        RouterPasswordVisible.Text = RouterPasswordBox.Password;
        RouterPasswordBox.Clear();
        RouterPasswordBox.Visibility = Visibility.Collapsed;
        RouterPasswordVisible.Visibility = Visibility.Visible;
        RouterPasswordVisible.Focus();
        RouterPasswordVisible.CaretIndex = RouterPasswordVisible.Text.Length;
    }

    private void ShowRouterPassword_Unchecked(object sender, RoutedEventArgs e)
    {
        if (RouterPasswordBox is null || RouterPasswordVisible is null) return;
        RouterPasswordBox.Password = RouterPasswordVisible.Text;
        RouterPasswordVisible.Clear();
        RouterPasswordVisible.Visibility = Visibility.Collapsed;
        RouterPasswordBox.Visibility = Visibility.Visible;
        RouterPasswordBox.Focus();
    }

    private void SaveLogin_Changed(object sender, RoutedEventArgs e)
    {
        if (SaveLoginCheck is null || SavePasswordCheck is null) return;
        SavePasswordCheck.IsEnabled = SaveLoginCheck.IsChecked == true;
        if (SaveLoginCheck.IsChecked != true) SavePasswordCheck.IsChecked = false;
    }

    private void ReloadHistory()
    {
        if (_history is null || SavedConnectionsCombo is null) return;
        _loadingHistory = true;
        try
        {
            SavedConnectionsCombo.ItemsSource = _history.Read();
            SavedConnectionsCombo.SelectedIndex = -1;
        }
        finally { _loadingHistory = false; }
    }

    private void SavedConnection_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingHistory ||
            SavedConnectionsCombo.SelectedItem is not RouterConnectionProfile item) return;
        if (item.RemoteMode) RemoteModeRadio.IsChecked = true;
        else LocalModeRadio.IsChecked = true;
        RouterHostBox.Text = item.Host;
        RouterPortBox.Text = item.Port.ToString(CultureInfo.InvariantCulture);
        RouterUsernameBox.Text = item.Username;
        UseSslCheck.IsChecked = item.UseSsl;
        VerifySslCheck.IsChecked = item.VerifySsl;
        ShowRouterPasswordCheck.IsChecked = false;
        ClearRouterPassword();
        RouterPasswordBox.Password = item.Password;
        SaveLoginCheck.IsChecked = true;
        SavePasswordCheck.IsChecked = item.Password.Length > 0;
        RouterProbeStatus.Text = "تم استرجاع اتصال محفوظ؛ راجع البيانات ثم سجل الدخول.";
    }

    private void DeleteSavedConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_history is null ||
            SavedConnectionsCombo.SelectedItem is not RouterConnectionProfile item) return;
        if (MessageBox.Show(this, "هل تريد حذف الاتصال من المحفوظات المحلية؟",
            "حذف اتصال", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            _history.Delete(item.Key);
            ReloadHistory();
            ClearRouterPassword();
            SaveLoginCheck.IsChecked = false;
            RouterProbeStatus.Text = "حُذف الاتصال المحفوظ من هذا الكمبيوتر فقط.";
        }
        catch (Exception)
        {
            RouterProbeStatus.Text = "تعذر حذف الاتصال من المحفوظات المحمية.";
        }
    }

    private void RememberConnection(RouterLoginRequest request, bool rememberPassword)
    {
        if (_history is null) return;
        try
        {
            RouterEndpoint endpoint = RouterAddress.Normalize(request.Host, request.ApiPort, request.ConnectionMode);
            _history.Upsert(new RouterConnectionProfile(endpoint.Host, endpoint.Port,
                request.Username.Trim(), request.UseSsl, request.VerifySsl,
                request.ConnectionMode == "remote", request.Password), rememberPassword);
            ReloadHistory();
        }
        catch (Exception)
        {
            RouterProbeStatus.Text = "تم الدخول، لكن تعذر حفظ الاتصال في Windows.";
        }
    }

    private void ConnectionMode_Checked(object sender, RoutedEventArgs e)
    {
        if (RouterHostBox is null || RouterPortBox is null || HostCaption is null ||
            PortCaption is null || RemoteModeRadio is null) return;
        RefreshMode();
    }

    private void RefreshMode()
    {
        if (RouterHostBox is null || RouterPortBox is null || RemoteModeRadio is null ||
            HostCaption is null || PortCaption is null || _changingMode) return;
        string target = RemoteModeRadio.IsChecked == true ? "remote" : "local";
        if (target != _mode)
        {
            _drafts[_mode] = (RouterHostBox.Text, RouterPortBox.Text,
                UseSslCheck.IsChecked == true, VerifySslCheck.IsChecked == true);
            var draft = _drafts[target];
            _changingMode = true;
            try
            {
                RouterHostBox.Text = draft.Host;
                RouterPortBox.Text = draft.Port;
                UseSslCheck.IsChecked = draft.Ssl;
                VerifySslCheck.IsChecked = draft.Verify;
            }
            finally { _changingMode = false; }
            _mode = target;
        }
        HostCaption.Text = target == "remote" ? "رابط الاتصال :" : "عنوان الراوتر :";
        PortCaption.Text = target == "remote" ? "API خارجي :" : "API :";
        ConnectionHint.Visibility = target == "remote" ? Visibility.Visible : Visibility.Collapsed;
        VerifySslCheck.IsEnabled = UseSslCheck.IsChecked == true;
    }

    private void UseSsl_Changed(object sender, RoutedEventArgs e)
    {
        if (_changingMode || RouterPortBox is null || UseSslCheck is null ||
            VerifySslCheck is null || RemoteModeRadio is null) return;
        if (RemoteModeRadio.IsChecked != true &&
            (RouterPortBox.Text.Trim() is "8728" or "8729"))
            RouterPortBox.Text = UseSslCheck.IsChecked == true ? "8729" : "8728";
        VerifySslCheck.IsEnabled = UseSslCheck.IsChecked == true;
    }

    private void FocusLogin_Click(object sender, RoutedEventArgs e) => RouterHostBox.Focus();

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, "حضرموت لإدارة الشبكات وطباعة الكروت — .NET 10\n"
            + "دخول MikroTik، الهوتسبوت، اليوزرمنجر، البرودباند وإنشاء وطباعة الكروت.\n"
            + "التجربة المجانية الأصلية 20 يوما.",
            "حول البرنامج", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Support_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
            "برمجة وتطوير: صالح بن عمرات\nرقم التواصل: 774660770\nتيليجرام: t.me/Salehbinzain\n"
            + "هل تريد فتح تيليجرام؟",
            "خدمة العملاء", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        try
        {
            Process.Start(new ProcessStartInfo("https://t.me/Salehbinzain")
                { UseShellExecute = true });
        }
        catch (Exception)
        {
            MessageBox.Show(this, "تعذر فتح الرابط؛ افتح t.me/Salehbinzain يدويا.");
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        _probeCancellation?.Cancel();
        _windowCancel.Cancel();
        _workspace?.Close();
        _supabaseClient.Dispose();
        base.OnClosed(e);
        try
        {
            if (_routerLogin is not null) await _routerLogin.DisposeAsync();
        }
        catch (Exception) { /* Closing must not reveal secrets or keep a socket open. */ }
        _windowCancel.Dispose();
    }
}
