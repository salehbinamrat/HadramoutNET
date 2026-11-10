namespace HadramoutNET.Core.Cards;

/// <summary>
/// Compatible presentation of the Python VoucherWorkspace credential controls.
/// The stored card format and character alphabets remain the existing contract.
/// </summary>
public static class VoucherCreationPresentation
{
    public const string Numeric = "0123456789";
    public const string Alphabetic = "abcdefghijklmnopqrstuvwxyz";
    public const string Alphanumeric = Numeric + Alphabetic;

    public static (string Mode, string LastPasswordRelation) FromStoredFormat(string format) =>
        format switch
        {
            "code_only" => ("username_only", "code_password"),
            "code_same_password" => ("username_password", "code_same_password"),
            "code_password" => ("username_password", "code_password"),
            _ => throw new CardGenerationException("صيغة بيانات الدخول غير مدعومة.")
        };

    public static string ResolveFormat(string mode, string lastPasswordRelation) =>
        (mode, lastPasswordRelation) switch
        {
            ("username_only", "code_password" or "code_same_password") => "code_only",
            ("username_password", "code_password") => "code_password",
            ("username_password", "code_same_password") => "code_same_password",
            _ => throw new CardGenerationException("اختر صيغة دخول صحيحة، وحدد مختلفتين أو متطابقتين.")
        };

    public static string DetectCharacterMode(string? value) => value switch
    {
        Numeric => "digits",
        Alphabetic => "letters",
        Alphanumeric => "mixed",
        _ => "custom"
    };

    public static string CharactersForMode(string mode) => mode switch
    {
        "digits" => Numeric,
        "letters" => Alphabetic,
        "mixed" => Alphanumeric,
        _ => throw new ArgumentException("نوع أحرف غير معروف؛ اكتب قيمة مخصصة يدويا.", nameof(mode))
    };
}
