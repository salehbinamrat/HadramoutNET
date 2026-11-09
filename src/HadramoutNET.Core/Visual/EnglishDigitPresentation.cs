using System.Globalization;
using HadramoutNET.Core.RouterOS;

namespace HadramoutNET.Core.Visual;

/// <summary>
/// One display-formatting policy for every WPF window and export-facing
/// application-generated metric. Keeps the Arabic labels unchanged.
/// Never use Text() on a stored credential or a RouterOS query identifier.
/// </summary>
public static class EnglishDigitPresentation
{
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("en-US");

    public static string Count(long value) => value.ToString("N0", Culture);

    public static string Decimal(decimal value) => value.ToString("0.##", Culture);

    public static string Timestamp(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm", Culture);

    public static string Text(string? value) => AsciiNumerals.Normalize(value);
}
