using System.Security.Cryptography;

namespace HadramoutNET.Core.Licensing;

/// <summary>
/// Signed-paid-license workflow for future WPF wiring. Merely constructing
/// it never accesses the network, writes files, starts a trial or issues a claim.
/// Access must be reverified after restart and checked against a monotonic clock.
/// </summary>
public sealed class PaidLicenseCoordinator
{
    private readonly PaidCodeStore _store;
    private readonly SupabaseLicenseClient _remote;
    private readonly Func<string> _deviceCode;
    private readonly PaidLicenseSession _session = new();
    private readonly SemaphoreSlim _operations = new(1, 1);

    public PaidLicenseCoordinator(PaidCodeStore store, SupabaseLicenseClient remote)
        : this(store, remote, () => WindowsLicenseIdentity.ReadCurrent().PaidDeviceCode) { }

    // Only tests may inject an identity. Production always reads Windows HKLM.
    internal PaidLicenseCoordinator(PaidCodeStore store, SupabaseLicenseClient remote,
        Func<string> deviceCode)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _deviceCode = deviceCode ?? throw new ArgumentNullException(nameof(deviceCode));
    }

    public LicenseAccess StatusAt(TimeSpan processUptime) => _session.StatusAt(processUptime);

    public bool NeedsCheck(TimeSpan processUptime)
    {
        PaidCodeReadResult stored = _store.Read();
        return _session.NeedsCheck(stored.HasCode, processUptime);
    }

    /// <summary>
    /// Checks an existing encrypted or original read-only Supabase code.
    /// Even a pending signed license NEVER causes automatic device claiming.
    /// </summary>
    public async Task<LicenseAccess> CheckStoredAsync(
        DateTimeOffset nowUtc, TimeSpan processUptime,
        CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PaidCodeReadResult saved;
            try { saved = _store.Read(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or CryptographicException or InvalidOperationException)
            {
                saved = new PaidCodeReadResult("invalid");
            }

            if (!saved.HasCode)
                return Record(new LicenseAccess("locked",
                    saved.Status == "not_found" ? "أدخل رمز الترخيص للتفعيل." :
                        "تعذر قراءة رمز الترخيص المحمي.",
                    saved.Status == "not_found" ? "missing_code" : "storage_invalid"),
                    processUptime);

            string device;
            try { device = _deviceCode(); }
            catch (Exception ex) when (ex is InvalidOperationException or
                PlatformNotSupportedException or UnauthorizedAccessException or
                System.Security.SecurityException)
            {
                return Record(new LicenseAccess("locked",
                    "تعذر التعرف على بصمة Windows؛ لم يتم إنشاء بصمة بديلة.",
                    "device_unavailable"), processUptime);
            }
            LicenseAccess online = await _remote.CheckAsync(
                saved.Code, device, nowUtc, cancellationToken).ConfigureAwait(false);
            return Record(online, processUptime);
        }
        finally
        {
            _operations.Release();
        }
    }

    /// <summary>
    /// Only an explicit activation interaction calls this method.
    /// The network client sends a device claim ONLY after independently
    /// verifying an Ed25519-signed pending record. A successful POST alone
    /// never activates the application.
    /// </summary>
    public async Task<LicenseAccess> ActivateEnteredCodeAsync(
        string? enteredCode, DateTimeOffset nowUtc, TimeSpan processUptime,
        CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string normalized = SignedLicenseFeed.NormalizeCode(enteredCode);
            if (normalized.Length != 32)
                return Record(new LicenseAccess("locked",
                    "رمز الترخيص غير صالح.", "input_invalid"), processUptime);

            string device;
            try { device = _deviceCode(); }
            catch (Exception ex) when (ex is InvalidOperationException or
                PlatformNotSupportedException or UnauthorizedAccessException or
                System.Security.SecurityException)
            {
                return Record(new LicenseAccess("locked",
                    "تعذر التعرف على بصمة Windows.", "device_unavailable"), processUptime);
            }

            LicenseAccess access = await _remote.ClaimPendingAsync(
                normalized, device, nowUtc, cancellationToken).ConfigureAwait(false);

            // Match original Python's explicit user-entry persistence policy.
            // Candidate codes may be retained for retries, but never grant
            // rights without a freshly verified online_valid signature.
            if (access.Permitted || access.Reason is "claim_pending" or
                "not_found" or "claim_unavailable")
            {
                try { _store.SaveEnteredCode(normalized); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or CryptographicException or InvalidOperationException)
                {
                    return Record(new LicenseAccess("locked",
                        "فشل حفظ رمز الترخيص بصورة آمنة.", "storage_error"), processUptime);
                }
            }
            return Record(access, processUptime);
        }
        finally
        {
            _operations.Release();
        }
    }

    private LicenseAccess Record(LicenseAccess access, TimeSpan uptime)
    {
        _session.RecordAttempt(access, uptime);
        return _session.StatusAt(uptime);
    }
}
