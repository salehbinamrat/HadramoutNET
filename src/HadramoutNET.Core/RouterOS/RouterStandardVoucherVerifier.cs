using System.Globalization;

namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// Read-only current-state observation of Hotspot and PPPoE accounts.
/// Does not prove historical creation, credential validity or connectivity.
/// </summary>
public sealed record StandardVoucherObservation(string Username, string State);
public sealed record StandardVoucherVerificationResult(
    string Status, string Service, IReadOnlyList<StandardVoucherObservation> Cards)
{
    public bool Available => Status == "ready";
}

/// <summary>
/// Uses bounded RouterOS count-only and .proplist projections. Never reads
/// passwords or writes to the router, and never equates an incomplete
/// listing with an absent voucher.
/// </summary>
public sealed class RouterStandardVoucherVerifier
{
    public const int MaxRows = 5000;
    private static readonly IReadOnlyDictionary<string, string> CountOnly =
        new Dictionary<string, string> { ["count-only"] = "yes" };

    public async Task<StandardVoucherVerificationResult> VerifyAsync(
        RouterAuthorizedSession session, string service,
        IReadOnlyList<string> usernames, string expectedProfile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(usernames);
        if (service is not ("hotspot" or "ppp"))
            throw new ArgumentException("خدمة الكروت المطلوبة غير صالحة.", nameof(service));
        if (usernames.Count is < 1 or > MaxRows ||
            string.IsNullOrWhiteSpace(expectedProfile) ||
            expectedProfile.Length > 80 || expectedProfile.Any(char.IsControl))
            throw new ArgumentException("حجم المسودة أو الباقة غير صالح للفحص.");

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string username in usernames)
        {
            if (string.IsNullOrWhiteSpace(username) || username.Length > 64 ||
                username.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
                !unique.Add(username))
                throw new ArgumentException("الأسماء غير صالحة أو مكررة.");
        }

        if (!session.HasCurrentAccess)
            return Unavailable("not_authorized", service);

        string menu = service == "hotspot" ? "/ip/hotspot/user/print" : "/ppp/secret/print";
        string fields = service == "hotspot" ?
            "name,profile,disabled" : "name,profile,disabled,service";

        try
        {
            var countResult = await session.ReadAsync(menu, CountOnly,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!countResult.Done.TryGetValue("ret", out string? countText) ||
                !int.TryParse(countText, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int expectedCount) ||
                expectedCount < 0)
                return Unavailable("unavailable", service);
            if (expectedCount > MaxRows)
                return Unavailable("limit_exceeded", service);

            var result = await session.ReadAsync(menu,
                new Dictionary<string, string> { [".proplist"] = fields },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Rows.Count != expectedCount)
                return Unavailable("incomplete", service);

            var observed = new Dictionary<string, (string Profile, string Disabled, string Service)>(
                StringComparer.Ordinal);
            foreach (var row in result.Rows)
            {
                if (!row.TryGetValue("name", out string? name) ||
                    string.IsNullOrWhiteSpace(name) || name.Length > 64 ||
                    !observed.TryAdd(name, (
                        Field(row, "profile"), Field(row, "disabled"),
                        Field(row, "service"))))
                    return Unavailable("incomplete", service);
            }

            var matches = new List<StandardVoucherObservation>(usernames.Count);
            foreach (string username in usernames)
            {
                string state;
                if (!observed.TryGetValue(username, out var entry))
                    state = "not_found";
                else if (IsDisabled(entry.Disabled))
                    state = "disabled";
                else if (string.IsNullOrWhiteSpace(entry.Profile))
                    state = "profile_unknown";
                else if (!string.Equals(entry.Profile, expectedProfile,
                             StringComparison.Ordinal))
                    state = "profile_other";
                else if (service == "ppp" &&
                    !string.Equals(entry.Service, "pppoe", StringComparison.OrdinalIgnoreCase))
                    state = "service_other";
                else
                    state = "profile_matches";
                matches.Add(new StandardVoucherObservation(username, state));
            }

            return new StandardVoucherVerificationResult("ready", service, matches);
        }
        catch (Exception ex) when (ex is RouterOsTrapException or IOException or
            InvalidOperationException or OperationCanceledException)
        {
            // Router read errors never masquerade as an empty account list.
            return Unavailable("unavailable", service);
        }
    }

    private static string Field(IReadOnlyDictionary<string, string> data, string key) =>
        data.TryGetValue(key, out string? value) ? value ?? "" : "";

    private static bool IsDisabled(string value) =>
        value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private static StandardVoucherVerificationResult Unavailable(string status, string service) =>
        new(status, service, []);
}
