using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HadramoutNET.Core.Licensing;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class SupabaseLicenseClientTests
{
    private const string LicenseId = "0123456789ABCDEF0123456789ABCDEF";
    private const string DeviceId = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly Ed25519PrivateKeyParameters TestSeed =
        new(Enumerable.Range(10, 32).Select(i => (byte)i).ToArray(), 0);
    private static byte[] TestPublicKey => TestSeed.GeneratePublicKey().GetEncoded();

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken token) => responder(request, token);
    }

    private static string Feed(string status, string? device, bool signedCorrectly = true)
    {
        string publicId = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(LicenseId)));
        string d = device is null ? "null" : "\"" + device + "\"";
        string payload = "{\"generated_at\":\"2026-11-09T10:00:00+00:00\"," +
            "\"licenses\":{\"" + publicId + "\":{\"device\":" + d +
            ",\"expires\":null,\"status\":\"" + status + "\"}}," +
            "\"product\":\"NIDI-MIKROTIK-CARDS\",\"revision\":1,\"schema\":1}";
        byte[] raw = Encoding.UTF8.GetBytes(payload);
        var signer = new Ed25519Signer();
        signer.Init(true, TestSeed);
        signer.BlockUpdate(raw, 0, raw.Length);
        string signature = Convert.ToBase64String(signer.GenerateSignature());
        string result = payload[..^1] + ",\"signature\":\"" + signature + "\"}";
        return signedCorrectly ? result : result.Replace("\"schema\":1", "\"schema\":2");
    }

    private static SupabaseLicenseClient Client(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var http = new HttpClient(new StubHandler(handler));
        return new SupabaseLicenseClient(http,
            (feed, code, device, now) =>
                SignedLicenseFeed.VerifyWithKey(feed, code, device, now, TestPublicKey));
    }

    [Fact]
    public async Task Check_is_read_only_uses_exact_HTTPS_feed_and_never_sends_credentials()
    {
        int get = 0, post = 0;
        using var client = Client((request, _) =>
        {
            if (request.Method == HttpMethod.Post) post++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https", request.RequestUri?.Scheme);
            Assert.Equal(new Uri(SignedLicenseFeed.FeedUrl).AbsolutePath, request.RequestUri?.AbsolutePath);
            Assert.Equal(new Uri(SignedLicenseFeed.FeedUrl).Host, request.RequestUri?.Host);
            Assert.StartsWith("?check=", request.RequestUri?.Query);
            Assert.DoesNotContain(LicenseId, request.RequestUri!.ToString());
            get++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Feed("active", DeviceId), Encoding.UTF8, "application/json")
            });
        });
        var access = await client.CheckAsync(LicenseId, DeviceId, Now);
        Assert.True(access.Permitted);
        Assert.Equal("online_valid", access.Reason);
        Assert.Equal(1, get);
        Assert.Equal(0, post);
    }

    [Fact]
    public async Task Only_a_signed_pending_code_can_trigger_explicit_first_activation()
    {
        int get = 0, post = 0;
        using var client = Client(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                get++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Feed("pending", null), Encoding.UTF8, "application/json")
                };
            }
            post++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(SignedLicenseFeed.ActivationUrl, request.RequestUri!.ToString());
            string body = await request.Content!.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(2, json.RootElement.EnumerateObject().Count());
            Assert.Equal(LicenseId, json.RootElement.GetProperty("license_id").GetString());
            Assert.Equal(DeviceId, json.RootElement.GetProperty("device_code").GetString());
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var checkedOnly = await client.CheckAsync(LicenseId, DeviceId, Now);
        Assert.Equal("pending_activation", checkedOnly.Reason);
        Assert.Equal(0, post);
        var claimed = await client.ClaimPendingAsync(LicenseId, DeviceId, Now);
        Assert.Equal("claim_pending", claimed.Reason);
        Assert.False(claimed.Permitted);
        Assert.Equal(2, get);
        Assert.Equal(1, post);
    }

    [Theory]
    [InlineData("active", true)]
    [InlineData("revoked", false)]
    public async Task No_activation_request_for_active_or_revoked_codes(string status, bool active)
    {
        int posts = 0;
        using var client = Client((request, _) =>
        {
            if (request.Method == HttpMethod.Post) posts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Feed(status, DeviceId), Encoding.UTF8, "application/json")
            });
        });
        var result = await client.ClaimPendingAsync(LicenseId, DeviceId, Now);
        Assert.Equal(active, result.Permitted);
        Assert.Equal(0, posts);
    }

    [Fact]
    public async Task Invalid_signature_must_not_send_device_claim()
    {
        int posts = 0;
        using var client = Client((request, _) =>
        {
            if (request.Method == HttpMethod.Post) posts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Feed("pending", null, signedCorrectly: false))
            });
        });
        Assert.Equal("feed_invalid",
            (await client.ClaimPendingAsync(LicenseId, DeviceId, Now)).Reason);
        Assert.Equal(0, posts);
    }

    [Fact]
    public async Task Redirect_or_too_large_or_unavailable_feed_fails_closed()
    {
        foreach (var response in new Func<HttpResponseMessage>[]
        {
            () => new(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://outside.example/licenses.json") }
            },
            () => new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[SignedLicenseFeed.MaxFeedBytes + 1])
            },
            () => new(HttpStatusCode.ServiceUnavailable)
        })
        {
            using var client = Client((_, _) => Task.FromResult(response()));
            var result = await client.CheckAsync(LicenseId, DeviceId, Now);
            Assert.False(result.Permitted);
            Assert.Equal("network_error", result.Reason);
        }
    }

    [Fact]
    public async Task Rejects_bad_input_without_any_network_traffic()
    {
        int hits = 0;
        using var client = Client((_, _) =>
        {
            hits++;
            throw new InvalidOperationException("No network should happen.");
        });
        Assert.Equal("input_invalid", (await client.CheckAsync("bad", DeviceId, Now)).Reason);
        Assert.Equal("input_invalid", (await client.ClaimPendingAsync(LicenseId, "bad", Now)).Reason);
        Assert.Equal(0, hits);
    }
}
