using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace HadramoutNET.Core.Licensing;

public sealed record LicenseAccess(
    string Mode, string Message, string Reason, string LicenseId = "",
    string Expires = "", string RemainingTerm = "", long RemainingSeconds = -1)
{
    public bool Permitted => Mode == "active";
}

/// <summary>
/// Pure, fail-closed verification of the original v1.1.3 Supabase/Ed25519
/// signed license feed. It does not contact the network, issue a license,
/// write any activation state, or grant rights based on local files.
/// </summary>
public static class SignedLicenseFeed
{
    public const string Product = "NIDI-MIKROTIK-CARDS";
    public const string FeedUrl =
        "https://zlirqppwemfmzxkholcr.supabase.co/functions/v1/hadramout-license/licenses.json";
    public const string ActivationUrl =
        "https://zlirqppwemfmzxkholcr.supabase.co/functions/v1/hadramout-license/v1/claim";
    public const string TrustedPublicKeyBase64 =
        "yifdpnKvdmxoEGUOoQGqeytEaume9Ldp7sKPbpQuGBE=";
    public const int MaxFeedBytes = 1_048_576;
    public const int CheckIntervalSeconds = 1800;
    public const int NetworkTimeoutSeconds = 8;

    private static readonly byte[] TrustedKey = Convert.FromBase64String(TrustedPublicKeyBase64);

    private static LicenseAccess Denied(string reason, string message) => new("locked", message, reason);

    public static LicenseAccess Verify(
        byte[]? signedJson, string licenseId, string deviceId, DateTimeOffset nowUtc) =>
        VerifyWithKey(signedJson, licenseId, deviceId, nowUtc, TrustedKey);

