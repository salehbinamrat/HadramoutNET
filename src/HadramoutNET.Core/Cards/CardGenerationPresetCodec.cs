using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HadramoutNET.Core.Cards;

/// <summary>
/// Python core/db.py creation settings with backwards-compatible .NET v1 keys.
/// Always validates the entire preset before a UI may apply any of its values.
/// </summary>
public static class CardGenerationPresetCodec
{
    public static CardGenerationOptions FromSettings(
        IReadOnlyDictionary<string, JsonElement> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var result = new CardGenerationOptions(
            Quantity: Number(settings, "create.quantity", 20, 1, VoucherGenerator.MaxBatchSize),
            CodeLength: Number(settings, "create.code_length", 8, 1, VoucherGenerator.MaxCodeLength),
            UsernameCharacters: Text(settings, "create.username_chars", "create.characters", "0123456789"),
            Format: Text(settings, "create.card_format", "create.format", "code_only"),
            PasswordLength: Number(settings, "create.password_length", 6, 1, VoucherGenerator.MaxCodeLength),
            PasswordCharacters: Text(settings, "create.password_chars", null,
                "abcdefghijklmnopqrstuvwxyz0123456789"),
            SequenceMode: Text(settings, "create.sequence_mode", null, "0000"),
            AvoidDuplicate: Boolean(settings, "create.avoid_duplicate", true),
            StartSequence: Number(settings, "create.start_sequence", 1, 0, int.MaxValue),
            UsernamePrefix: Text(settings, "create.username_prefix", null, ""));
        Validate(result);
        return result;
    }

    public static Dictionary<string, JsonElement> ToSettings(CardGenerationOptions options)
    {
        Validate(options);
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["create.quantity"] = JsonSerializer.SerializeToElement(options.Quantity),
            ["create.code_length"] = JsonSerializer.SerializeToElement(options.CodeLength),
            ["create.username_chars"] = JsonSerializer.SerializeToElement(options.UsernameCharacters),
            ["create.card_format"] = JsonSerializer.SerializeToElement(options.Format),
            ["create.password_length"] = JsonSerializer.SerializeToElement(options.PasswordLength),
            ["create.password_chars"] = JsonSerializer.SerializeToElement(options.PasswordCharacters),
            ["create.sequence_mode"] = JsonSerializer.SerializeToElement(options.SequenceMode),
            ["create.avoid_duplicate"] = JsonSerializer.SerializeToElement(options.AvoidDuplicate),
            ["create.start_sequence"] = JsonSerializer.SerializeToElement(options.StartSequence),
            ["create.username_prefix"] = JsonSerializer.SerializeToElement(options.UsernamePrefix)
        };
    }

    public static void Validate(CardGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Quantity is < 1 or > VoucherGenerator.MaxBatchSize ||
            options.CodeLength is < 1 or > VoucherGenerator.MaxCodeLength ||
            options.PasswordLength is < 1 or > VoucherGenerator.MaxCodeLength ||
            options.StartSequence < 0 ||
            (long)options.StartSequence + options.Quantity > int.MaxValue)
            throw new CardGenerationException("أعداد توليد الكروت خارج النطاق المسموح.");
        if (options.Format is not ("code_only" or "code_same_password" or "code_password"))
            throw new CardGenerationException("صيغة إنشاء الكروت غير معروفة.");
        if (options.SequenceMode is not ("00" or "000" or "0000" or "plain"))
            throw new CardGenerationException("صيغة ترقيم الكروت غير مدعومة.");
        if (string.IsNullOrWhiteSpace(options.UsernameCharacters) ||
            options.UsernameCharacters.EnumerateRunes().Any(InvalidRune) ||
            options.UsernameCharacters.EnumerateRunes().Count() > 128)
            throw new CardGenerationException("حروف اسم المستخدم غير صالحة.");
        if (options.Format == "code_password" &&
            (string.IsNullOrWhiteSpace(options.PasswordCharacters) ||
             options.PasswordCharacters.EnumerateRunes().Any(InvalidRune) ||
             options.PasswordCharacters.EnumerateRunes().Count() > 128))
            throw new CardGenerationException("حروف كلمة المرور غير صالحة.");
        string prefix = options.UsernamePrefix ?? "";
        if (prefix.EnumerateRunes().Count() > options.CodeLength ||
            prefix.EnumerateRunes().Any(InvalidRune))
            throw new CardGenerationException("بادئة رقم الكرت غير صالحة أو طويلة.");
        var alphabet = options.UsernameCharacters.EnumerateRunes().ToArray();
        if (alphabet.All(Rune.IsDigit) && prefix.EnumerateRunes().Any(r => !Rune.IsDigit(r)))
            throw new CardGenerationException("البادئة يجب أن تكون أرقاما في نمط الأرقام فقط.");
        if (alphabet.All(r => r.Value is >= 'a' and <= 'z' or >= 'A' and <= 'Z') &&
            prefix.EnumerateRunes().Any(r => r.Value is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z')))
            throw new CardGenerationException("البادئة يجب أن تكون حروفا إنجليزية فقط.");
    }

    private static bool InvalidRune(Rune r) =>
        Rune.IsWhiteSpace(r) || Rune.GetUnicodeCategory(r) is
            UnicodeCategory.Control or UnicodeCategory.Format or
            UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or
            UnicodeCategory.OtherNotAssigned;

    private static int Number(IReadOnlyDictionary<string, JsonElement> source,
        string key, int fallback, int min, int max)
    {
        if (!source.TryGetValue(key, out var value)) return fallback;
        int parsed;
        bool valid = value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out parsed),
            JsonValueKind.String => int.TryParse(value.GetString(),
                NumberStyles.None, CultureInfo.InvariantCulture, out parsed),
            _ => false
        };
        if (!valid || parsed < min || parsed > max)
            throw new CardGenerationException("قيمة رقمية غير صالحة في الباقة: " + key);
        return parsed;
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement> source,
        string key, string? legacyKey, string fallback)
    {
        if (!source.TryGetValue(key, out var value) &&
            (legacyKey is null || !source.TryGetValue(legacyKey, out value)))
            return fallback;
        if (value.ValueKind != JsonValueKind.String)
            throw new CardGenerationException("قيمة نصية غير صالحة في الباقة: " + key);
        return value.GetString() ?? "";
    }

    private static bool Boolean(IReadOnlyDictionary<string, JsonElement> source,
        string key, bool fallback)
    {
        if (!source.TryGetValue(key, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new CardGenerationException("خيار منع التكرار غير صالح.")
        };
    }
}

/// <summary>
/// The upload identity is the operator's creation-time target. Editing service,
/// profile or UM owner after generation must not silently retarget the batch.
/// Contains no password, username or license details.
/// </summary>
public sealed record VoucherDraftTarget(string Service, string Profile, string Customer)
{
    public static VoucherDraftTarget Create(string service, string profile, string customer)
    {
        if (service is not ("hotspot" or "ppp" or "userman") ||
            string.IsNullOrWhiteSpace(profile) || profile.Length > 120 ||
            profile.Any(char.IsControl) || profile.Contains('\\') ||
            customer is null || customer.Length > 160 || customer.Any(char.IsControl))
            throw new CardGenerationException("الراوتر أو الباقة أو مالك User Manager غير صالح.");
        return new VoucherDraftTarget(service, profile.Trim(), customer.Trim());
    }

    public bool Matches(string service, string profile, string customer) =>
        string.Equals(Service, service, StringComparison.Ordinal) &&
        string.Equals(Profile, profile?.Trim(), StringComparison.Ordinal) &&
        string.Equals(Customer, customer?.Trim(), StringComparison.Ordinal);
}
