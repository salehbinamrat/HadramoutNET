using HadramoutNET.Core.Licensing;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>Pure trial decision tests: not a writer or a new activation-state store.</summary>
public sealed class TrialAccessPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Machine = "FIXTURE-MACHINE";

    [Fact]
    public void No_history_is_ready_but_not_a_newly_started_trial()
    {
        var result = TrialAccessPolicy.Evaluate(null, Start, Machine);
        Assert.Equal("ready", result.Mode);
        Assert.Equal("not_started", result.Reason);
        Assert.Equal(480L * 3600L, result.RemainingSeconds);
        Assert.Null(result.TrialStarted);
    }

    [Fact]
    public void Existing_trial_is_exactly_480_hours_and_never_extended_by_renewal_revision()
    {
        var state = new TrialSnapshot(Start, Start.AddHours(10), Machine, ActivationRevision: 4);
        var beforeEnd = TrialAccessPolicy.Evaluate(state, Start.AddHours(480).AddSeconds(-1), Machine);
        Assert.Equal("trial", beforeEnd.Mode);
        Assert.Equal(1, beforeEnd.RemainingSeconds);
        Assert.Equal(Start, beforeEnd.TrialStarted);
        Assert.Equal(Start.AddHours(480), beforeEnd.TrialExpires);
        var expired = TrialAccessPolicy.Evaluate(state, Start.AddHours(480), Machine);
        Assert.Equal("trial_expired", expired.Reason);
        Assert.False(expired.Permitted);
        Assert.Equal(0, expired.RemainingSeconds);
        Assert.Equal("trial_expired", TrialAccessPolicy.Evaluate(
            state with { ActivationRevision = 21 }, Start.AddHours(482), Machine).Reason);
    }

    [Fact]
    public void Trial_clock_rollback_and_tampered_machine_fail_closed()
    {
        var state = new TrialSnapshot(Start, Start.AddHours(15), Machine);
        Assert.Equal("clock_rollback", TrialAccessPolicy.Evaluate(
            state, Start.AddHours(15).AddMinutes(-6), Machine).Reason);
        Assert.Equal("state_invalid", TrialAccessPolicy.Evaluate(
            state, Start.AddHours(16), "ANOTHER-MACHINE").Reason);
        Assert.Equal("state_invalid", TrialAccessPolicy.Evaluate(
            state with { First = Start.AddHours(18) }, Start.AddHours(20), Machine).Reason);
    }

    [Fact]
    public void Last_verified_clock_never_decreases_and_in_process_shift_is_rejected()
    {
        var state = new TrialSnapshot(Start, Start.AddHours(13), Machine);
        var nearRollback = TrialAccessPolicy.Evaluate(
            state, Start.AddHours(13).AddMinutes(-4), Machine);
        Assert.Equal("trial", nearRollback.Mode);
        Assert.Equal(467L * 3600L, nearRollback.RemainingSeconds);
        Assert.Equal("clock_changed", TrialAccessPolicy.Evaluate(
            state, Start.AddHours(20), Machine, clockChanged: true).Reason);
        Assert.Equal("state_invalid", TrialAccessPolicy.Evaluate(
            state with { Last = Start.AddYears(200) }, Start.AddDays(1), Machine).Reason);
    }
}
