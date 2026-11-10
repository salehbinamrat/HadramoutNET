using System.Xml.Linq;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>
/// Structural boundary: every login UI action routes to a real WPF code-behind handler.
/// Router/login/trial semantics are exercised by their separate integration tests.
/// </summary>
public sealed class LoginPageContractTests
{
    [Fact]
    public void Login_controls_and_handlers_are_connected_and_paid_gate_is_signed()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName,
            "src", "HadramoutNET.App", "MainWindow.xaml")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string appDir = Path.Combine(dir!.FullName, "src", "HadramoutNET.App");
        var xml = XDocument.Load(Path.Combine(appDir, "MainWindow.xaml"));
        var cs = File.ReadAllText(Path.Combine(appDir, "MainWindow.xaml.cs"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string name in new[] {
            "LicenseDetailsButton", "LicenseActivationPanel", "RenewLicenseButton",
            "TrialStartButton", "RouterHostBox", "RouterPortBox", "RouterUsernameBox",
            "RouterPasswordBox", "RouterPasswordVisible", "ShowRouterPasswordCheck",
            "UseSslCheck", "VerifySslCheck", "LocalModeRadio", "RemoteModeRadio",
            "SaveLoginCheck", "SavePasswordCheck", "SavedConnectionsCombo",
            "DeleteSavedConnectionButton", "RouterLoginButton", "RouterProbeButton" })
            Assert.Contains(xml.Descendants(), node => (string?)node.Attribute(x + "Name") == name);

        foreach (var handler in xml.Descendants()
            .SelectMany(node => new[] { "Click", "Checked", "Unchecked", "SelectionChanged", "TextChanged" }
                .Select(attr => (string?)node.Attribute(attr)))
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
            Assert.Contains("void " + handler + "(", cs);

        Assert.Contains("access.Permitted && access.Reason == \"online_valid\"", cs.Replace("verified = ", "verified = "));
        Assert.Contains("if (remember) RememberConnection(request, savePassword);", cs);
        Assert.Contains("if (_paidLicense.NeedsCheck(", cs);
        Assert.Contains("RouterConnectionHistoryStore.ForCurrentUser()", cs);
        Assert.DoesNotContain("full login remains deliberately disabled", cs);
    }
}
