using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HadramoutNET.Core.Cards;

public sealed record CardGenerationOptions(
    int Quantity, int CodeLength, string UsernameCharacters,
    string Format = "code_only",
    int PasswordLength = 6, string PasswordCharacters = "abcdef",
    string SequenceMode = "0000", bool AvoidDuplicate = true,
    int StartSequence = 1, string UsernamePrefix = "");

public sealed record Voucher(string Sequence, string Username, string Password,
    decimal Price = 0, long LimitBytes = 0, string LimitUptime = "",
    int ValidityDays = 0, string CreatedAt = "");

public sealed class CardGenerationException(string message) : ArgumentException(message);

/// <summary>
/// A standalone C# port of the core voucher rules. Does NOT access the original
/// Python database or perform any remote MikroTik operation.
/// </summary>
public static class VoucherGenerator
{
    public const int MaxCodeLength = 64;
    public const int MaxBatchSize = 10000;

    public static IReadOnlyList<Voucher> Generate(
        CardGenerationOptions options,
        Func<string, bool> usernameExists,
        Func<int, int>? randomIndex = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(usernameExists);
        randomIndex ??= RandomNumberGenerator.GetInt32;

        if (options.Format is not ("code_only" or "code_same_password" or "code_password"))
            throw new CardGenerationException("صيغة بيانات الدخول غير صحيحة.");
        if (options.Quantity < 1 || options.Quantity > MaxBatchSize)
            throw new CardGenerationException("الكمية يجب أن تكون بين 1 و10000 كرت.");
        if (options.StartSequence < 0 || (long)options.StartSequence + options.Quantity > int.MaxValue)
            throw new CardGenerationException("بداية الترقيم خارج النطاق المسموح.");
        if (options.CodeLength < 1 || options.CodeLength > MaxCodeLength)
            throw new CardGenerationException("طول اسم المستخدم يجب أن يكون بين 1 و64.");

        string alphabet = ValidateAlphabet(LowerAscii(options.UsernameCharacters), "أحرف اسم المستخدم");
        Rune[] usernameAlphabet = alphabet.EnumerateRunes().ToArray();
        string prefix = LowerAscii(options.UsernamePrefix).Trim();
        Rune[] prefixRunes = prefix.EnumerateRunes().ToArray();

        if (prefixRunes.Length > options.CodeLength)
            throw new CardGenerationException("بادئة رقم الكرت أطول من طول اسم المستخدم المحدد.");
        if (prefixRunes.Any(BadRune))
            throw new CardGenerationException("بادئة رقم الكرت لا تقبل المسافات أو رموز التحكم.");
        if (usernameAlphabet.All(Rune.IsDigit) && prefixRunes.Any(r => !Rune.IsDigit(r)))
            throw new CardGenerationException("بادئة رقم الكرت يجب أن تكون أرقاما فقط.");
        if (usernameAlphabet.All(IsAsciiLower) && prefixRunes.Any(r => !IsAsciiLower(r)))
            throw new CardGenerationException("بادئة رقم الكرت يجب أن تكون أحرفا إنجليزية فقط.");

        int randomLength = options.CodeLength - prefixRunes.Length;
        Rune[] passwordAlphabet = [];
        string? fixedPassword = null;
        if (options.Format == "code_password")
        {
            if (options.PasswordLength < 1 || options.PasswordLength > MaxCodeLength)
                throw new CardGenerationException("طول كلمة المرور يجب أن يكون بين 1 و64.");
            passwordAlphabet = ValidateAlphabet(LowerAscii(options.PasswordCharacters), "أحرف كلمة المرور")
                .EnumerateRunes().ToArray();
            if (passwordAlphabet.Length == 1)
                fixedPassword = Repeat(passwordAlphabet[0], options.PasswordLength);
        }

        long capacity = 1;
        for (int i = 0; i < randomLength; ++i)
        {
            capacity *= usernameAlphabet.Length;
            if (capacity > MaxBatchSize * 1000L) break;
        }

        if (fixedPassword is not null &&
            fixedPassword.EnumerateRunes().Count() == options.CodeLength &&
            fixedPassword.StartsWith(prefix, StringComparison.Ordinal))
        {
            string suffix = string.Concat(fixedPassword.EnumerateRunes().Skip(prefixRunes.Length).Select(r => r.ToString()));
            if (suffix.EnumerateRunes().All(r => usernameAlphabet.Contains(r)))
                capacity--;
        }

        if (options.Quantity > capacity)
            throw new CardGenerationException("تركيبة الأحرف والطول لا تكفي لتوليد كروت مختلفة.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var vouchers = new List<Voucher>(options.Quantity);
        int maxAttempts = Math.Max(200, options.Quantity * 80);

        for (int attempts = 0; vouchers.Count < options.Quantity;)
        {
            if (++attempts > maxAttempts)
                throw new CardGenerationException("تعذر العثور على أسماء مستخدمين غير مكررة.");

            string username = prefix + Token(usernameAlphabet, randomLength, randomIndex);
            // The old AvoidDuplicate option is intentionally ignored: all vouchers
            // must have unique credentials regardless of an imported preset.
            if (username == fixedPassword || seen.Contains(username) || usernameExists(username))
                continue;
            seen.Add(username);

            string password = "";
            if (options.Format == "code_same_password")
                password = username;
            else if (options.Format == "code_password")
            {
                password = Token(passwordAlphabet, options.PasswordLength, randomIndex);
                if (password == username)
                {
                    int index = Choose(randomIndex, options.PasswordLength);
                    Rune[] original = password.EnumerateRunes().ToArray();
                    Rune[] alternatives = passwordAlphabet.Where(r => r != original[index]).ToArray();
                    if (alternatives.Length == 0)
                        throw new CardGenerationException("تعذر اختيار كلمة مرور مختلفة.");
                    original[index] = alternatives[Choose(randomIndex, alternatives.Length)];
                    password = string.Concat(original.Select(r => r.ToString()));
                }
            }

            long seq = (long)options.StartSequence + vouchers.Count;
            int width = options.SequenceMode switch
            {
                "00" => 2, "000" => 3, "0000" => 4, _ => 0
            };
            string sequence = width == 0
                ? seq.ToString(CultureInfo.InvariantCulture)
                : seq.ToString("D" + width.ToString(CultureInfo.InvariantCulture),
                               CultureInfo.InvariantCulture);
            vouchers.Add(new Voucher(sequence, username, password));
        }
        return vouchers;
    }

    private static string Token(Rune[] alphabet, int length, Func<int, int> randomIndex)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < length; i++)
            sb.Append(alphabet[Choose(randomIndex, alphabet.Length)].ToString());
        return sb.ToString();
    }

    private static int Choose(Func<int, int> randomIndex, int limit)
    {
        int index = randomIndex(limit);
        if ((uint)index >= (uint)limit)
            throw new CardGenerationException("Invalid random choice.");
        return index;
    }

    private static string Repeat(Rune r, int length)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < length; ++i) sb.Append(r.ToString());
        return sb.ToString();
    }

    private static bool IsAsciiLower(Rune r) => r.Value is >= 'a' and <= 'z';

    private static bool BadRune(Rune r)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(r);
        return Rune.IsWhiteSpace(r) ||
               category is UnicodeCategory.Control or UnicodeCategory.Format
                   or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                   or UnicodeCategory.OtherNotAssigned;
    }

    private static string ValidateAlphabet(string? raw, string label)
    {
        var unique = new HashSet<Rune>();
        var sb = new StringBuilder();
        foreach (Rune rune in (raw ?? "").EnumerateRunes())
        {
            if (BadRune(rune)) throw new CardGenerationException("أحرف غير مسموحة في " + label);
            if (unique.Add(rune)) sb.Append(rune.ToString());
        }
        if (unique.Count == 0) throw new CardGenerationException("يرجى تحديد أحرف في " + label);
        return sb.ToString();
    }

    private static string LowerAscii(string? value)
    {
        var sb = new StringBuilder();
        foreach (char c in value ?? "")
            sb.Append(c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c);
        return sb.ToString();
    }
}
