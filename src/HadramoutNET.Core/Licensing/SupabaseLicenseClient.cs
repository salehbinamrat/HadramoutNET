using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HadramoutNET.Core.Licensing;

/// <summary>
/// Network-only transport for the original signed Supabase license service.
/// The first check is always read-only. First-device claim requires an explicit
/// separate call AND a freshly verified signed pending record. The client
/// never treats HTTP success alone as a paid entitlement.
/// </summary>
public sealed class SupabaseLicenseClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<byte[], string, string, DateTimeOffset, LicenseAccess> _verifier;
    private bool _disposed;

    public SupabaseLicenseClient()
        : this(new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        }, SignedLicenseFeed.Verify)
    {
    }

    // Dedicated test seam, accessible only to the test assembly.
    internal SupabaseLicenseClient(
        HttpClient http,
        Func<byte[], string, string, DateTimeOffset, LicenseAccess> verifier)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    public async Task<LicenseAccess> CheckAsync(
        string? code, string? deviceId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized = SignedLicenseFeed.NormalizeCode(code);
        if (normalized.Length != 32 || !IsDeviceCode(deviceId))
            return Denied("input_invalid", "رمز الترخيص أو بصمة الجهاز غير صالح.");
        try
        {
            byte[] content = await FetchFeedAsync(cancellationToken).ConfigureAwait(false);
            return _verifier(content, normalized, deviceId!, nowUtc);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or IOException or OperationCanceledException
                or InvalidOperationException or JsonException)
        {
            return Denied("network_error",
                "تعذر الاتصال بخدمة الترخيص؛ تأكد من الإنترنت وأعد المحاولة.");
        }
    }

    /// <summary>
    /// Called only after explicit activation action by the user. A signed
    /// pending feed record must be seen before sending the hashed device ID.
    /// A 200/202 claim response is NOT permission to use the software.
    /// A subsequent signed active feed must still be verified.
    /// </summary>
    public async Task<LicenseAccess> ClaimPendingAsync(
        string? code, string? deviceId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var status = await CheckAsync(code, deviceId, nowUtc, cancellationToken)
            .ConfigureAwait(false);
        if (status.Reason != "pending_activation")
            return status;

        string normalized = SignedLicenseFeed.NormalizeCode(code);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(SignedLicenseFeed.NetworkTimeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Post, SignedLicenseFeed.ActivationUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("Hadramout-Cards-License/1");
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { license_id = normalized, device_code = deviceId }),
                Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
                throw new HttpRequestException("Activation claim rejected.");

            return Denied("claim_pending",
                "تم إرسال بصمة الجهاز؛ يلزم تحقق جديد من السجل الموقع قبل السماح بالدخول.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or IOException or OperationCanceledException
                or InvalidOperationException)
        {
            return Denied("claim_unavailable",
                "تعذر إرسال طلب ربط الجهاز؛ لا يوجد تصريح استخدام حتى نجاح التحقق.");
        }
    }

    private async Task<byte[]> FetchFeedAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(SignedLicenseFeed.NetworkTimeoutSeconds));

        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, SignedLicenseFeed.FeedUrl + "?check=" + nonce);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        request.Headers.UserAgent.ParseAdd("Hadramout-Cards-License/1");

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        // Reject redirects instead of forwarding tokens or trusting
        // unexpected feed hosts. No other HTTP 2xx success is acceptable.
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("Unexpected license feed status.");
        if (response.Content.Headers.ContentLength > SignedLicenseFeed.MaxFeedBytes)
            throw new IOException("License feed exceeds the maximum length.");

        using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token)
            .ConfigureAwait(false);
        using var result = new MemoryStream();
        byte[] block = new byte[8192];
        while (true)
        {
            int length = await stream.ReadAsync(block.AsMemory(), timeout.Token)
                .ConfigureAwait(false);
            if (length == 0) break;
            if (result.Length + length > SignedLicenseFeed.MaxFeedBytes)
                throw new IOException("License feed exceeds the maximum length.");
            result.Write(block, 0, length);
        }
        return result.ToArray();
    }

    private static LicenseAccess Denied(string reason, string message) =>
        new("locked", message, reason);

    private static bool IsDeviceCode(string? id) =>
        id is not null && id.Length == 64 &&
        id.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
