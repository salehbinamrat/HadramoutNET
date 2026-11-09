namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// Display/input-only normalization for Arabic-Indic and Eastern Arabic digits.
/// Apply to typed endpoint/numeric fields, never silently to RouterOS passwords.
/// </summary>
public static class AsciiNumerals
{
    public static string Normalize(string? value)
    {
        if (value is null) return string.Empty;
        char[]? updated = null;
        for (int i = 0; i < value.Length; i++)
        {
            char original = value[i];
            char converted = original switch
            {
                >= '\u0660' and <= '\u0669' => (char)('0' + original - '\u0660'),
                >= '\u06F0' and <= '\u06F9' => (char)('0' + original - '\u06F0'),
                '\u066B' => '.',
                _ => original
            };
            if (converted == original) continue;
            updated ??= value.ToCharArray();
            updated[i] = converted;
        }
        return updated is null ? value : new string(updated);
    }
}
