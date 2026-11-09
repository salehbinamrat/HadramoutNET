using System.Xml.Linq;
using HadramoutNET.Core.RouterOS;
using HadramoutNET.Core.Visual;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>
/// Regression coverage for Arabic keyboard input and small Windows desktops.
/// XAML structure is checked here; actual multi-DPI rendering still requires Windows acceptance.
/// </summary>
public sealed class DesktopInputAndViewportTests
{
    [Theory]
    [InlineData("١٩٢.١٦٨.٨٨.١", "٨٧٢٨", "local", "192.168.88.1", 8728)]
    [InlineData("۱۹۲.۱۶۸.۸۸.۱", "۸۷۲۸", "local", "192.168.88.1", 8728)]
    [InlineData("١٩٢٫١٦٨٫٨٨٫١", "8728", "local", "192.168.88.1", 8728)]
    [InlineData("https://router.example:٨٢٩١/", "٨٧٢٩", "remote", "router.example", 8729)]
    public void Arabic_keyboard_address_and_port_are_usable(
        string host, string port, string mode, string expectedHost, int expectedPort)
    {
        RouterEndpoint normalized = RouterAddress.Normalize(host, port, mode);
        Assert.Equal(expectedHost, normalized.Host);
        Assert.Equal(expectedPort, normalized.Port);
    }

    [Fact]
    public void Only_digit_shapes_and_decimal_separator_change()
    {
        Assert.Equal("router-0123456789", AsciiNumerals.Normalize("router-٠١٢٣٤٥٦٧٨٩"));
        Assert.Equal("شبكة.0123456789", AsciiNumerals.Normalize("شبكة٫۰۱۲۳۴۵۶۷۸۹"));
        Assert.Equal("p@ss١٢".Replace("١٢", "12"), AsciiNumerals.Normalize("p@ss١٢"));
        Assert.Equal("name/secret", AsciiNumerals.Normalize("name/secret"));
    }

    [Theory]
    [InlineData("secret١٢", true, "secret12")]
    [InlineData("secret١٢", false, "secret١٢")]
    [InlineData("secret۱۲", true, "secret12")]
    public void Password_digit_conversion_is_explicitly_reversible(
        string password, bool convertDigits, string expected)
    {
        Assert.Equal(expected, AsciiNumerals.PasswordForRouter(password, convertDigits));
    }

    [Theory]
    [InlineData(1280, 820, 1024, 720, 1000, 696)]
    [InlineData(1280, 820, 1920, 1080, 1280, 820)]
    [InlineData(1280, 820, 640, 480, 616, 456)]
    public void Preferred_desktop_size_never_exceeds_work_area(
        double preferredWidth, double preferredHeight, double areaWidth,
        double areaHeight, double expectedWidth, double expectedHeight)
    {
        DesktopWindowSize size = DesktopWindowSizing.Fit(
            preferredWidth, preferredHeight, areaWidth, areaHeight);
        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
        Assert.True(size.Width <= areaWidth && size.Height <= areaHeight);
    }

    private static DirectoryInfo RepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src",
                    "HadramoutNET.App", "MainWindow.xaml")))
                return current;
            current = current.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Login_and_workspace_windows_allow_smaller_desktops()
    {
        string dir = Path.Combine(RepoRoot().FullName, "src", "HadramoutNET.App");
        XElement login = XDocument.Load(Path.Combine(dir, "MainWindow.xaml")).Root!;
        XElement workspace = XDocument.Load(Path.Combine(dir, "WorkspaceWindow.xaml")).Root!;
        Assert.Equal("CanResize", (string?)login.Attribute("ResizeMode"));
        Assert.True((double?)login.Attribute("MinWidth") <= 800);
        Assert.True((double?)login.Attribute("MinHeight") <= 560);
        Assert.True((double?)workspace.Attribute("MinWidth") <= 800);
        Assert.True((double?)workspace.Attribute("MinHeight") <= 560);

        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xns = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (string field in new[] { "RouterHostBox", "RouterPortBox" })
        {
            XElement input = Assert.Single(login.Descendants(ns + "TextBox"),
                element => (string?)element.Attribute(xns + "Name") == field);
            Assert.Equal("RouterNumericInput_TextChanged",
                (string?)input.Attribute("TextChanged"));
            Assert.Equal("LeftToRight", (string?)input.Attribute("FlowDirection"));
        }
    }
}
