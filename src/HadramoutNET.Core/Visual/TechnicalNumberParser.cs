using System.Globalization;
using HadramoutNET.Core.RouterOS;

namespace HadramoutNET.Core.Visual;

/// <summary>
/// Locale-independent technical number parsing for typed/pasted Arabic and
/// Persian digits. No group separators or alternate sign rules are inferred.
/// Credentials and customer-defined package names are not changed.
/// </summary>
public static class TechnicalNumberParser
{
    public static bool TryUnsignedInt(string? input, out int value) =>
        int.TryParse(AsciiNumerals.Normalize(input?.Trim()), NumberStyles.None,
            CultureInfo.InvariantCulture, out value);

    public static bool TryDecimal(string? input, out decimal value) =>
        decimal.TryParse(AsciiNumerals.Normalize(input?.Trim()),
            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    public static bool TryFiniteDouble(string? input, out double value) =>
        double.TryParse(AsciiNumerals.Normalize(input?.Trim()), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
