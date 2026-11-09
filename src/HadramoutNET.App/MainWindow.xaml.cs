using System.Threading;
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
    private CancellationTokenSource? _probeCancellation;

    public MainWindow()
    {
        InitializeComponent();
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

    protected override void OnClosed(EventArgs e)
    {
        _probeCancellation?.Cancel();
        base.OnClosed(e);
    }
}
