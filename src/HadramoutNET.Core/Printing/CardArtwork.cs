using System.Globalization;
using System.Text;
using HadramoutNET.Core.Cards;
using QRCoder;

namespace HadramoutNET.Core.Printing;

public readonly record struct CardArtPoint(double X, double Y);
public sealed record CardArtOptions(
    bool ShowTitle = false,
    bool ShowSequence = false,
    bool ShowPhone = false,
    bool ShowOther = false,
    bool ShowOther2 = false,
    bool ShowPrice = false,
    bool ShowDuration = false,
    bool ShowCapacity = false,
    bool ShowValidity = false,
    bool ShowDate = false,
    bool ShowQr = false,
    string Title = "شبكة حضرموت",
    string Phone = "رقم الهاتف",
    string Other = "أخرى 1",
    string Other2 = "أخرى 2",
    string Price = "",
    string Duration = "",
    string Capacity = "",
    string Validity = "",
    string Date = "",
    string QrMode = "credentials",
    string QrLoginUrl = "http://hotspot.local/login",
    double QrSizeMm = 6.0,
    IReadOnlyDictionary<string, CardArtPoint>? Positions = null,
    string? BackgroundData = null,
    string BackgroundMode = "stretch");

public sealed record CardArtText(
    string Key, string Value, double X, double Y, double Width,
    double Height, double FontPt, string Color, bool Bold);

public sealed record CardArtQr(double X, double Y, double Size,
    string Payload, bool[,] Modules);

public sealed record CardArtScene(
    double Width, double Height, string Background, string Accent,
    string Border, IReadOnlyList<CardArtText> Texts, CardArtQr? Qr);

/// <summary>
/// One pure model of card artwork for BOTH PDF and Windows preview/printing.
/// Physical units are millimeters; the renderer is responsible only for
/// converting to PDF points or Windows DIPs. User credentials never persist.
/// </summary>
public static class CardArtwork
{
    private static readonly IReadOnlyDictionary<string, CardArtPoint> OriginalPositions =
        new Dictionary<string, CardArtPoint>(StringComparer.Ordinal)
        {
            ["title"] = new(141, 9), ["sequence"] = new(4, 118),
            ["username"] = new(76, 13), ["password"] = new(122, 58),
            ["price"] = new(8, 8), ["capacity"] = new(142, 71),
            ["duration"] = new(223, 71), ["validity"] = new(4, 42),
            ["date"] = new(4, 111), ["phone"] = new(182, 96),
            ["qr"] = new(7, 37), ["other"] = new(224, 89),
            ["other2"] = new(225, 67)
        };

    public static IReadOnlyDictionary<string, CardArtPoint> DefaultPositions =>
        OriginalPositions;

