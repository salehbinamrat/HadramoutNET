namespace HadramoutNET.Core.Licensing;

/// <summary>
/// Per-process paid entitlement gate. A saved code is never a permission to
/// enter: only a current, signed "online_valid" result can be accepted.
/// Caller must provide non-decreasing Stopwatch elapsed time, never wall time.
/// No authorization is persisted across application restarts.
/// </summary>
public sealed class PaidLicenseSession
{
    private static readonly TimeSpan CheckEvery =
        TimeSpan.FromSeconds(SignedLicenseFeed.CheckIntervalSeconds);
    private static readonly TimeSpan CheckGrace = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(60);

    private LicenseAccess _last = new("locked",
        "يلزم التحقق من الترخيص عبر الإنترنت.", "not_checked");
    private TimeSpan? _lastAttempt;
    private TimeSpan? _lastSuccess;
    private TimeSpan? _lastObserved;
    private bool _clockFault;
    private readonly object _sync = new();

    public LicenseAccess StatusAt(TimeSpan uptime)
    {
        lock (_sync)
        {
            if (!Observe(uptime))
                return InvalidClock();
            if (_last.Permitted &&
                (!_lastSuccess.HasValue || uptime - _lastSuccess.Value > CheckEvery + CheckGrace))
                return new LicenseAccess("locked",
                    "انتهت مهلة التحقق الإلكتروني؛ أعد الاتصال بخدمة الترخيص.",
                    "check_overdue");
            return _last;
        }
    }

    public bool NeedsCheck(bool hasCode, TimeSpan uptime)
    {
        lock (_sync)
        {
            if (!Observe(uptime) || !hasCode)
                return false;
            if (!_lastAttempt.HasValue)
                return true;
            if (_last.Permitted)
                return !_lastSuccess.HasValue || uptime - _lastSuccess.Value >= CheckEvery;
            return uptime - _lastAttempt.Value >= RetryEvery;
        }
    }

    /// <summary>
    /// Call ONLY with a result obtained from SignedLicenseFeed via the actual
    /// Supabase client; never infer active status from HTTP 200 or saved codes.
    /// </summary>
    internal void RecordAttempt(LicenseAccess result, TimeSpan uptime)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_sync)
        {
            if (!Observe(uptime))
                return;
            _lastAttempt = uptime;
            if (result.Permitted)
            {
                if (result.Reason != "online_valid" ||
                    SignedLicenseFeed.NormalizeCode(result.LicenseId) != result.LicenseId)
                {
                    _last = new LicenseAccess("locked",
                        "نتيجة التحقق الإلكتروني غير موثوقة.", "invalid_grant");
                    return;
                }
                _lastSuccess = uptime;
            }
            _last = result;
        }
    }

    private bool Observe(TimeSpan uptime)
    {
        if (_clockFault) return false;
        if (uptime < TimeSpan.Zero ||
            (_lastObserved.HasValue && uptime < _lastObserved.Value))
        {
            _clockFault = true;
            _last = InvalidClock();
            return false;
        }
        _lastObserved = uptime;
        return true;
    }

    private static LicenseAccess InvalidClock() =>
        new("locked", "تعذر التحقق من الزمن المتتابع للتطبيق؛ يلزم إعادة التشغيل.", "clock_invalid");
}
