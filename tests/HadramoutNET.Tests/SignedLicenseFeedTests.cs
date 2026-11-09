using System.Security.Cryptography;
using System.Text;
using HadramoutNET.Core.Licensing;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class SignedLicenseFeedTests
{
    private const string Code = "ABCDEF0123456789ABCDEF0123456789";
    private const string Device = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly Ed25519PrivateKeyParameters PrivateKey =
        new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), 0);
    private static byte[] PublicKey => PrivateKey.GeneratePublicKey().GetEncoded();
    private static string HashedCode => Convert.ToHexString(
        SHA256.HashData(Encoding.ASCII.GetBytes(Code)));

    private static string SignPayload(string payload)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, PrivateKey);
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        string signature = Convert.ToBase64String(signer.GenerateSignature());
        return payload[..^1] + ",\"signature\":\"" + signature + "\"}";
    }

    private static string Payload(string record, DateTimeOffset? generated = null)
    {
        // Explicit sorted fields: this is the Python json.dumps canonical form.
        return "{\"generated_at\":\"" + (generated ?? Now).ToString("yyyy-MM-ddTHH:mm:ss+00:00") +
            "\",\"licenses\":{\"" + HashedCode + "\":" + record +
            "},\"product\":\"NIDI-MIKROTIK-CARDS\",\"revision\":7,\"schema\":1}";
    }

    private static string Record(string device = Device, string status = "active",
        string? expiry = "2026-11-11") =>
        "{\"device\":\"" + device + "\",\"expires\":" +
        (expiry is null ? "null" : "\"" + expiry + "\"") +
        ",\"status\":\"" + status + "\"}";

    private static LicenseAccess Check(string signed) =>
        SignedLicenseFeed.VerifyWithKey(Encoding.UTF8.GetBytes(signed), Code, Device, Now, PublicKey);

    [Fact]
    public void Pinned_contract_matches_original_Supabase_and_public_key_only()
    {
        Assert.Equal("NIDI-MIKROTIK-CARDS", SignedLicenseFeed.Product);
        Assert.Equal("https://zlirqppwemfmzxkholcr.supabase.co/functions/v1/hadramout-license/licenses.json",
            SignedLicenseFeed.FeedUrl);
        Assert.Equal("https://zlirqppwemfmzxkholcr.supabase.co/functions/v1/hadramout-license/v1/claim",
            SignedLicenseFeed.ActivationUrl);
        Assert.Equal("yifdpnKvdmxoEGUOoQGqeytEaume9Ldp7sKPbpQuGBE=",
            SignedLicenseFeed.TrustedPublicKeyBase64);
        Assert.Equal(1_048_576, SignedLicenseFeed.MaxFeedBytes);
    }

    [Fact]
    public void Canonical_json_matches_python_sorted_compact_utf8_including_Arabic()
    {
        var actual = SignedLicenseFeed.CanonicalizeForTests(
            Encoding.UTF8.GetBytes("{\"z\":\"مرحبا\",\"a\":{\"y\":1,\"b\":\"شبكة\"},\"flag\":true}"));
        Assert.Equal("{\"a\":{\"b\":\"شبكة\",\"y\":1},\"flag\":true,\"z\":\"مرحبا\"}",
            Encoding.UTF8.GetString(actual));
    }

    [Fact]
    public void Signed_active_license_grants_exact_device_only_until_expiry_day()
    {
        string signed = SignPayload(Payload(Record()));
        var access = Check(signed);
        Assert.True(access.Permitted);
        Assert.Equal("online_valid", access.Reason);
        Assert.Equal(Code, access.LicenseId);
        Assert.Equal("2026-11-11", access.Expires);
        Assert.Equal(3L * 86400, access.RemainingSeconds);
        Assert.Equal("wrong_device", SignedLicenseFeed.VerifyWithKey(
            Encoding.UTF8.GetBytes(signed), Code, new string('A', 64), Now, PublicKey).Reason);
        Assert.Equal("expired", SignedLicenseFeed.VerifyWithKey(
            Encoding.UTF8.GetBytes(signed), Code, Device, Now.AddDays(3), PublicKey).Reason);
        Assert.True(SignedLicenseFeed.VerifyWithKey(
            Encoding.UTF8.GetBytes(signed), Code, Device, Now.AddDays(2), PublicKey).Permitted);
    }

    [Fact]
    public void Pending_revoked_and_non_matching_license_are_never_grants()
    {
        string pending = SignPayload(Payload("{\"device\":null,\"expires\":null,\"status\":\"pending\"}"));
        Assert.Equal("pending_activation", Check(pending).Reason);
        Assert.False(Check(pending).Permitted);
        Assert.Equal("revoked", Check(SignPayload(Payload(Record(status: "suspended")))).Reason);
        Assert.Equal("not_found", Check(SignPayload(
            "{\"generated_at\":\"2026-11-09T10:00:00+00:00\",\"licenses\":{},\"product\":\"NIDI-MIKROTIK-CARDS\",\"revision\":7,\"schema\":1}")).Reason);
        Assert.True(Check(SignPayload(Payload(Record(expiry: null)))).Permitted);
    }

    [Fact]
    public void Rejects_tampering_invalid_schema_future_time_duplicates_and_oversize()
    {
        string valid = SignPayload(Payload(Record()));
        Assert.Equal("feed_invalid", Check(valid.Replace("\"status\":\"active\"", "\"status\":\"revoked\"")).Reason);
        Assert.Equal("feed_invalid", Check(SignPayload(Payload(Record()).Replace("\"schema\":1", "\"schema\":2"))).Reason);
        Assert.Equal("feed_invalid", Check(SignPayload(Payload(Record(), Now.AddMinutes(6)))).Reason);
        Assert.Equal("feed_invalid", Check(SignPayload(
            Payload("{\"device\":\"" + Device + "\",\"device\":\"" + Device +
                "\",\"expires\":null,\"status\":\"active\"}"))).Reason);
        Assert.Equal("feed_invalid", Check(new string('A', SignedLicenseFeed.MaxFeedBytes + 1)).Reason);
        Assert.Equal("input_invalid", SignedLicenseFeed.VerifyWithKey(
            Encoding.UTF8.GetBytes(valid), "invalid", Device, Now, PublicKey).Reason);
        Assert.Equal("input_invalid", SignedLicenseFeed.VerifyWithKey(
            Encoding.UTF8.GetBytes(valid), Code, "wrong", Now, PublicKey).Reason);
    }

    [Fact]
    public void Existing_windows_identifier_hash_and_code_normalization_match_original()
    {
        string identifier = "01234567-abcd-test";
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "HADRAMOUT-GITHUB-LICENSE-V1|" + identifier)));
        Assert.Equal(expected, SignedLicenseFeed.HashWindowsIdentifier(identifier.ToUpperInvariant()));
        Assert.Equal(Code, SignedLicenseFeed.NormalizeCode("abcdef01-2345 6789 ABCDEF0123456789"));
        Assert.Equal("", SignedLicenseFeed.NormalizeCode("hello-not-hex"));
    }
}
