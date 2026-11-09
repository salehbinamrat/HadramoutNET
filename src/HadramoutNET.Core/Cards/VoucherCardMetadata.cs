using System.Globalization;
using System.Text.Json;

namespace HadramoutNET.Core.Cards;

/// <summary>
/// Original Python core/db.py voucher fields: price, limit_bytes with chosen
/// unit, limit_uptime_h/m and validity_days. These are display metadata
/// on this .NET draft, not remotely provisioned accounting policies.
/// </summary>
public sealed record VoucherCardMetadataOptions(
    decimal Price = 10.5m, decimal Capacity = 512m,
    string CapacityUnit = "MB", int Hours = 1,
    int Minutes = 0, int ValidityDays = 7);

public static class VoucherCardMetadata
{
    private static readonly IReadOnlyDictionary<string, long> Multipliers =
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["B"] = 1, ["KB"] = 1024,
            ["MB"] = 1024L * 1024,
            ["GB"] = 1024L * 1024 * 1024,
            ["TB"] = 1024L * 1024 * 1024 * 1024
        };

    public static void Validate(VoucherCardMetadataOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (o.Price < 0 || o.Price > 1_000_000_000m ||
            o.Hours is < 0 or > 24000 ||
            o.Minutes is < 0 or > 1_440_000 ||
            o.ValidityDays is < 0 or > 36500)
            throw new CardGenerationException(
                "السعر أو المدة أو الصلاحية خارج النطاق المقبول.");
        _ = BytesTotal(o.Capacity, o.CapacityUnit);
    }

    public static long BytesTotal(decimal capacity, string unit)
    {
        if (capacity < 0 || unit is null ||
            !Multipliers.TryGetValue(unit.ToUpperInvariant(), out long multiplier))
            throw new CardGenerationException("قيمة أو وحدة سعة الكرت غير صالحة.");
        try
        {
            decimal bytes = capacity * multiplier;
            if (bytes > long.MaxValue)
                throw new CardGenerationException("سعة الكرت تتجاوز الحد المسموح.");
            return (long)decimal.Truncate(bytes);
        }
        catch (OverflowException)
        {
            throw new CardGenerationException("سعة الكرت كبيرة جدا.");
        }
    }

    public static string UptimeString(int hours, int minutes)
    {
        if (hours < 0 || minutes < 0 ||
            (long)hours + minutes / 60 > int.MaxValue)
            throw new CardGenerationException("مدة الاستخدام غير صالحة.");
        hours += minutes / 60;
        minutes %= 60;
        return (hours > 0 ? hours.ToString(CultureInfo.InvariantCulture) + "h" : "") +
               (minutes > 0 ? minutes.ToString(CultureInfo.InvariantCulture) + "m" : "");
    }

    public static string ReadableBytes(long bytes)
    {
        if (bytes < 0)
            throw new CardGenerationException("سعة الكرت غير صالحة.");
        double remaining = bytes;
        foreach (string unit in new[] { "B", "KB", "MB", "GB", "TB" })
        {
            if (remaining < 1024 || unit == "TB")
                return remaining.ToString(remaining >= 10 ? "0" : "0.0",
                    CultureInfo.InvariantCulture) + " " + unit;
            remaining /= 1024;
        }
        return "0 B";
    }

    public static IReadOnlyList<Voucher> Attach(
        IReadOnlyList<Voucher> cards,
        VoucherCardMetadataOptions options, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(cards);
        Validate(options);
        if (cards.Count is < 1 or > VoucherGenerator.MaxBatchSize)
            throw new CardGenerationException("عدد الكروت غير صالح لإضافة البيانات.");
        long bytes = BytesTotal(options.Capacity, options.CapacityUnit);
        string uptime = UptimeString(options.Hours, options.Minutes);
        string date = createdAt.ToString("yyyy-MM-ddTHH:mm:ss",
            CultureInfo.InvariantCulture);
        return cards.Select(card => card with
        {
            Price = options.Price, LimitBytes = bytes,
            LimitUptime = uptime, ValidityDays = options.ValidityDays,
            CreatedAt = date
        }).ToArray();
    }
}

/// <summary>
/// Preserve the same Python create.* card metadata keys for each package.
/// Missing values use source defaults, while malformed existing values are
/// rejected before any app state change or SQLite save.
/// </summary>
public static class VoucherCardMetadataPresetCodec
{
    public static Dictionary<string, JsonElement> ToSettings(
        VoucherCardMetadataOptions options)
    {
        VoucherCardMetadata.Validate(options);
        return new(StringComparer.Ordinal)
        {
            ["create.price"] = JsonSerializer.SerializeToElement(options.Price),
            ["create.limit_bytes"] = JsonSerializer.SerializeToElement(options.Capacity),
            ["create.limit_unit"] = JsonSerializer.SerializeToElement(options.CapacityUnit),
            ["create.limit_uptime_h"] = JsonSerializer.SerializeToElement(options.Hours),
            ["create.limit_uptime_m"] = JsonSerializer.SerializeToElement(options.Minutes),
            ["create.validity_days"] = JsonSerializer.SerializeToElement(options.ValidityDays)
        };
    }

    public static VoucherCardMetadataOptions FromSettings(
        IReadOnlyDictionary<string, JsonElement> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = new VoucherCardMetadataOptions(
            Price: ReadDecimal(settings, "create.price", 10.5m),
            Capacity: ReadDecimal(settings, "create.limit_bytes", 512m),
            CapacityUnit: ReadText(settings, "create.limit_unit", "MB"),
            Hours: ReadInteger(settings, "create.limit_uptime_h", 1),
            Minutes: ReadInteger(settings, "create.limit_uptime_m", 0),
            ValidityDays: ReadInteger(settings, "create.validity_days", 7));
        VoucherCardMetadata.Validate(options);
        return options;
    }

    private static decimal ReadDecimal(
        IReadOnlyDictionary<string, JsonElement> data, string key, decimal fallback)
    {
        if (!data.TryGetValue(key, out var element)) return fallback;
        decimal amount = 0;
        bool valid = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out amount),
            JsonValueKind.String => decimal.TryParse(element.GetString(),
                NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out amount),
            _ => false
        };
        if (!valid || amount < 0)
            throw new CardGenerationException("قيمة باقة غير صالحة: " + key);
        return amount;
    }

    private static int ReadInteger(
        IReadOnlyDictionary<string, JsonElement> data, string key, int fallback)
    {
        if (!data.TryGetValue(key, out var element)) return fallback;
        int value = 0;
        bool valid = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(element.GetString(),
                NumberStyles.None, CultureInfo.InvariantCulture, out value),
            _ => false
        };
        if (!valid || value < 0)
            throw new CardGenerationException("مدة أو صلاحية غير صالحة: " + key);
        return value;
    }

    private static string ReadText(
        IReadOnlyDictionary<string, JsonElement> data, string key, string fallback)
    {
        if (!data.TryGetValue(key, out var element)) return fallback;
        if (element.ValueKind != JsonValueKind.String)
            throw new CardGenerationException("وحدة السعة غير صالحة.");
        return element.GetString() ?? "";
    }
}
