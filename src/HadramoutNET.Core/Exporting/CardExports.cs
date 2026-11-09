using System.Globalization;
using System.Text;

namespace HadramoutNET.Core.Exporting;

/// <summary>
/// Offline exports matching the pinned Python core/exporting.py contract.
/// No router commands are executed here; these are user-reviewed files only.
/// Inputs should be copied from validated local card records, never from an
/// active router connection or credentials cache.
/// </summary>
public static class CardExports
{
    public static readonly IReadOnlyList<string> CardHeaders =
    [
        "sequence", "username", "password", "profile", "server",
        "limit_uptime", "limit_bytes", "validity_days", "price",
        "status", "created_at", "comment", "service"
    ];

    private static string Get(IReadOnlyDictionary<string, string?> card, string key, string fallback = "") =>
        card.TryGetValue(key, out string? value) ? value ?? "" : fallback;

    /// <summary>
    /// Quote an untrusted value for an individual RouterOS string literal.
    /// Escapes quotes, variable expansion, backslashes and all control bytes
    /// so that a card field cannot terminate its surrounding command.
    /// </summary>
    public static string QuoteRouterOs(string? value)
    {
        var result = new StringBuilder("\"");
        foreach (char character in value ?? "")
        {
            switch (character)
            {
                case '\\': result.Append(@"\\"); break;
                case '"': result.Append("\\\""); break;
                case '$': result.Append("\\$"); break;
                case '\r': result.Append(@"\r"); break;
                case '\n': result.Append(@"\n"); break;
                case '\t': result.Append(@"\t"); break;
                default:
                    if (character < 32 || character == 127)
                    {
                        result.Append('\\');
                        result.Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
                    }
                    else result.Append(character);
                    break;
            }
        }
        return result.Append('"').ToString();
    }

    /// <summary>
    /// Creates an idempotence-guarded RouterOS import script. Retains the
    /// original distinction between Hotspot, PPP, User Manager v6 and v7.
    /// User Manager v6 refuses to export until its owner/customer is known.
    /// </summary>
    public static string ScriptText(IEnumerable<IReadOnlyDictionary<string, string?>> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>
        {
            "# Hadramout Network Cards - RouterOS 6/7",
            "# Generated offline; review the target profile before import."
        };
        foreach (var card in cards)
        {
            ArgumentNullException.ThrowIfNull(card);
            string username = Get(card, "username");
            if (string.IsNullOrWhiteSpace(username) || !names.Add(username))
                throw new ArgumentException("أسماء مستخدمين فارغة أو مكررة؛ لم يُنشأ السكربت.");

            string service = Get(card, "service", "hotspot");
            if (string.IsNullOrEmpty(service)) service = "hotspot";
            string menu;
            string field = "name";
            switch (service)
            {
                case "hotspot":
                    menu = "/ip hotspot user";
                    break;
                case "ppp":
                    menu = "/ppp secret";
                    break;
                case "userman":
                    int version = UmVersion(card);
                    if (version == 6)
                    {
                        menu = "/tool user-manager user";
                        field = "username";
                    }
                    else if (version == 7)
                    {
                        menu = "/user-manager user";
                    }
                    else
                        throw new ArgumentException("إصدار مدير المستخدمين غير مدعوم.");
                    break;
                default:
                    throw new ArgumentException("نوع خدمة غير معروف.");
            }

            string command = RouterOsUserLine(card, service);
            lines.Add($":if ([:len [{menu} find where {field}={QuoteRouterOs(username)}]] = 0) do={{\n{command}\n}} else={{ :error \"Voucher already exists; stopped\" }}");
        }
        return string.Join("\n", lines) + "\n";
    }

    private static int UmVersion(IReadOnlyDictionary<string, string?> card)
    {
        string text = Get(card, "um_version");
        if (string.IsNullOrEmpty(text)) return 0;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
            throw new ArgumentException("إصدار مدير المستخدمين غير صالح.");
        return version;
    }

