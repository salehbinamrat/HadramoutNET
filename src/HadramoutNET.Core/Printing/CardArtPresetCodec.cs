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
            ["card.qr_mode"] = JsonSerializer.SerializeToElement(value.QrMode),
            ["card.qr_login_url"] = JsonSerializer.SerializeToElement(value.QrLoginUrl),
            ["card.qr_size_mm"] = JsonSerializer.SerializeToElement(value.QrSizeMm),
            ["card.background_mode"] = JsonSerializer.SerializeToElement(value.BackgroundMode),
            ["card.background_data"] = JsonSerializer.SerializeToElement(value.BackgroundData ?? ""),
            ["card.positions"] = JsonSerializer.SerializeToElement(
                (value.Positions ?? CardArtwork.DefaultPositions)
                    .ToDictionary(kv => kv.Key, kv => new[] { kv.Value.X, kv.Value.Y },
                        StringComparer.Ordinal))
        };
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
            QrMode = ReadText(values, "card.qr_mode", defaultOptions.QrMode),
            QrLoginUrl = ReadText(values, "card.qr_login_url", defaultOptions.QrLoginUrl),
            QrSizeMm = ReadNumber(values, "card.qr_size_mm", defaultOptions.QrSizeMm),
            BackgroundMode = ReadText(values, "card.background_mode", defaultOptions.BackgroundMode),
            BackgroundData = ReadBackground(values),
            Positions = ReadPositions(values)
        };
        Validate(options);
        return options;
    }

    public static void Validate(CardArtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.BackgroundMode is not ("stretch" or "fit" or "fill" or "tile"))
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
            options.Title, options.Phone, options.Other, options.Other2 })
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
