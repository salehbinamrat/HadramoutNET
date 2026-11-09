using System.Threading;
using System.Diagnostics;
using HadramoutNET.Core.Licensing;
using System.Windows;
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
    private PaidLicenseCoordinator? _paidLicense;
    private bool _licenseBusy;
    private CancellationTokenSource? _probeCancellation;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            _paidLicense = new PaidLicenseCoordinator(
                PaidCodeStore.ForCurrentUser(), _supabaseClient);
        }
        catch (Exception)
        {
            LicenseStatusText.Text = "تعذر فتح مخزن الترخيص المحمي؛ تحقق من ملفات المستخدم.";
            ActivateLicenseButton.IsEnabled = false;
            CheckLicenseButton.IsEnabled = false;
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

    protected override void OnClosed(EventArgs e)
    {
        _probeCancellation?.Cancel();
        _windowCancel.Cancel();
        _supabaseClient.Dispose();
        base.OnClosed(e);
    }
}
