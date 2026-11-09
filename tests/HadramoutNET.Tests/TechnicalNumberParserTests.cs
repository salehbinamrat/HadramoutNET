using System.Globalization;
using System.Xml.Linq;
using HadramoutNET.Core.Visual;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class TechnicalNumberParserTests
{
    [Theory]
    [InlineData("٢٥", 25)]
    [InlineData("۲۵", 25)]
    [InlineData("100", 100)]
    [InlineData(" ٩ ", 9)]
    public void Card_quantity_and_length_accept_Arabic_keyboard_digits(string input, int expected)
    {
        Assert.True(TechnicalNumberParser.TryUnsignedInt(input, out int actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("١٠٫٥", "10.5")]
    [InlineData("۱۰.۵", "10.5")]
    [InlineData(" ١٢.٢٥ ", "12.25")]
    public void Card_price_and_capacity_parse_without_localized_separator_ambiguity(
        string input, string expected)
    {
        Assert.True(TechnicalNumberParser.TryDecimal(input, out decimal result));
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), result);
    }

    [Theory]
    [InlineData("١٩٫٥", 19.5)]
    [InlineData("۹٫۲۵", 9.25)]
    [InlineData("1e2", 100)]
    public void Physical_card_dimensions_accept_finite_numbers(string input, double expected)
    {
        Assert.True(TechnicalNumberParser.TryFiniteDouble(input, out double result));
        Assert.Equal(expected, result, 6);
    }

    [Theory]
    [InlineData("1e3")]
    [InlineData("-1")]
    [InlineData("١٬٠٠٠")]
    [InlineData("1,000")]
    [InlineData("12a")]
    [InlineData("")]
    public void Quantities_reject_unsafe_or_ambiguous_syntax(string input)
    {
        Assert.False(TechnicalNumberParser.TryUnsignedInt(input, out _));
    }

    [Theory]
    [InlineData("١,٥")]
    [InlineData("Infinity")]
    [InlineData("NaN")]
    [InlineData("1e309")]
    public void Numeric_inputs_reject_ambiguous_or_nonfinite_values(string input)
    {
        Assert.False(TechnicalNumberParser.TryFiniteDouble(input, out _));
        Assert.False(TechnicalNumberParser.TryDecimal(input, out _));
    }

    [Fact]
    public void All_technical_card_form_fields_normalize_pasted_digits_live()
    {
        var root = RepoRoot();
        var doc = XDocument.Load(Path.Combine(root.FullName,
            "src", "HadramoutNET.App", "WorkspaceWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xns = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string name in new[]
        {
            "QtyBox", "CardLengthBox", "PasswordLengthBox", "StartSequenceBox",
            "CardPriceBox", "CardCapacityBox", "CardHoursBox", "CardMinutesBox",
            "CardValidityBox"
        })
        {
            var field = Assert.Single(doc.Descendants(ns + "TextBox"),
                e => (string?)e.Attribute(xns + "Name") == name);
            Assert.Equal("TechnicalNumericInput_TextChanged",
                (string?)field.Attribute("TextChanged"));
            Assert.Equal("LeftToRight", (string?)field.Attribute("FlowDirection"));
        }
    }

    private static DirectoryInfo RepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src",
                    "HadramoutNET.App", "WorkspaceWindow.xaml")))
                return current;
            current = current.Parent;
        }
        throw new InvalidOperationException("Source repository root missing.");
    }
}