    private static string RouterOsUserLine(IReadOnlyDictionary<string, string?> card, string service)
    {
        string username = QuoteRouterOs(Get(card, "username"));
        string password = QuoteRouterOs(Get(card, "password"));
        string profile = QuoteRouterOs(Get(card, "profile"));
        string comment = QuoteRouterOs(Get(card, "comment"));

        if (service == "ppp")
            return $"/ppp secret add service=pppoe name={username} password={password} profile={profile} comment={comment}";

        if (service == "userman")
        {
            int version = UmVersion(card);
            if (version == 7)
                return $"/user-manager user add name={username} password={password}\n/user-manager user-profile add user={username} profile={profile}";
            string owner = Get(card, "customer");
            if (version != 6 || string.IsNullOrEmpty(owner))
                throw new ArgumentException("لتصدير سكربت مدير المستخدمين، اتصل براوتر الدفعة واقرأ الباقة وحساب مديرها أولا.");
            string customer = QuoteRouterOs(owner);
            return $"/tool user-manager user add username={username} password={password} customer={customer}\n"
                + $":local voucherId [/tool user-manager user find where username={username}]\n"
                + ":if ([:len $voucherId] != 1) do={ :error \"Cannot identify new voucher; stopped\" }\n"
                + $"/tool user-manager user create-and-activate-profile numbers=$voucherId customer={customer} profile={profile}";
        }

        var parts = new List<string> { "/ip hotspot user add", "name=" + username };
        if (!string.IsNullOrEmpty(Get(card, "password")))
            parts.Add("password=" + password);
        string server = Get(card, "server", "all");
        if (!string.IsNullOrEmpty(server) && server != "all")
            parts.Add("server=" + QuoteRouterOs(server));
        string hotspotProfile = Get(card, "profile", "default");
        if (!string.IsNullOrEmpty(hotspotProfile))
            parts.Add("profile=" + QuoteRouterOs(hotspotProfile));
        string uptime = Get(card, "limit_uptime");
        if (!string.IsNullOrEmpty(uptime))
            parts.Add("limit-uptime=" + QuoteRouterOs(uptime));
        string bytes = Get(card, "limit_bytes");
        if (!string.IsNullOrEmpty(bytes))
        {
            if (!long.TryParse(bytes, NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount))
                throw new ArgumentException("سعة الكرت غير صالحة.");
            if (amount != 0)
                parts.Add("limit-bytes-total=" + amount.ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrEmpty(Get(card, "comment")))
            parts.Add("comment=" + comment);
        return string.Join(" ", parts);
    }

    /// <summary>CSV with the original 13 columns, including formula protection.</summary>
    public static string CsvText(IEnumerable<IReadOnlyDictionary<string, string?>> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var builder = new StringBuilder();
        builder.Append(string.Join(",", CardHeaders)).Append("\r\n");
        foreach (var card in cards)
        {
            ArgumentNullException.ThrowIfNull(card);
            builder.Append(string.Join(",", CardHeaders.Select(header =>
                EscapeCsv(SpreadsheetSafe(Get(card, header)))))).Append("\r\n");
        }
        return builder.ToString();
    }

    /// <summary>Legacy two-column username,password text export.</summary>
    public static string TextCsv(IEnumerable<IReadOnlyDictionary<string, string?>> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var builder = new StringBuilder();
        foreach (var card in cards)
        {
            ArgumentNullException.ThrowIfNull(card);
            builder.Append(EscapeCsv(Get(card, "username"))).Append(',')
                .Append(EscapeCsv(Get(card, "password"))).Append('\n');
        }
        return builder.ToString();
    }

    private static string SpreadsheetSafe(string value)
    {
        string probe = value.TrimStart();
        bool formula = probe.Length != 0 && "=+-@".Contains(probe[0]);
        bool controlPrefix = value.Length != 0 && value[0] is '\t' or '\r' or '\n';
        return formula || controlPrefix ? "'" + value : value;
    }

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static string SaveScript(IEnumerable<IReadOnlyDictionary<string, string?>> cards, string path) =>
        SaveAtomic(ScriptText(cards), path);

    public static string SaveCsv(IEnumerable<IReadOnlyDictionary<string, string?>> cards, string path) =>
        SaveAtomic(CsvText(cards), path);

    public static string SaveText(IEnumerable<IReadOnlyDictionary<string, string?>> cards, string path) =>
        SaveAtomic(TextCsv(cards), path);

    /// <summary>
    /// Validate and generate the complete export before opening any file.
    /// Write through a same-folder temporary file, flush to disk, then replace
    /// the destination only after the export is complete. Clean up on failure.
    /// </summary>
    private static string SaveAtomic(string content, string path) =>
        AtomicExportFile.Write(path, staged =>
            File.WriteAllText(staged, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)));
}
