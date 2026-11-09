using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using HadramoutNET.Core.Licensing;
using Xunit;

namespace HadramoutNET.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsLicenseIdentityTests
{
    [Fact]
    public void Original_python_hash_prefixes_and_lowercase_machine_guid_are_preserved()
    {
        const string machineGuid = "D5C748D8-3E30-48AD-9B08-8A803F6D709A";
        string lower = machineGuid.ToLowerInvariant();
        string expectedPaid = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "HADRAMOUT-GITHUB-LICENSE-V1|" + lower)));
        string expectedTrial = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "NIDI-MIKROTIK-CARDS-TRIAL-V2|" + lower)));
        var hashes = WindowsLicenseIdentity.FromGuidForTests(machineGuid);
        Assert.Equal(expectedPaid, hashes.PaidDeviceCode);
        Assert.Equal(expectedTrial, hashes.TrialMachineId);
        Assert.Equal(64, hashes.PaidDeviceCode.Length);
        Assert.Equal(64, hashes.TrialMachineId.Length);
        Assert.NotEqual(hashes.PaidDeviceCode, hashes.TrialMachineId);
        Assert.Equal(hashes, WindowsLicenseIdentity.FromGuidForTests("  " + lower + "  "));
    }

    [Fact]
    public void MachineGuid_reads_original_registry_view_on_both_windows_architectures()
    {
        RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using RegistryKey? cryptography = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
        string? machineGuid = cryptography?.GetValue("MachineGuid") as string;
        Assert.False(string.IsNullOrWhiteSpace(machineGuid));
        var expected = WindowsLicenseIdentity.FromGuidForTests(machineGuid!);
        var actual = WindowsLicenseIdentity.ReadCurrent();
        Assert.Equal(expected.PaidDeviceCode, actual.PaidDeviceCode);
        Assert.Equal(expected.TrialMachineId, actual.TrialMachineId);
    }

    [Fact]
    public void Missing_identity_fails_closed_without_generating_a_random_device()
    {
        Assert.Throws<ArgumentException>(() => WindowsLicenseIdentity.FromGuidForTests(""));
        Assert.Throws<ArgumentException>(() => WindowsLicenseIdentity.FromGuidForTests("  "));
        Assert.Throws<ArgumentException>(() => WindowsLicenseIdentity.FromGuidForTests(":000000000000"));
    }
}
