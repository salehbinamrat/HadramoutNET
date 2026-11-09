using System.Windows.Controls;
using HadramoutNET.Core.RouterOS;

namespace HadramoutNET.App;

/// <summary>
/// Keep technical numeric field display in Latin ASCII while preserving the
/// caret after pasted/typed Arabic-Indic or Eastern Arabic digits.
/// Do not attach to passwords or arbitrary source-router identifiers.
/// </summary>
internal static class TechnicalTextInput
{
    internal static void Attach(TextBox input)
    {
        input.FlowDirection = System.Windows.FlowDirection.LeftToRight;
        input.Language = System.Windows.Markup.XmlLanguage.GetLanguage("en-US");
        input.TextChanged += NormalizeTextBox;
    }

    internal static void NormalizeTextBox(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox field) return;
        string normalized = AsciiNumerals.Normalize(field.Text);
        if (string.Equals(normalized, field.Text, StringComparison.Ordinal)) return;

        int caret = field.CaretIndex;
        field.Text = normalized;
        field.CaretIndex = Math.Min(caret, normalized.Length);
    }
}
