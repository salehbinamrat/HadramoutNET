using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// Parsed endpoint only. The operator-supplied API port is authoritative:
/// WinBox links and URL ports must never silently select the RouterOS API port.
/// Parity contract: reference/python-v1.1.3/core/connection_address.py.
/// </summary>
public sealed record RouterEndpoint(string Host, int Port);

public static class RouterAddress
{
    private static readonly Regex DnsLabel = new(
        @"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static RouterEndpoint Normalize(string? address, string? apiPort, string mode = "local")
    {
        if (mode is not ("local" or "remote"))
            throw new ArgumentException("اختر نوع الاتصال: محلي أو عن بُعد.", nameof(mode));

        string host = address?.Trim() ?? "";
        if (host.Length is 0 or > 1024 || host.Any(c =>
            char.IsWhiteSpace(c) || char.IsControl(c) ||
            CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
            throw InvalidAddress();

        if (!int.TryParse(apiPort?.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            throw new ArgumentException(mode == "remote"
                ? "أدخل منفذ API الخارجي من مزود الرابط (1–65535)، وليس منفذ WinBox تلقائياً."
                : "منفذ API يجب أن يكون رقمًا من 1 إلى 65535.", nameof(apiPort));

        if (mode == "local")
        {
            if (host.StartsWith('[') || host.EndsWith(']'))
            {
                if (!(host.StartsWith('[') && host.EndsWith(']')) ||
                    !TryIpv6(host[1..^1], out string? ipv6))
                    throw InvalidAddress();
                return new RouterEndpoint(ipv6!, port);
            }

            if (host.IndexOfAny(['/','\\','?','#','@']) >= 0)
                throw InvalidAddress();

            if (host.Contains(':'))
            {
                if (!TryIpv6(host, out string? ipv6))
                    throw InvalidAddress();
                return new RouterEndpoint(ipv6!, port);
            }
            return new RouterEndpoint(host, port);
        }

        if (host.IndexOfAny(['\\','?','#','@','%']) >= 0)
            throw InvalidAddress();

        if (IPAddress.TryParse(host, out IPAddress? direct))
            return new RouterEndpoint(direct.ToString(), port);

        string uriString = host.Contains("://", StringComparison.Ordinal)
            ? host : "http://" + host;
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https" or "winbox") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            uri.AbsolutePath is not ("" or "/") ||
            uriString.TrimEnd('/').EndsWith(':') || uri.Host.Length == 0)
            throw InvalidAddress();

        // Unknown WinBox ports must not be treated as the externally forwarded API port.
        // Validate syntactically, then discard the port inside the pasted link.
        string authority = uriString[(uriString.IndexOf("://", StringComparison.Ordinal) + 3)..];
        authority = authority.Split('/')[0];
        if (authority.EndsWith(':') || !ValidUrlPort(authority))
            throw InvalidAddress();

        string normalizedHost = uri.Host.Trim('[', ']');
        if (IPAddress.TryParse(normalizedHost, out IPAddress? addressIp))
            return new RouterEndpoint(addressIp.ToString(), port);

        try
        {
            var idn = new IdnMapping();
            normalizedHost = idn.GetAscii(normalizedHost).ToLowerInvariant();
            string withoutDot = normalizedHost.TrimEnd('.');
            if (withoutDot.Length > 253 || withoutDot.Length == 0 ||
                withoutDot.Split('.').Any(label => !DnsLabel.IsMatch(label)))
                throw InvalidAddress();
        }
        catch (ArgumentException)
        {
            throw InvalidAddress();
        }
        return new RouterEndpoint(normalizedHost, port);
    }

    public static string EndpointText(string host, int port) =>
        host.Contains(':', StringComparison.Ordinal)
            ? $"[{host}]:{port.ToString(CultureInfo.InvariantCulture)}"
            : $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryIpv6(string raw, out string? normalized)
    {
        normalized = null;
        if (!IPAddress.TryParse(raw, out IPAddress? ip) ||
            ip.AddressFamily != AddressFamily.InterNetworkV6)
            return false;
        normalized = ip.ToString();
        return true;
    }

    private static bool ValidUrlPort(string authority)
    {
        if (authority.StartsWith('['))
        {
            int bracket = authority.IndexOf(']');
            if (bracket < 0) return false;
            if (authority.Length == bracket + 1) return true;
            return authority.Length > bracket + 2 &&
                authority[bracket + 1] == ':' &&
                ValidPort(authority[(bracket + 2)..]);
        }
        int colon = authority.LastIndexOf(':');
        return colon < 0 || ValidPort(authority[(colon + 1)..]);
    }

    private static bool ValidPort(string value) =>
        value.Length > 0 && value.All(c => c is >= '0' and <= '9') &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port) &&
        port is >= 1 and <= 65535;

    private static ArgumentException InvalidAddress() =>
        new("عنوان الراوتر أو الرابط غير صالح؛ استخدم عنوان IP أو نطاق صحيحا دون بيانات دخول أو مسار.");
}