    public static CardArtScene Create(Voucher card, double widthMm, double heightMm,
        CardArtOptions options)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(widthMm) || !double.IsFinite(heightMm) ||
            widthMm is < 1 or > 300 || heightMm is < 1 or > 300)
            throw new PrintLayoutException("أبعاد الكرت غير صالحة.");
        if (string.IsNullOrWhiteSpace(card.Username) ||
            card.Username.Length > 64 || card.Password.Length > 64 ||
            card.Username.Any(char.IsControl) || card.Password.Any(char.IsControl))
            throw new PrintLayoutException("اسم المستخدم أو كلمة المرور غير صالح للطباعة.");
        if (!double.IsFinite(options.QrSizeMm) || options.QrSizeMm is < 1 or > 300)
            throw new PrintLayoutException("حجم QR غير صالح.");

        var positions = OriginalPositions.ToDictionary(
            item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (options.Positions is not null)
            foreach (var (key, point) in options.Positions)
            {
                if (!OriginalPositions.ContainsKey(key) ||
                    !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                    point.X is < 0 or > 280 || point.Y is < 0 or > 130)
                    throw new PrintLayoutException("موضع عنصر تصميم الكرت غير صالح.");
                positions[key] = point;
            }

        var texts = new List<CardArtText>();
        // 14px at 280px reference width -> about 7pt at 50mm, with a 1.35x
        // credential emphasis as in core/render.py. PDF and WPF use the same
        // physical point sizes from this scene, rather than separate styles.
        double basePt = Math.Clamp(14 * (widthMm / 50) * 0.51, 4.0, 32.0);
        double bigPt = basePt * 1.35;
        double smallPt = basePt * 0.75;

        void Add(string key, string value, bool enabled, double fontPt,
            string color, bool required = false)
        {
            if (!enabled || string.IsNullOrEmpty(value)) return;
            if (value.Length > 256 || value.Any(char.IsControl))
                throw new PrintLayoutException("نص بطاقة غير صالح أو طويل.");
            var point = positions[key];
            double x = point.X / 280 * widthMm;
            double y = point.Y / 130 * heightMm;
            double height = Math.Max(1.1, fontPt * 1.25 / PrintLayouts.PointsPerMillimeter);
            double estimatedWidth = value.EnumerateRunes().Count() *
                fontPt * 0.59 / PrintLayouts.PointsPerMillimeter + 0.9;
            double allowedWidth = widthMm - x - 0.35;
            if (required && (allowedWidth <= 0 || estimatedWidth > allowedWidth + 0.01 ||
                y + height > heightMm + 0.01))
                throw new PrintLayoutException("بيانات الدخول خارج حدود الكرت؛ غيّر مواضع النصوص أو أبعاد الكرت.");
            double actualWidth = Math.Min(Math.Max(0.1, allowedWidth), estimatedWidth);
            double actualHeight = Math.Min(height, Math.Max(0.1, heightMm - y));
            if (required && actualHeight < height - 0.01)
                throw new PrintLayoutException("بيانات الدخول خارج حدود الكرت.");
            texts.Add(new CardArtText(key, value, x, y, actualWidth,
                actualHeight, fontPt, color, true));
        }

        Add("title", options.Title, options.ShowTitle, bigPt, "#7D0F24");
        Add("sequence", "#" + card.Sequence, options.ShowSequence, smallPt, "#7D0F24");
        Add("username", card.Username, true, bigPt, "#17110B", required: true);
        Add("password", card.Password, card.Password.Length != 0, bigPt, "#17110B",
            required: true);
        // Python render.py uses the individual card's price, bytes,
        // uptime, validity and generation date. An explicitly edited label
        // takes precedence only when the operator supplied one.
        string price = string.IsNullOrEmpty(options.Price) ?
            card.Price.ToString("G29", CultureInfo.InvariantCulture) : options.Price;
        string capacity = string.IsNullOrEmpty(options.Capacity) ?
            VoucherCardMetadata.ReadableBytes(card.LimitBytes) : options.Capacity;
        string duration = string.IsNullOrEmpty(options.Duration) ?
            card.LimitUptime : options.Duration;
        string validity = string.IsNullOrEmpty(options.Validity) ?
            card.ValidityDays.ToString(CultureInfo.InvariantCulture) + " يوم" : options.Validity;
        string date = string.IsNullOrEmpty(options.Date) ?
            (card.CreatedAt ?? "").Split('T', ' ')[0] : options.Date;
        Add("price", price, options.ShowPrice, bigPt, "#7D0F24");
        Add("capacity", capacity, options.ShowCapacity, basePt, "#7D0F24");
        Add("duration", duration, options.ShowDuration, basePt, "#7D0F24");
        Add("validity", validity, options.ShowValidity, basePt, "#7D0F24");
        Add("date", date, options.ShowDate, smallPt, "#7D0F24");
        Add("phone", options.Phone, options.ShowPhone, smallPt, "#7D0F24");
        Add("other", options.Other, options.ShowOther, smallPt, "#7D0F24");
        Add("other2", options.Other2, options.ShowOther2, smallPt, "#7D0F24");

        var user = texts.Single(t => t.Key == "username");
        CardArtText? password = texts.FirstOrDefault(t => t.Key == "password");
        if (password is not null && Overlaps(user.X, user.Y, user.Width,
                user.Height, password.X, password.Y, password.Width, password.Height))
            throw new PrintLayoutException("اسم المستخدم وكلمة المرور متداخلان على الكرت.");

        CardArtQr? qr = null;
        if (options.ShowQr)
        {
            var p = positions["qr"];
            double size = Math.Min(options.QrSizeMm, Math.Min(widthMm, heightMm));
            double x = Math.Min(p.X / 280 * widthMm, widthMm - size);
            double y = Math.Min(p.Y / 130 * heightMm, heightMm - size);
            if (texts.Where(t => t.Key is "username" or "password")
                .Any(t => Overlaps(t.X, t.Y, t.Width, t.Height, x, y, size, size)))
                throw new PrintLayoutException("رمز QR يغطي بيانات الدخول؛ غيّر موضعه أو مقاسه.");
            string payload = QrPayload(options, card);
            if (Encoding.UTF8.GetByteCount(payload) > 1024)
                throw new PrintLayoutException("محتوى QR طويل جدا.");
            using var generator = new QRCodeGenerator();
            using QRCodeData encoded = generator.CreateQrCode(
                payload, QRCodeGenerator.ECCLevel.M);
            int count = encoded.ModuleMatrix.Count;
            var modules = new bool[count, count];
            for (int row = 0; row < count; row++)
                for (int col = 0; col < count; col++)
                    modules[row, col] = encoded.ModuleMatrix[row][col];
            qr = new CardArtQr(x, y, size, payload, modules);
        }
        return new CardArtScene(widthMm, heightMm, "#FFFAF1", "#7D0F24",
            "#C59F63", texts, qr);
    }

    public static string QrPayload(CardArtOptions options, Voucher card)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(card);
        if (options.QrMode == "username") return card.Username;
        if (options.QrMode == "credentials")
            return card.Username + (card.Password.Length > 0 ? "|" + card.Password : "");
        if (options.QrMode != "login_url" ||
            !Uri.TryCreate(options.QrLoginUrl, UriKind.Absolute, out Uri? url) ||
            url.Scheme is not ("http" or "https"))
            throw new PrintLayoutException("وضع QR أو رابط تسجيل الدخول غير صالح.");

        // Same replacement behavior as Python's explicitly templated URL,
        // while keeping percent-encoded credentials out of logs and state.
        if (options.QrLoginUrl.Contains("{username}", StringComparison.Ordinal) ||
            options.QrLoginUrl.Contains("{password}", StringComparison.Ordinal))
            return options.QrLoginUrl
                .Replace("{username}", Uri.EscapeDataString(card.Username),
                    StringComparison.Ordinal)
                .Replace("{password}", Uri.EscapeDataString(card.Password),
                    StringComparison.Ordinal);
        string baseQuery = url.Query.TrimStart('?');
        string query = string.Join("&", baseQuery.Split('&',
            StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => !entry.StartsWith("username=", StringComparison.OrdinalIgnoreCase) &&
                            !entry.StartsWith("password=", StringComparison.OrdinalIgnoreCase)));
        if (query.Length != 0) query += "&";
        query += "username=" + Uri.EscapeDataString(card.Username);
        if (card.Password.Length > 0)
            query += "&password=" + Uri.EscapeDataString(card.Password);
        var builder = new UriBuilder(url) { Query = query };
        return builder.Uri.AbsoluteUri;
    }

    private static bool Overlaps(double x1, double y1, double w1, double h1,
        double x2, double y2, double w2, double h2) =>
        x1 < x2 + w2 && x1 + w1 > x2 &&
        y1 < y2 + h2 && y1 + h1 > y2;
}
