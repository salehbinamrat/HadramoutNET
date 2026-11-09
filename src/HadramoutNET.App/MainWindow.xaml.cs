using System.Threading;
using System.Diagnostics;
using HadramoutNET.Core.Licensing;
using System.Windows;
using System.Windows.Controls;
using HadramoutNET.Core.RouterOS;

namespace HadramoutNET.App;

/// <summary>
/// Login UI foundation. Only the read-only MikroTik connection test is wired.
/// Successful /login + identity is NOT a trial start, paid grant, or access to
/// application management screens; full login remains deliberately disabled.
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

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowWorkArea.Fit(this);
        try
        {
            _paidLicense = new PaidLicenseCoordinator(
                PaidCodeStore.ForCurrentUser(), _supabaseClient);
            _trialStore = TrialStateManager.ForCurrentWindowsUser();
            _routerLogin = new RouterLoginCoordinator(_paidLicense, _trialStore,
                () => _licenseClock.Elapsed);
            RefreshTrialStatus();
        }
        catch (Exception)
        {
            LicenseStatusText.Text = "تعذر فتح مخزن الترخيص المحمي؛ تحقق من ملفات المستخدم.";
            ActivateLicenseButton.IsEnabled = false;
            CheckLicenseButton.IsEnabled = false;
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
        TrialStatusText.Text = "تم اختيار التجربة. يبدأ حساب المدة فقط بعد تسجيل دخول ناجح إلى MikroTik.";
    }

    private async void RouterLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_loggingIn || _routerLogin is null || _paidLicense is null ||
            _trialStore is null || _workspace is not null) return;
        _loggingIn = true;
        RouterLoginButton.IsEnabled = false;
        var request = new RouterLoginRequest(
            RouterHostBox.Text, RouterPortBox.Text, RouterUsernameBox.Text,
            RouterPasswordBox.Password,
            TrialOptIn: _trialOptedIn,
            UseSsl: UseSslCheck.IsChecked == true,
            VerifySsl: VerifySslCheck.IsChecked == true,
            ConnectionMode: RemoteModeRadio.IsChecked == true ? "remote" : "local");
        RouterPasswordBox.Clear();
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
            RouterPasswordBox.Password,
            UseSsl: UseSslCheck.IsChecked == true,
            VerifySsl: VerifySslCheck.IsChecked == true,
            ConnectionMode: RemoteModeRadio.IsChecked == true ? "remote" : "local");

        // The password is only used for this explicit connection attempt.
        // Do not save or log it; the original trial mirrors stay untouched.
        RouterPasswordBox.Clear();
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
            LicenseStatusText.Text = result.Permitted
                ? "الترخيص المدفوع صالح بالتوقيع الرقمي؛ الدخول الكامل لم يُفعّل بعد."
                : result.Message;
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
            }
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
