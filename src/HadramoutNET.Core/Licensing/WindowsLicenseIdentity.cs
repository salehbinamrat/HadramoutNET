using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HadramoutNET.Core.Licensing;

/// <summary>
/// Same Windows installation identity as the pinned Python 1.1.3 client.
/// Reads the 64-bit MachineGuid registry view from both x86 and x64 processes.
/// No identifier is generated, stored or transmitted by this class.
/// </summary>
public sealed record WindowsDeviceCodes(string PaidDeviceCode, string TrialMachineId);

public static class WindowsLicenseIdentity
{
    public static WindowsDeviceCodes ReadCurrent()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("يتطلب الترخيص جهاز Windows.");
        return ReadWindowsRegistry();
    }

    [SupportedOSPlatform("windows")]
    private static WindowsDeviceCodes ReadWindowsRegistry()
    {
        RegistryView view = Environment.Is64BitOperatingSystem ?
            RegistryView.Registry64 : RegistryView.Registry32;
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
        if (key is null || !key.GetValueNames().Contains("MachineGuid", StringComparer.Ordinal) ||
            key.GetValueKind("MachineGuid") != RegistryValueKind.String ||
            key.GetValue("MachineGuid") is not string guid)
            throw new InvalidOperationException("تعذر قراءة بصمة Windows؛ لم يُنشأ معرف بديل.");
        return FromGuidForTests(guid);
    }

    // Only tests can supply a synthetic MachineGuid. Production always reads HKLM.
    internal static WindowsDeviceCodes FromGuidForTests(string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid) ||
            guid.Trim().Equals(":000000000000", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("معرف Windows غير صالح.", nameof(guid));
        string normalized = guid.Trim().ToLowerInvariant();
        return new WindowsDeviceCodes(
            SignedLicenseFeed.HashWindowsIdentifier(normalized),
            LegacyTrialStateReader.HashTrialMachineIdentifier(normalized));
    }
}
