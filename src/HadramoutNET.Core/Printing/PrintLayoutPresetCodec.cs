using System.Globalization;
using System.Text.Json;

namespace HadramoutNET.Core.Printing;

/// <summary>
/// Bidirectional mapping between the original Python card/print.* package
/// keys and the single print geometry used by WPF preview, native print and
/// PDF export. No dependency on the original Python database or printer.
/// </summary>
public static class PrintLayoutPresetCodec
{
    public static Dictionary<string, JsonElement> ToSettings(PrintLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = PrintLayouts.Calculate(options);
        var settings = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["card.width_mm"] = JsonSerializer.SerializeToElement(options.CardWidthMm),
            ["card.height_mm"] = JsonSerializer.SerializeToElement(options.CardHeightMm),
            ["print.rows"] = JsonSerializer.SerializeToElement(options.Rows),
            ["print.columns"] = JsonSerializer.SerializeToElement(options.Columns),
            ["print.paper"] = JsonSerializer.SerializeToElement(options.Paper.ToUpperInvariant()),
            ["print.orientation"] = JsonSerializer.SerializeToElement(options.Orientation),
            ["print.gap_h_mm"] = JsonSerializer.SerializeToElement(options.HorizontalGapMm),
            ["print.gap_v_mm"] = JsonSerializer.SerializeToElement(options.VerticalGapMm),
            ["print.page_margin_mm"] = JsonSerializer.SerializeToElement(options.MarginMm),
            ["print.order"] = JsonSerializer.SerializeToElement(options.Order),
            ["print.duplex"] = JsonSerializer.SerializeToElement(options.Duplex)
        };
        return settings;
    }

    public static PrintLayoutOptions FromSettings(
        IReadOnlyDictionary<string, JsonElement> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var defaults = new PrintLayoutOptions();
        var options = defaults with
        {
            CardWidthMm = Number(settings, "card.width_mm", defaults.CardWidthMm),
            CardHeightMm = Number(settings, "card.height_mm", defaults.CardHeightMm),
            Rows = Integer(settings, "print.rows", defaults.Rows),
            Columns = Integer(settings, "print.columns", defaults.Columns),
            Paper = Text(settings, "print.paper", defaults.Paper),
            Orientation = Text(settings, "print.orientation", defaults.Orientation),
            HorizontalGapMm = Number(settings, "print.gap_h_mm", defaults.HorizontalGapMm),
            VerticalGapMm = Number(settings, "print.gap_v_mm", defaults.VerticalGapMm),
            MarginMm = Number(settings, "print.page_margin_mm", defaults.MarginMm),
            Order = Text(settings, "print.order", defaults.Order),
            Duplex = Text(settings, "print.duplex", defaults.Duplex)
        };
        _ = PrintLayouts.Calculate(options);
        return options;
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement> settings,
        string key, string fallback)
    {
        if (!settings.TryGetValue(key, out JsonElement value)) return fallback;
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new PrintLayoutException("قيمة نصية غير صالحة في إعداد الطباعة: " + key);
        return value.GetString()!;
    }

    private static double Number(IReadOnlyDictionary<string, JsonElement> settings,
        string key, double fallback)
    {
        if (!settings.TryGetValue(key, out JsonElement value)) return fallback;
        double parsed;
        bool ok = value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out parsed),
            JsonValueKind.String => double.TryParse(value.GetString(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out parsed),
            _ => false
        };
        if (!ok)
            throw new PrintLayoutException("قيمة رقمية غير صالحة في إعداد الطباعة: " + key);
        // Validity/ranges are enforced in PrintLayouts.Calculate.
        return parsed;
    }

    private static int Integer(IReadOnlyDictionary<string, JsonElement> settings,
        string key, int fallback)
    {
        if (!settings.ContainsKey(key)) return fallback;
        double parsed = Number(settings, key, fallback);
        if (!double.IsFinite(parsed) || parsed < 1 || parsed > 1000 ||
            Math.Truncate(parsed) != parsed)
            throw new PrintLayoutException("عدد صفوف أو أعمدة غير صالح: " + key);
        return checked((int)parsed);
    }
}
