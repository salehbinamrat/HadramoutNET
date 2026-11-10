using System.Globalization;
using System.Text.Json;

namespace HadramoutNET.Core.Printing;

/// <summary>
/// Typed roundtrip for Python core/db.py card art settings. Unknown card keys
/// stay untouched in PackageSettingsStore; new code never opens Python DB.
/// </summary>
public static class CardArtPresetCodec
{
    private static readonly string[] Toggles =
    [
        "title", "sequence", "price", "capacity", "duration", "validity",
        "date", "phone", "qr", "other", "other2"
    ];

    public static Dictionary<string, JsonElement> ToSettings(CardArtOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Validate(value);
        var settings = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["card.add_title"] = JsonSerializer.SerializeToElement(value.ShowTitle),
            ["card.add_sequence"] = JsonSerializer.SerializeToElement(value.ShowSequence),
            ["card.add_price"] = JsonSerializer.SerializeToElement(value.ShowPrice),
            ["card.add_capacity"] = JsonSerializer.SerializeToElement(value.ShowCapacity),
            ["card.add_duration"] = JsonSerializer.SerializeToElement(value.ShowDuration),
            ["card.add_validity"] = JsonSerializer.SerializeToElement(value.ShowValidity),
            ["card.add_date"] = JsonSerializer.SerializeToElement(value.ShowDate),
            ["card.add_phone"] = JsonSerializer.SerializeToElement(value.ShowPhone),
            ["card.add_qr"] = JsonSerializer.SerializeToElement(value.ShowQr),
            ["card.add_other"] = JsonSerializer.SerializeToElement(value.ShowOther),
            ["card.add_other2"] = JsonSerializer.SerializeToElement(value.ShowOther2),
            ["card.title_text"] = JsonSerializer.SerializeToElement(value.Title),
            ["card.phone_text"] = JsonSerializer.SerializeToElement(value.Phone),
            ["card.other_text"] = JsonSerializer.SerializeToElement(value.Other),
            ["card.other2_text"] = JsonSerializer.SerializeToElement(value.Other2),
            ["card.price_text"] = JsonSerializer.SerializeToElement(value.Price),
            ["card.capacity_text"] = JsonSerializer.SerializeToElement(value.Capacity),
            ["card.duration_text"] = JsonSerializer.SerializeToElement(value.Duration),
            ["card.validity_text"] = JsonSerializer.SerializeToElement(value.Validity),
            ["card.date_text"] = JsonSerializer.SerializeToElement(value.Date),
            ["card.qr_mode"] = JsonSerializer.SerializeToElement(value.QrMode),
            ["card.qr_login_url"] = JsonSerializer.SerializeToElement(value.QrLoginUrl),
            ["card.qr_size_mm"] = JsonSerializer.SerializeToElement(value.QrSizeMm),
            ["card.background_mode"] = JsonSerializer.SerializeToElement(value.BackgroundMode),
            ["card.background_data"] = JsonSerializer.SerializeToElement(value.BackgroundData ?? ""),
            ["card.font_size"] = JsonSerializer.SerializeToElement(value.FontSize),
            ["card.font_family"] = JsonSerializer.SerializeToElement(value.FontFamily),
            ["card.font_bold"] = JsonSerializer.SerializeToElement(value.FontBold),
            ["card.font_color"] = JsonSerializer.SerializeToElement(value.FontColor),
            ["card.color.username"] = JsonSerializer.SerializeToElement(value.UsernameColor),
            ["card.border_color"] = JsonSerializer.SerializeToElement(value.BorderColor),
            ["card.border_width"] = JsonSerializer.SerializeToElement(value.BorderWidth),
            ["card.border_radius"] = JsonSerializer.SerializeToElement(value.BorderRadius),
            ["card.positions"] = JsonSerializer.SerializeToElement(
                (value.Positions ?? CardArtwork.DefaultPositions)
                    .ToDictionary(kv => kv.Key, kv => new[] { kv.Value.X, kv.Value.Y },
                        StringComparer.Ordinal))
        };
        if (value.LayerScales is not null)
            foreach (var (key, multiplier) in value.LayerScales)
                settings["card.size." + key] =
                    JsonSerializer.SerializeToElement(multiplier);
        if (value.LayerColors is not null)
            foreach (var (key, color) in value.LayerColors)
                settings["card.color." + key] =
                    JsonSerializer.SerializeToElement(color);
        return settings;
    }

    public static CardArtOptions FromSettings(
        IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var defaultOptions = new CardArtOptions();
        var options = defaultOptions with
        {
            ShowTitle = ReadBool(values, "card.add_title", false),
            ShowSequence = ReadBool(values, "card.add_sequence", false),
            ShowPrice = ReadBool(values, "card.add_price", false),
            ShowCapacity = ReadBool(values, "card.add_capacity", false),
            ShowDuration = ReadBool(values, "card.add_duration", false),
            ShowValidity = ReadBool(values, "card.add_validity", false),
            ShowDate = ReadBool(values, "card.add_date", false),
            ShowPhone = ReadBool(values, "card.add_phone", false),
            ShowQr = ReadBool(values, "card.add_qr", false),
            ShowOther = ReadBool(values, "card.add_other", false),
            ShowOther2 = ReadBool(values, "card.add_other2", false),
            Title = ReadText(values, "card.title_text", defaultOptions.Title),
            Phone = ReadText(values, "card.phone_text", defaultOptions.Phone),
            Other = ReadText(values, "card.other_text", defaultOptions.Other),
            Other2 = ReadText(values, "card.other2_text", defaultOptions.Other2),
            Price = ReadText(values, "card.price_text", defaultOptions.Price),
            Capacity = ReadText(values, "card.capacity_text", defaultOptions.Capacity),
            Duration = ReadText(values, "card.duration_text", defaultOptions.Duration),
            Validity = ReadText(values, "card.validity_text", defaultOptions.Validity),
            Date = ReadText(values, "card.date_text", defaultOptions.Date),
            QrMode = ReadText(values, "card.qr_mode", defaultOptions.QrMode),
            QrLoginUrl = ReadText(values, "card.qr_login_url", defaultOptions.QrLoginUrl),
            QrSizeMm = ReadNumber(values, "card.qr_size_mm", defaultOptions.QrSizeMm),
            BackgroundMode = ReadText(values, "card.background_mode", defaultOptions.BackgroundMode),
            BackgroundData = ReadBackground(values),
            FontSize = ReadNumber(values, "card.font_size", defaultOptions.FontSize),
            FontFamily = ReadText(values, "card.font_family", defaultOptions.FontFamily),
            FontBold = ReadBool(values, "card.font_bold", defaultOptions.FontBold),
            FontColor = ReadText(values, "card.font_color", defaultOptions.FontColor),
            UsernameColor = ReadText(values, "card.color.username", defaultOptions.UsernameColor),
            BorderColor = ReadText(values, "card.border_color", defaultOptions.BorderColor),
            BorderWidth = ReadNumber(values, "card.border_width", defaultOptions.BorderWidth),
            BorderRadius = ReadNumber(values, "card.border_radius", defaultOptions.BorderRadius),
            LayerScales = ReadScales(values),
            LayerColors = ReadColors(values),
            Positions = ReadPositions(values)
        };
        Validate(options);
        return options;
    }

    /// <summary>Prevent malformed RGB values entering either WPF or PDF converters.</summary>
    public static bool ValidHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' &&
        value.Skip(1).All(char.IsAsciiHexDigit);

    /// <summary>Original Python renderer's text-layer overrides.</summary>
    public static bool IsSupportedLayerStyleKey(string key)
    {
        if (key.StartsWith("card.size.", StringComparison.Ordinal))
        {
            string name = key["card.size.".Length..];
            return name != "qr" && CardArtwork.DefaultPositions.ContainsKey(name);
        }
        if (key.StartsWith("card.color.", StringComparison.Ordinal))
        {
            string name = key["card.color.".Length..];
            return name is not ("qr" or "username" or "password") &&
                CardArtwork.DefaultPositions.ContainsKey(name);
        }
        return false;
    }

    public static void ValidateLayerStyles(CardArtOptions options)
    {
        if (options.LayerScales is not null)
            foreach (var (key, factor) in options.LayerScales)
            {
                if (!IsSupportedLayerStyleKey("card.size." + key) ||
                    !double.IsFinite(factor) || factor is < 0.4 or > 4.0)
                    throw new PrintLayoutException("معامل حجم نص عنصر الكرت غير صالح.");
            }
        if (options.LayerColors is not null)
            foreach (var (key, color) in options.LayerColors)
            {
                if (!IsSupportedLayerStyleKey("card.color." + key) ||
                    !ValidHexColor(color))
                    throw new PrintLayoutException("لون نص عنصر الكرت غير صالح.");
            }
    }

    private static IReadOnlyDictionary<string, double> ReadScales(
        IReadOnlyDictionary<string, JsonElement> values)
    {
        var scales = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string key in CardArtwork.DefaultPositions.Keys.Where(
            k => k != "qr"))
        {
            string field = "card.size." + key;
            if (values.ContainsKey(field))
                scales[key] = ReadNumber(values, field, 1.0);
        }
        return scales;
    }

    private static IReadOnlyDictionary<string, string> ReadColors(
        IReadOnlyDictionary<string, JsonElement> values)
    {
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in CardArtwork.DefaultPositions.Keys.Where(
            k => k is not ("qr" or "username" or "password")))
        {
            string field = "card.color." + key;
            if (values.ContainsKey(field))
                colors[key] = ReadText(values, field, "");
        }
        return colors;
    }

    public static void Validate(CardArtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateLayerStyles(options);
        if (options.FontFamily is not ("Arial" or "Tahoma"))
            throw new PrintLayoutException("يُدعم خط Arial أو Tahoma فقط في طباعتي PDF وWindows.");
        if (!double.IsFinite(options.FontSize) ||
            options.FontSize is < 1 or > 300 ||
            !double.IsFinite(options.BorderWidth) ||
            options.BorderWidth is < 0 or > 100 ||
            !double.IsFinite(options.BorderRadius) ||
            options.BorderRadius is < 0 or > 100 ||
            !ValidHexColor(options.FontColor) ||
            !ValidHexColor(options.UsernameColor) ||
            !ValidHexColor(options.BorderColor))
            throw new PrintLayoutException("حجم أو لون الخط أو الإطار غير صالح.");
        if (options.BackgroundMode is not ("stretch" or "fit" or "fill" or "crop" or "tile"))
            throw new PrintLayoutException("وضع صورة خلفية الكرت غير مدعوم.");
        _ = CardBackgroundAsset.Decode(options.BackgroundData);
        if (options.QrMode is not ("username" or "credentials" or "login_url"))
            throw new PrintLayoutException("نوع رمز QR غير مدعوم.");
        if (!double.IsFinite(options.QrSizeMm) ||
            options.QrSizeMm is < 1 or > 300)
            throw new PrintLayoutException("مقاس رمز QR خارج النطاق.");
        if (options.QrMode == "login_url" && options.ShowQr &&
            (!Uri.TryCreate(options.QrLoginUrl, UriKind.Absolute, out Uri? uri) ||
                uri.Scheme is not ("http" or "https")))
            throw new PrintLayoutException("رابط QR غير صالح.");
        foreach (string value in new[] {
            options.Title, options.Phone, options.Other, options.Other2,
            options.Price, options.Capacity, options.Duration,
            options.Validity, options.Date })
        {
            if (value is null || value.Length > 200 || value.Any(char.IsControl))
                throw new PrintLayoutException("نص تصميم الكرت غير صالح.");
        }
        if (options.Positions is not null)
            foreach (var (key, point) in options.Positions)
            {
                if (!CardArtwork.DefaultPositions.ContainsKey(key) ||
                    !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                    point.X is < 0 or > 280 || point.Y is < 0 or > 130)
                    throw new PrintLayoutException("موضع عنصر في تصميم الكرت غير صالح.");
            }
    }

    private static string? ReadBackground(IReadOnlyDictionary<string, JsonElement> values)
    {
        if (!values.TryGetValue("card.background_data", out JsonElement raw))
            return null;
        if (raw.ValueKind != JsonValueKind.String)
            throw new PrintLayoutException("بيانات صورة الخلفية غير صالحة.");
        return raw.GetString();
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, JsonElement> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var raw)) return fallback;
        return raw.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new PrintLayoutException("قيمة ظهور عنصر غير صالحة: " + key)
        };
    }

    private static string ReadText(
        IReadOnlyDictionary<string, JsonElement> values, string key, string fallback)
    {
        if (!values.TryGetValue(key, out var raw)) return fallback;
        if (raw.ValueKind != JsonValueKind.String)
            throw new PrintLayoutException("قيمة نصية غير صالحة: " + key);
        return raw.GetString() ?? "";
    }

    private static double ReadNumber(
        IReadOnlyDictionary<string, JsonElement> values, string key, double fallback)
    {
        if (!values.TryGetValue(key, out var raw)) return fallback;
        double parsed = 0;
        bool ok = raw.ValueKind switch
        {
            JsonValueKind.Number => raw.TryGetDouble(out parsed),
            JsonValueKind.String => double.TryParse(raw.GetString(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out parsed),
            _ => false
        };
        if (!ok || !double.IsFinite(parsed))
            throw new PrintLayoutException("قيمة رقمية غير صالحة: " + key);
        return parsed;
    }

    private static IReadOnlyDictionary<string, CardArtPoint> ReadPositions(
        IReadOnlyDictionary<string, JsonElement> values)
    {
        var merged = CardArtwork.DefaultPositions.ToDictionary(
            item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (!values.TryGetValue("card.positions", out var raw))
            return merged;
        if (raw.ValueKind != JsonValueKind.Object)
            throw new PrintLayoutException("إعدادات مواضع العناصر غير صالحة.");
        foreach (JsonProperty item in raw.EnumerateObject())
        {
            if (!merged.ContainsKey(item.Name) ||
                item.Value.ValueKind != JsonValueKind.Array ||
                item.Value.GetArrayLength() != 2)
                throw new PrintLayoutException("عنصر أو موضع تصميم الكرت غير صالح.");
            JsonElement x = item.Value[0], y = item.Value[1];
            if (x.ValueKind != JsonValueKind.Number || !x.TryGetDouble(out double xx) ||
                y.ValueKind != JsonValueKind.Number || !y.TryGetDouble(out double yy))
                throw new PrintLayoutException("إحداثيات عنصر الكرت يجب أن تكون رقمية.");
            merged[item.Name] = new CardArtPoint(xx, yy);
        }
        return merged;
    }
}
