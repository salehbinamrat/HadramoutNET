namespace HadramoutNET.Core.Licensing;

/// <summary>
/// A validated, already-existing protected trial record supplied by a trusted
/// storage reader. No new record can be created via this type.
/// </summary>
public sealed record TrialSnapshot(
    DateTimeOffset First,
    DateTimeOffset Last,
    string MachineId,
    int ActivationRevision = 0);

public sealed record TrialAccess(
    string Mode, string Reason, string Message, long RemainingSeconds,
    DateTimeOffset? TrialStarted = null, DateTimeOffset? TrialExpires = null)
{
    public bool Permitted => Mode is "ready" or "trial";
}

/// <summary>
/// Pure evaluation of the ORIGINAL 480-hour trial clock. Does not read or
/// write DPAPI, registry, disk or Supabase and cannot start/reset a trial.
/// A trusted legacy StateStore-compatible reader must precede this policy.
/// </summary>
public static class TrialAccessPolicy
{
    public const int TrialDays = 20;
    public const long TrialSeconds = 480L * 3600L;
    public static readonly TimeSpan TrialLength = TimeSpan.FromSeconds(TrialSeconds);
    private static readonly TimeSpan ClockGrace = TimeSpan.FromMinutes(5);

    public static TrialAccess Evaluate(
        TrialSnapshot? state, DateTimeOffset nowUtc,
        string expectedMachineId, bool clockChanged = false)
    {
        if (clockChanged)
            return new TrialAccess("clock_error", "clock_changed",
                "تغير وقت الجهاز أثناء التشغيل؛ لم تغير بداية التجربة.", 0);
        if (string.IsNullOrWhiteSpace(expectedMachineId))
            return Invalid();

        if (state is null)
            return new TrialAccess("ready", "not_started",
                "تبدأ التجربة المجانية بعد أول دخول ناجح إلى MikroTik فقط.", TrialSeconds);

        if (!string.Equals(expectedMachineId, state.MachineId, StringComparison.Ordinal) ||
            state.ActivationRevision < 0 ||
            state.First > state.Last ||
            state.Last > nowUtc.AddYears(100) ||
            state.First > nowUtc.AddYears(100))
            return Invalid();

        DateTimeOffset first = state.First.ToUniversalTime();
        DateTimeOffset last = state.Last.ToUniversalTime();
        DateTimeOffset now = nowUtc.ToUniversalTime();
        DateTimeOffset deadline;
        try { deadline = first + TrialLength; }
        catch (ArgumentOutOfRangeException) { return Invalid(); }

        if (now + ClockGrace < last)
            return new TrialAccess("clock_error", "clock_rollback",
                "تاريخ أو ساعة Windows رجعا للخلف.", 0, first, deadline);

        DateTimeOffset effectiveNow = now > last ? now : last;
        long remaining = Math.Max(0, (long)Math.Ceiling((deadline - effectiveNow).TotalSeconds));
        if (remaining > 0)
            return new TrialAccess("trial", "trial_valid", "التجربة المجانية متاحة.",
                remaining, first, deadline);
        return new TrialAccess("locked", "trial_expired",
            "انتهت التجربة بعد 20 يوما (480 ساعة).", 0, first, deadline);
    }

    private static TrialAccess Invalid() =>
        new("locked", "state_invalid", "سجل تجربة هذا الكمبيوتر غير صالح.", 0);
}
