using System.Globalization;
using System.Text.RegularExpressions;

namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// Per-user User Manager voucher state. An "expired" row MUST have a
/// positive Transfer Limit actually exhausted by usage counters, never
/// expired time/old assignments or lack of a current profile alone.
/// </summary>
public sealed record UserManagerLifecycleRow(
    string Username, string Profile, string State, string Reason,
    long UsedBytes, long TransferLimit);

public sealed record UserManagerLifecycleSnapshot(
    int Version, int Total, int Valid, int Active, int Expired,
    int Disabled, int Unclassified,
    IReadOnlyList<UserManagerLifecycleRow> Rows)
{
    public IEnumerable<UserManagerLifecycleRow> ForStatus(string status) =>
        Rows.Where(row => row.State == status);
}

/// <summary>
/// The Python core/network.py lifecycle semantics expressed as a deterministic
/// O(users + profileAssignments) pure function. Never store any passwords.
/// </summary>
public static partial class UserManagerLifecycleClassifier
{
    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)([kmgt]?i?b?)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ByteSuffixPattern();

    [GeneratedRegex(@"-?[0-9]+(?:\.[0-9]+)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex NumericPattern();

    [GeneratedRegex(@"([0-9]+)([wdhms])", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    public static UserManagerLifecycleSnapshot Classify(
        int version,
        IReadOnlyList<IReadOnlyDictionary<string, string>> users,
        IReadOnlyList<IReadOnlyDictionary<string, string>> assignments,
        IReadOnlyDictionary<string, long>? legacyProfileTransferLimits = null)
    {
        if (version is not (6 or 7))
            throw new ArgumentException("User Manager version must be 6 or 7.", nameof(version));
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(assignments);

        var byName = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            users.Count, StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in users)
        {
            string username = Get(row, "name", "username").Trim();
            if (string.IsNullOrWhiteSpace(username) || username.Length > 128 ||
                username.Any(char.IsControl) || !byName.TryAdd(username, row))
                throw new InvalidOperationException(
                    "User Manager returned an invalid or duplicated username.");
            aliases[username] = username;
            string id = Get(row, ".id");
            if (id.Length != 0 && !aliases.TryAdd(id, username) &&
                aliases[id] != username)
                throw new InvalidOperationException(
                    "User Manager returned a duplicate internal record identifier.");
        }

        var assigned = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (version == 7)
        {
            foreach (var item in assignments)
            {
                string reference = Get(item, "user");
                if (!aliases.TryGetValue(reference, out string? username))
                    continue;
                if (!assigned.TryGetValue(username, out List<string>? values))
                    assigned[username] = values = new List<string>();
                values.Add(NormalizeState(Get(item, "state")));
            }
        }

        var result = new List<UserManagerLifecycleRow>(users.Count);
        int valid = 0, active = 0, expired = 0, disabled = 0, unclassified = 0;
        foreach (var (username, row) in byName)
        {
            string profile = Get(row, "actual-profile", "profile");
            long down = ParseBytes(Get(row, "total-download-used", "download-used"));
            long up = ParseBytes(Get(row, "total-upload-used", "upload-used"));
            long used = down > long.MaxValue - up ? long.MaxValue : down + up;
            long limit = ParseBytes(Get(row, "transfer-limit"));
            if (version == 6 && limit == 0 && profile.Length != 0 &&
                legacyProfileTransferLimits is not null &&
                legacyProfileTransferLimits.TryGetValue(profile, out long linked) &&
                linked > 0)
                limit = linked;
            string state, reason;

            if (Flag(Get(row, "disabled")))
            {
                state = "disabled";
                reason = "معطل إداريا";
            }
            else if (version == 6 && Flag(Get(row, "incomplete")))
            {
                state = "unclassified";
                reason = "حساب غير مكتمل";
            }
            else if (version == 7)
            {
                assigned.TryGetValue(username, out List<string>? values);
                if (values?.Contains("running active") == true)
                {
                    state = "active";
                    reason = "الباقة الحالية مفعلة";
                }
                else if (values?.Contains("running") == true)
                {
                    state = "valid";
                    reason = "الباقة جاهزة للاستخدام";
                }
                else if (values is { Count: > 0 } &&
                         values.All(x => x == "used") &&
                         limit > 0 && used >= limit)
                {
                    state = "expired";
                    reason = "استنفد حد إجمالي نقل البيانات";
                }
                else
                {
                    state = "unclassified";
                    reason = "لا يوجد إثبات لاستنفاد Transfer Limit أو حالة باقة فعلية";
                }
            }
            else
            {
                bool started = UsedV6(row, used);
                if (started && limit > 0 && used >= limit)
                {
                    state = "expired";
                    reason = "استنفد حد إجمالي نقل البيانات";
                }
                else if (started && profile.Length > 0)
                {
                    state = "active";
                    reason = "بدأ استخدام الكرت وما زال رصيده غير مستنفد";
                }
                else if (!started && profile.Length > 0)
                {
                    state = "valid";
                    reason = "جاهز للاستخدام ولم يبدأ بعد";
                }
                else
                {
                    state = "unclassified";
                    reason = "حالة الكرت أو الباقة الفعلية غير مؤكدة";
                }
            }

            switch (state)
            {
                case "valid": valid++; break;
                case "active": active++; break;
                case "expired": expired++; break;
                case "disabled": disabled++; break;
                default: unclassified++; break;
            }
            result.Add(new UserManagerLifecycleRow(username, profile, state,
                reason, used, limit));
        }
        return new UserManagerLifecycleSnapshot(version, users.Count, valid, active,
            expired, disabled, unclassified, result);
    }

    public static long ParseBytes(string? value)
    {
        string input = (value ?? "").Trim().ToLowerInvariant()
            .Replace(" ", "", StringComparison.Ordinal);
        if (input.Length is 0 or > 128 ||
            input is "none" or "unlimited") return 0;
        Match match = ByteSuffixPattern().Match(input);
        if (!match.Success ||
            !decimal.TryParse(match.Groups[1].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out decimal number) ||
            number < 0) return 0;

        int power = match.Groups[2].Value.TrimEnd('b').TrimEnd('i') switch
        {
            "k" => 1, "m" => 2, "g" => 3, "t" => 4, "" => 0,
            _ => -1
        };
        if (power < 0) return 0;
        try
        {
            decimal result = number;
            for (int i = 0; i < power; i++) result *= 1024;
            return result > long.MaxValue ? 0 : (long)decimal.Truncate(result);
        }
        catch (OverflowException) { return 0; }
    }

    private static bool UsedV6(IReadOnlyDictionary<string, string> row, long used)
    {
        if (used > 0) return true;
        string money = Get(row, "money-used");
        Match n = NumericPattern().Match(money.Length > 128 ? "" : money);
        if (n.Success && decimal.TryParse(n.Value, NumberStyles.AllowLeadingSign |
                    NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                    out decimal amount) && amount > 0) return true;
        string uptime = Get(row, "uptime-used");
        if (uptime.Length > 0 && uptime.Length < 128 &&
            DurationPattern().Matches(uptime).Cast<Match>()
                .Any(x => long.TryParse(x.Groups[1].Value, out long t) && t > 0))
            return true;
        if (Flag(Get(row, "active")) ||
            long.TryParse(Get(row, "active-sessions"),
                NumberStyles.None, CultureInfo.InvariantCulture,
                out long sessions) && sessions > 0) return true;
        if (Get(row, "last-used-ip", "last-used-mac").Length > 0) return true;
        string lastSeen = Get(row, "last-seen").Trim().ToLowerInvariant();
        return lastSeen is not ("" or "never" or "unknown");
    }

    private static bool Flag(string v) =>
        v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";

    private static string Get(IReadOnlyDictionary<string, string> row,
        string key, string? fallback = null)
    {
        if (row.TryGetValue(key, out string? value)) return value ?? "";
        return fallback is not null && row.TryGetValue(fallback, out value) ?
            value ?? "" : "";
    }

    private static string NormalizeState(string text) =>
        string.Join(' ', text.Trim().ToLowerInvariant()
            .Replace('-', ' ').Replace(',', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
