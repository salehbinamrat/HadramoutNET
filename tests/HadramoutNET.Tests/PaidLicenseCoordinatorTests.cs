using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using HadramoutNET.Core.Licensing;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace HadramoutNET.Tests;

[SupportedOSPlatform("windows")]
public sealed class PaidLicenseCoordinatorTests
{
    private const string Code = "1234567890ABCDEF1234567890ABCDEF";
    private const string Device = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly Ed25519PrivateKeyParameters TestKey =
        new(Enumerable.Range(22, 32).Select(i => (byte)i).ToArray(), 0);

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(handler(request));
    }

    private static string SignedFeed(string status, bool bound)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Code)));
        string device = bound ? "\"" + Device + "\"" : "null";
        string payload = "{\"generated_at\":\"2026-11-09T10:00:00+00:00\",\"licenses\":{\"" +
            hash + "\":{\"device\":" + device + ",\"expires\":null,\"status\":\"" +
            status + "\"}},\"product\":\"NIDI-MIKROTIK-CARDS\",\"revision\":1,\"schema\":1}";
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        var signer = new Ed25519Signer();
        signer.Init(true, TestKey);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return payload[..^1] + ",\"signature\":\"" +
            Convert.ToBase64String(signer.GenerateSignature()) + "\"}";
    }

    private static SupabaseLicenseClient Client(
        Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new ResponseHandler(respond)),
            (bytes, license, device, now) => SignedLicenseFeed.VerifyWithKey(
                bytes, license, device, now, TestKey.GeneratePublicKey().GetEncoded()));

    [Fact]
    public async Task No_automatic_activation_and_explicit_claim_needs_new_signed_active_feed()
    {
        string root = Path.Combine(Path.GetTempPath(), "hnet-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int gets = 0, posts = 0;
            using var client = Client(request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    posts++;
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
                }
                gets++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(posts == 0 ? SignedFeed("pending", false) :
                        SignedFeed("active", true), Encoding.UTF8, "application/json")
                };
            });
            var store = new PaidCodeStore(root);
            var runtime = new PaidLicenseCoordinator(store, client, () => Device);
            Assert.Equal("missing_code",
                (await runtime.CheckStoredAsync(Now, TimeSpan.Zero)).Reason);
            Assert.Equal(0, gets);
            Assert.Equal(0, posts);
            Directory.CreateDirectory(Path.GetDirectoryName(store.LegacyReadOnlyPath)!);
            File.WriteAllText(store.LegacyReadOnlyPath, Code);
            byte[] legacyHash = SHA256.HashData(File.ReadAllBytes(store.LegacyReadOnlyPath));

            var pending = await runtime.CheckStoredAsync(Now, TimeSpan.FromSeconds(1));
            Assert.Equal("pending_activation", pending.Reason);
            Assert.False(pending.Permitted);
            Assert.Equal(1, gets);
            Assert.Equal(0, posts);

            var claiming = await runtime.ActivateEnteredCodeAsync(Code, Now,
                TimeSpan.FromSeconds(2));
            Assert.Equal("claim_pending", claiming.Reason);
            Assert.False(claiming.Permitted);
            Assert.Equal(1, posts);
            Assert.True(store.Read().HasCode);
            Assert.Equal(legacyHash, SHA256.HashData(File.ReadAllBytes(store.LegacyReadOnlyPath)));

            var active = await runtime.CheckStoredAsync(Now, TimeSpan.FromSeconds(3));
            Assert.True(active.Permitted);
            Assert.Equal("online_valid", active.Reason);
            Assert.True(runtime.StatusAt(TimeSpan.FromSeconds(1803)).Permitted);
            Assert.Equal("check_overdue", runtime.StatusAt(TimeSpan.FromSeconds(1824)).Reason);
            Assert.Equal(1, posts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Failed_or_revoked_paid_checks_never_retain_previous_access()
    {
        string root = Path.Combine(Path.GetTempPath(), "hnet-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string status = "active";
            using var client = Client(_ => status == "offline" ?
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) :
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SignedFeed(status, true))
                });
            var store = new PaidCodeStore(root);
            store.SaveEnteredCode(Code);
            var runtime = new PaidLicenseCoordinator(store, client, () => Device);
            Assert.True((await runtime.CheckStoredAsync(Now, TimeSpan.Zero)).Permitted);
            status = "revoked";
            Assert.Equal("revoked",
                (await runtime.CheckStoredAsync(Now, TimeSpan.FromSeconds(10))).Reason);
            Assert.False(runtime.StatusAt(TimeSpan.FromSeconds(11)).Permitted);
            status = "offline";
            Assert.Equal("network_error",
                (await runtime.CheckStoredAsync(Now, TimeSpan.FromSeconds(20))).Reason);
            Assert.False(runtime.StatusAt(TimeSpan.FromSeconds(21)).Permitted);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Invalid_stored_ciphertext_never_causes_claim_or_fallback()
    {
        string root = Path.Combine(Path.GetTempPath(), "hnet-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int hits = 0;
            using var client = Client(_ =>
            {
                hits++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SignedFeed("active", true))
                };
            });
            var store = new PaidCodeStore(root);
            Directory.CreateDirectory(Path.GetDirectoryName(store.ProtectedPath)!);
            File.WriteAllBytes(store.ProtectedPath, [1, 2, 3, 4]);
            var runtime = new PaidLicenseCoordinator(store, client, () => Device);
            Assert.Equal("storage_invalid",
                (await runtime.CheckStoredAsync(Now, TimeSpan.Zero)).Reason);
            Assert.False(runtime.StatusAt(TimeSpan.Zero).Permitted);
            Assert.Equal(0, hits);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
