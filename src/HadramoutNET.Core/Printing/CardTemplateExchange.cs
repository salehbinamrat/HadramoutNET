using System.Text.Json;

namespace HadramoutNET.Core.Printing;

/// <summary>
/// Import only the card.* part of a Python Hadramout card-template JSON.
/// Unsupported card fields remain clearly identified and can be preserved
/// for future parity, but are NOT falsely claimed as rendered by .NET.
/// </summary>
public sealed record CardTemplateImport(
    CardArtOptions Artwork,
    IReadOnlyDictionary<string, JsonElement> UnrecognizedCardSettings);

public static class CardTemplateExchange
{
    public const int MaxJsonBytes = 22 * 1024 * 1024;
    private static readonly HashSet<string> KnownKeys =
        CardArtPresetCodec.ToSettings(new CardArtOptions())
            .Keys.ToHashSet(StringComparer.Ordinal);

    public static CardTemplateImport Import(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length is < 2 or > MaxJsonBytes)
            throw new PrintLayoutException("ملف قالب الكرت فارغ أو أكبر من 22 ميجابايت.");
        int offset = source.Length > 2 &&
            source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF
                ? 3 : 0;
        try
        {
            using var doc = JsonDocument.Parse(source.AsMemory(offset),
                new JsonDocumentOptions { MaxDepth = 32,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new PrintLayoutException("القالب يجب أن يكون كائن JSON.");
            var cardFields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (!prop.Name.StartsWith("card.", StringComparison.Ordinal))
                    continue; // Python imports only card.* from mixed snapshots.
                if (prop.Name.Length > 120 ||
                    prop.Name.Any(char.IsControl) ||
                    !cardFields.TryAdd(prop.Name, prop.Value.Clone()))
                    throw new PrintLayoutException("اسم أو تكرار مفتاح قالب غير صالح.");
            }
            if (cardFields.Count == 0)
                throw new PrintLayoutException("لا توجد إعدادات card. في القالب.");
            RejectExternalBackgroundPath(cardFields);

            CardArtOptions art = CardArtPresetCodec.FromSettings(cardFields);
            var extra = cardFields
                .Where(item => !KnownKeys.Contains(item.Key))
                .ToDictionary(item => item.Key, item => item.Value.Clone(),
                    StringComparer.Ordinal);
            return new CardTemplateImport(art, extra);
        }
        catch (JsonException)
        {
            throw new PrintLayoutException("تنسيق JSON لقالب الكرت غير صالح.");
        }
    }

    public static byte[] Export(CardArtOptions artwork,
        IReadOnlyDictionary<string, JsonElement>? unrecognized = null)
    {
        var result = CardArtPresetCodec.ToSettings(artwork);
        if (unrecognized is not null)
        {
            foreach (var (key, value) in unrecognized)
            {
                if (!key.StartsWith("card.", StringComparison.Ordinal) ||
                    key.Length > 120 || key.Any(char.IsControl) ||
                    KnownKeys.Contains(key) || result.ContainsKey(key) ||
                    value.ValueKind == JsonValueKind.Undefined)
                    throw new PrintLayoutException(
                        "إعداد غير معروف يتعارض مع إعداد مدعوم في القالب.");
                result[key] = value.Clone();
            }
        }
        RejectExternalBackgroundPath(result);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(result,
            new JsonSerializerOptions { WriteIndented = true });
        if (json.Length > MaxJsonBytes)
            throw new PrintLayoutException("قالب الكرت يتجاوز 22 ميجابايت.");
        return json;
    }

    public static IReadOnlyDictionary<string, JsonElement> ExtractExtras(
        IReadOnlyDictionary<string, JsonElement> settings) =>
        settings.Where(item => item.Key.StartsWith("card.", StringComparison.Ordinal) &&
                          !KnownKeys.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.Value.Clone(),
                StringComparer.Ordinal);

    private static void RejectExternalBackgroundPath(
        IReadOnlyDictionary<string, JsonElement> cardFields)
    {
        if (!cardFields.TryGetValue("card.background_path", out var path))
            return;
        if (path.ValueKind != JsonValueKind.String ||
            !string.IsNullOrEmpty(path.GetString()))
            throw new PrintLayoutException(
                "لا يُسمح باستيراد أو تصدير مسار صورة خارجي. أدرج الصورة داخل القالب.");
    }
}