    // Test-only trust-root seam; not accessible from the Windows UI assembly.
    internal static LicenseAccess VerifyWithKey(
        byte[]? signedJson, string licenseId, string deviceId, DateTimeOffset nowUtc, byte[] publicKey)
    {
        if (signedJson is null || signedJson.Length > MaxFeedBytes)
            return Denied("feed_invalid", "استجابة خدمة الترخيص غير صالحة.");
        if (!IsUpperHex(licenseId, 32) || !IsUpperHex(deviceId, 64))
            return Denied("input_invalid", "رمز الترخيص أو بصمة الجهاز غير صالح.");
        try
        {
            using var source = JsonDocument.Parse(signedJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            if (source.RootElement.ValueKind != JsonValueKind.Object)
                return Denied("feed_invalid", "استجابة خدمة الترخيص غير صالحة.");
            RefuseDuplicateProperties(source.RootElement);
            JsonElement root = source.RootElement;
            if (!root.TryGetProperty("signature", out JsonElement signatureElement) ||
                signatureElement.ValueKind != JsonValueKind.String ||
                !TrySignature(signatureElement.GetString(), out byte[] signature) ||
                publicKey is null || publicKey.Length != 32)
                return Denied("feed_invalid", "تعذر التحقق من توقيع الترخيص.");

            byte[] canonical = Canonicalize(root, removeRootSignature: true);
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(canonical, 0, canonical.Length);
            if (!verifier.VerifySignature(signature))
                return Denied("feed_invalid", "تعذر التحقق من توقيع الترخيص.");

            if (!root.TryGetProperty("schema", out JsonElement schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int schemaNumber) ||
                schemaNumber != 1 ||
                !root.TryGetProperty("revision", out JsonElement revision) ||
                revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt32(out int revisionNumber) ||
                revisionNumber < 1 ||
                !root.TryGetProperty("product", out JsonElement product) ||
                product.ValueKind != JsonValueKind.String || product.GetString() != Product ||
                !root.TryGetProperty("licenses", out JsonElement licenses) ||
                licenses.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("generated_at", out JsonElement generated) ||
                generated.ValueKind != JsonValueKind.String ||
                !TrySignedTimestamp(generated.GetString(), out DateTimeOffset issuedAt) ||
                issuedAt > nowUtc.ToUniversalTime().AddMinutes(5))
                return Denied("feed_invalid", "بيانات سجل التراخيص الموقعة غير صالحة.");

            string publicCodeHash = Convert.ToHexString(
                SHA256.HashData(Encoding.ASCII.GetBytes(licenseId)));
            if (!licenses.TryGetProperty(publicCodeHash, out JsonElement record) ||
                record.ValueKind != JsonValueKind.Object)
                return Denied("not_found", "الرمز لم يظهر بعد في سجل التراخيص المنشور.");

            string status = ReadString(record, "status") ?? "";
            string? recordDevice = ReadString(record, "device");
            string? expires = ReadString(record, "expires");
            bool hasExpiry = record.TryGetProperty("expires", out JsonElement expiryValue) &&
                expiryValue.ValueKind != JsonValueKind.Null;
            DateOnly today = DateOnly.FromDateTime(nowUtc.UtcDateTime);

            if (status == "pending" && recordDevice is null)
            {
                if (hasExpiry)
                {
                    if (expires is null || !TryDate(expires, out DateOnly pendingUntil))
                        return Denied("feed_invalid", "بيانات انتهاء الترخيص غير صالحة.");
                    if (pendingUntil < today)
                        return Denied("expired", "انتهت صلاحية رمز الترخيص قبل أول تفعيل.");
                }
                return Denied("pending_activation", "هذا الرمز جاهز للربط بأول جهاز عبر الإنترنت.");
            }

            if (recordDevice != deviceId)
                return Denied("wrong_device", "الترخيص مخصص لجهاز آخر.");
            if (status != "active")
                return Denied("revoked", "هذا الترخيص موقوف أو ملغى.");

            if (hasExpiry)
            {
                if (expires is null || !TryDate(expires, out DateOnly finish))
                    return Denied("feed_invalid", "بيانات انتهاء الترخيص غير صالحة.");
                if (today > finish)
                    return Denied("expired", "انتهت صلاحية الترخيص. اطلب التجديد.");
                long remainingDays = (long)finish.DayNumber - today.DayNumber + 1;
                return new LicenseAccess("active", "رمز الترخيص صالح", "online_valid",
                    licenseId, expires, remainingDays + " يوم", remainingDays * 86400);
            }

            return new LicenseAccess("active", "رمز الترخيص صالح", "online_valid",
                licenseId, "", "دائم", -1);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException
            or InvalidOperationException or CryptographicException or DecoderFallbackException
            or OverflowException)
        {
            return Denied("feed_invalid", "تعذر التحقق من التوقيع أو بيانات رمز الترخيص.");
        }
    }

    public static string NormalizeCode(string? value)
    {
        var valueBuilder = new StringBuilder();
        foreach (char letter in value ?? "")
            if (letter != '-' && !char.IsWhiteSpace(letter))
                valueBuilder.Append(char.ToUpperInvariant(letter));
        string normalized = valueBuilder.ToString();
        return IsUpperHex(normalized, 32) ? normalized : "";
    }

    public static string HashWindowsIdentifier(string rawWindowsIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawWindowsIdentifier);
        string identifier = rawWindowsIdentifier.Trim().ToLowerInvariant();
        if (identifier is "" or ":000000000000")
            throw new ArgumentException("تعذر التعرف على الجهاز.", nameof(rawWindowsIdentifier));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "HADRAMOUT-GITHUB-LICENSE-V1|" + identifier)));
    }

    private static bool IsUpperHex(string? value, int expectedLength) =>
        value is not null && value.Length == expectedLength &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool TrySignature(string? value, out byte[] signature)
    {
        signature = [];
        if (value is null || value.Length != 88 ||
            !Regex.IsMatch(value, @"\A[A-Za-z0-9+/]{86}==\z", RegexOptions.CultureInvariant))
            return false;
        try
        {
            signature = Convert.FromBase64String(value);
            return signature.Length == 64 && Convert.ToBase64String(signature) == value;
        }
        catch (FormatException) { return false; }
    }

    private static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool TrySignedTimestamp(string? value, out DateTimeOffset issued)
    {
        issued = default;
        if (string.IsNullOrWhiteSpace(value) ||
            !Regex.IsMatch(value, @"(?:Z|[+-][0-9]{2}:[0-9]{2})\z",
                RegexOptions.CultureInvariant))
            return false;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out issued);
    }

    private static string? ReadString(JsonElement record, string key)
    {
        if (!record.TryGetProperty(key, out JsonElement property))
            return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static void RefuseDuplicateProperties(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    if (!keys.Add(property.Name))
                        throw new JsonException("Duplicate JSON key.");
                    RefuseDuplicateProperties(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement value in node.EnumerateArray())
                    RefuseDuplicateProperties(value);
                break;
        }
    }

    internal static byte[] CanonicalizeForTests(byte[] unsignedJson)
    {
        using var doc = JsonDocument.Parse(unsignedJson);
        RefuseDuplicateProperties(doc.RootElement);
        return Canonicalize(doc.RootElement, removeRootSignature: false);
    }

    private static byte[] Canonicalize(JsonElement element, bool removeRootSignature)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false
        }))
        {
            WriteSorted(writer, element, removeRootSignature);
            writer.Flush();
        }
        return buffer.ToArray();
    }

    private static void WriteSorted(Utf8JsonWriter writer, JsonElement value, bool removeSignature)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject()
                    .Where(p => !removeSignature || p.Name != "signature")
                    .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteSorted(writer, property.Value, removeSignature: false);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                    WriteSorted(writer, item, removeSignature: false);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("Invalid JSON value.");
        }
    }
}
