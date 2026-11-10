using System.Xml.Linq;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>
/// Fail before compiling WPF when a XAML TextChanged handler is missing from
/// the matching partial window class. The Windows WPF build remains the final check.
/// </summary>
public sealed class WpfInputHandlerContractTests
{
    [Fact]
    public void Workspace_numeric_input_handlers_exist_in_real_WPF_code_behind()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName,
            "src", "HadramoutNET.App", "WorkspaceWindow.xaml")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        string path = Path.Combine(dir!.FullName, "src", "HadramoutNET.App");
        var xaml = XDocument.Load(Path.Combine(path, "WorkspaceWindow.xaml"));
        string codeBehind = File.ReadAllText(
            Path.Combine(path, "WorkspaceWindow.xaml.cs"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var handlers = xaml.Descendants(ns + "TextBox")
            .Select(e => (string?)e.Attribute("TextChanged"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("TechnicalNumericInput_TextChanged", handlers);
        foreach (string handler in handlers!)
            Assert.Contains("void " + handler + "(", codeBehind);
    }
}
