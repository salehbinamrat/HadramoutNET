using System.Globalization;
using HadramoutNET.Core.Visual;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class EnglishDigitPresentationTests
{
    [Fact]
    public void All_application_generated_counts_use_Latin_digits_independent_of_Arabic_Windows()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal("1,234,567", EnglishDigitPresentation.Count(1234567));
            Assert.Equal("12.5", EnglishDigitPresentation.Decimal(12.5m));
            Assert.Equal("2026-11-09 07:05",
                EnglishDigitPresentation.Timestamp(new DateTime(2026, 11, 9, 7, 5, 0)));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Display_text_normalizes_unicode_digits_without_changing_Arabic_words()
    {
        Assert.Equal("عدد الكروت: 120", EnglishDigitPresentation.Text("عدد الكروت: ١٢٠"));
        Assert.Equal("سعة 128 GB", EnglishDigitPresentation.Text("سعة ۱۲۸ GB"));
        Assert.Equal("192.168.88.1", EnglishDigitPresentation.Text("١٩٢.١٦٨.٨٨.١"));
    }

    [Fact]
    public void Numeric_culture_does_not_replace_Arabic_interface_language()
    {
        Assert.Equal("en-US", EnglishDigitPresentation.Culture.Name);
        Assert.Equal("العربية", EnglishDigitPresentation.Text("العربية"));
    }

    [Fact]
    public void Window_XAML_must_override_WPF_number_substitution_with_European()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null &&
            !File.Exists(Path.Combine(repo.FullName, "src", "HadramoutNET.App", "App.xaml")))
            repo = repo.Parent;
        Assert.NotNull(repo);
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(
            repo!.FullName, "src", "HadramoutNET.App", "App.xaml"));
        var ns = System.Xml.Linq.XNamespace.Get(
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var styles = xaml.Descendants(ns + "Style");
        var windows = Assert.Single(styles,
            e => (string?)e.Attribute("TargetType") == "Window");
        Assert.Contains(windows.Descendants(ns + "Setter"),
            x => (string?)x.Attribute("Property") == "NumberSubstitution.Substitution" &&
                 (string?)x.Attribute("Value") == "European");
        Assert.Contains(windows.Descendants(ns + "Setter"),
            x => (string?)x.Attribute("Property") == "Language" &&
                 (string?)x.Attribute("Value") == "en-US");
    }
}
