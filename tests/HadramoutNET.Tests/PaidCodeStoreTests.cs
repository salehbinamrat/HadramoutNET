using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using HadramoutNET.Core.Licensing;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>All files and encrypted values in this suite are synthetic.</summary>
[SupportedOSPlatform("windows")]
public sealed class PaidCodeStoreTests
{
    private const string Code = "AABBCCDD00112233AABBCCDD00112233";
    private const string RenewedCode = "1234567890ABCDEF1234567890ABCDEF";

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "hnet-license-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Legacy_supabase_code_is_read_only_and_deprecated_github_code_is_ignored()
    {
        string root = NewRoot();
        try
        {
            var store = new PaidCodeStore(root);
            Directory.CreateDirectory(Path.GetDirectoryName(store.LegacyReadOnlyPath)!);
            File.WriteAllText(store.LegacyReadOnlyPath, "aabbccdd-0011 2233-aabbccdd00112233\n");
            string obsolete = Path.Combine(root, "NidiMikroTikCards", "licensing", "github-license-id.txt");
            File.WriteAllText(obsolete, RenewedCode);
            byte[] before = SHA256.HashData(File.ReadAllBytes(store.LegacyReadOnlyPath));
            PaidCodeReadResult read = store.Read();
            Assert.Equal("legacy_read_only", read.Status);
            Assert.True(read.HasCode);
            Assert.Equal(Code, read.Code);
            Assert.False(File.Exists(store.ProtectedPath));
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(store.LegacyReadOnlyPath)));
            File.Delete(store.LegacyReadOnlyPath);
            Assert.Equal("not_found", store.Read().Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Explicit_save_creates_separate_DPAPI_ciphertext_and_restart_reads_it()
    {
        string root = NewRoot();
        try
        {
            var store = new PaidCodeStore(root);
            Assert.Equal("not_found", store.Read().Status);
            store.SaveEnteredCode("aabbccdd-0011-2233-AABBCCDD00112233");
            Assert.True(File.Exists(store.ProtectedPath));
            byte[] ciphertext = File.ReadAllBytes(store.ProtectedPath);
            Assert.DoesNotContain(Code, Encoding.Latin1.GetString(ciphertext));
            Assert.Equal("stored", new PaidCodeStore(root).Read().Status);
            Assert.Equal(Code, new PaidCodeStore(root).Read().Code);
            Assert.Equal("not_found", new PaidCodeStore(Path.Combine(root, "other-user")).Read().Status);
            Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(store.ProtectedPath)!),
                file => Path.GetFileName(file).StartsWith(".license-", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Renewal_replaces_only_dotnet_paid_code_and_never_original_trial_history()
    {
        string root = NewRoot();
        try
        {
            var store = new PaidCodeStore(root);
            Directory.CreateDirectory(Path.GetDirectoryName(store.LegacyReadOnlyPath)!);
            File.WriteAllText(store.LegacyReadOnlyPath, Code);
            string trial = Path.Combine(root, "NidiMikroTikCards", "licensing", "activation-state.bin");
            File.WriteAllBytes(trial, [7, 4, 8, 1, 2]);
            byte[] originalLicense = SHA256.HashData(File.ReadAllBytes(store.LegacyReadOnlyPath));
            byte[] originalTrial = SHA256.HashData(File.ReadAllBytes(trial));

            store.SaveEnteredCode(Code);
            store.SaveEnteredCode(RenewedCode);
            Assert.Equal(RenewedCode, store.Read().Code);
            Assert.Equal(originalLicense, SHA256.HashData(File.ReadAllBytes(store.LegacyReadOnlyPath)));
            Assert.Equal(originalTrial, SHA256.HashData(File.ReadAllBytes(trial)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Invalid_new_ciphertext_must_not_fall_back_to_legacy_or_grant_access()
    {
        string root = NewRoot();
        try
        {
            var store = new PaidCodeStore(root);
            Directory.CreateDirectory(Path.GetDirectoryName(store.LegacyReadOnlyPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(store.ProtectedPath)!);
            File.WriteAllText(store.LegacyReadOnlyPath, Code);
            File.WriteAllBytes(store.ProtectedPath, [1, 2, 3, 4]);
            var read = store.Read();
            Assert.Equal("invalid", read.Status);
            Assert.False(read.HasCode);
            Assert.Equal("", read.Code);
            Assert.Equal("invalid", new PaidCodeStore(root).Read().Status);
            File.WriteAllBytes(store.ProtectedPath, new byte[PaidCodeStore.MaxProtectedBytes + 1]);
            Assert.Equal("invalid", store.Read().Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Invalid_legacy_file_does_not_silently_turn_into_new_entitlement()
    {
        string root = NewRoot();
        try
        {
            var store = new PaidCodeStore(root);
            Directory.CreateDirectory(Path.GetDirectoryName(store.LegacyReadOnlyPath)!);
            File.WriteAllText(store.LegacyReadOnlyPath, "INVALID-LICENSE-CODE");
            Assert.Equal("invalid", store.Read().Status);
            File.WriteAllBytes(store.LegacyReadOnlyPath, new byte[65]);
            Assert.Equal("invalid", store.Read().Status);
            Assert.False(File.Exists(store.ProtectedPath));
            Assert.Throws<ArgumentException>(() => store.SaveEnteredCode("not a code"));
            Assert.False(File.Exists(store.ProtectedPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_saved_code_is_never_a_signed_license_grant()
    {
        string root = NewRoot();
        try
        {
            var store = new PaidCodeStore(root);
            store.SaveEnteredCode(Code);
            Assert.True(store.Read().HasCode);
            Assert.False(SignedLicenseFeed.Verify(
                Array.Empty<byte>(), Code, new string('A', 64),
                new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero)).Permitted);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
