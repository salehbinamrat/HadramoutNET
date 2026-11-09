using HadramoutNET.Core.Licensing;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class PaidLicenseSessionTests
{
    private static LicenseAccess Active() =>
        new("active", "صالح", "online_valid", "ABCDEF0123456789ABCDEF0123456789");

    [Fact]
    public void Starts_locked_even_when_a_code_is_saved_and_checks_immediately()
    {
        var session = new PaidLicenseSession();
        Assert.Equal("not_checked", session.StatusAt(TimeSpan.Zero).Reason);
        Assert.False(session.StatusAt(TimeSpan.Zero).Permitted);
        Assert.True(session.NeedsCheck(hasCode: true, TimeSpan.Zero));
        Assert.False(session.NeedsCheck(hasCode: false, TimeSpan.Zero));
    }

    [Fact]
    public void Active_result_needs_recheck_at_30_minutes_and_locks_after_grace()
    {
        var session = new PaidLicenseSession();
        session.RecordAttempt(Active(), TimeSpan.Zero);
        Assert.True(session.StatusAt(TimeSpan.FromSeconds(1800)).Permitted);
        Assert.False(session.NeedsCheck(true, TimeSpan.FromSeconds(1799)));
        Assert.True(session.NeedsCheck(true, TimeSpan.FromSeconds(1800)));
        Assert.True(session.StatusAt(TimeSpan.FromSeconds(1820)).Permitted);
        Assert.False(session.StatusAt(TimeSpan.FromSeconds(1821)).Permitted);
        Assert.Equal("check_overdue", session.StatusAt(TimeSpan.FromSeconds(1821)).Reason);
    }

    [Fact]
    public void Revocation_or_offline_failure_immediately_discards_previous_grant()
    {
        var session = new PaidLicenseSession();
        session.RecordAttempt(Active(), TimeSpan.Zero);
        session.RecordAttempt(new("locked", "ملغى", "revoked"), TimeSpan.FromSeconds(100));
        Assert.False(session.StatusAt(TimeSpan.FromSeconds(101)).Permitted);
        Assert.Equal("revoked", session.StatusAt(TimeSpan.FromSeconds(101)).Reason);
        Assert.False(session.NeedsCheck(true, TimeSpan.FromSeconds(159)));
        Assert.True(session.NeedsCheck(true, TimeSpan.FromSeconds(160)));
        session.RecordAttempt(Active(), TimeSpan.FromSeconds(160));
        Assert.True(session.StatusAt(TimeSpan.FromSeconds(161)).Permitted);
        session.RecordAttempt(new("locked", "تعذر الاتصال", "network_error"),
            TimeSpan.FromSeconds(200));
        Assert.False(session.StatusAt(TimeSpan.FromSeconds(201)).Permitted);
        Assert.Equal("network_error", session.StatusAt(TimeSpan.FromSeconds(201)).Reason);
    }

    [Fact]
    public void Rejects_forged_success_and_monotonic_rollback()
    {
        var session = new PaidLicenseSession();
        session.RecordAttempt(new("active", "غير موقع", "not_signed"), TimeSpan.FromSeconds(12));
        Assert.False(session.StatusAt(TimeSpan.FromSeconds(12)).Permitted);
        session.RecordAttempt(Active(), TimeSpan.FromSeconds(30));
        Assert.True(session.StatusAt(TimeSpan.FromSeconds(31)).Permitted);
        Assert.Equal("clock_invalid", session.StatusAt(TimeSpan.FromSeconds(20)).Reason);
        Assert.False(session.StatusAt(TimeSpan.FromSeconds(32)).Permitted);
        Assert.False(session.NeedsCheck(true, TimeSpan.FromSeconds(40)));
    }

    [Fact]
    public void Session_restart_never_restores_a_previous_entitlement()
    {
        var previous = new PaidLicenseSession();
        previous.RecordAttempt(Active(), TimeSpan.FromSeconds(1));
        Assert.True(previous.StatusAt(TimeSpan.FromSeconds(2)).Permitted);
        var restarted = new PaidLicenseSession();
        Assert.Equal("not_checked", restarted.StatusAt(TimeSpan.Zero).Reason);
        Assert.False(restarted.StatusAt(TimeSpan.Zero).Permitted);
    }
}
