using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.Visual;
using Xunit;
namespace HadramoutNET.Tests;

public sealed class DashboardAccessPresentationTests
{
    private static LicenseAccess InactivePaid() => new("locked", "غير مفعل", "not_checked");
    private static TrialAccess Trial(long remaining) => new("trial", "trial_valid", "متاحة", remaining);

    [Fact]
    public void Paid_entitlement_takes_priority_over_trial()
    {
        var result = DashboardAccessPresentation.From(
            new("active", "valid", "online_valid", RemainingSeconds: 176400),
            Trial(500), true);
        Assert.True(result.Authorized);
        Assert.Contains("الترخيص المدفوع", result.Text);
        Assert.Contains("2 يوم", result.Text);
        Assert.DoesNotContain("التجربة المجانية", result.Text);
    }
    [Fact]
    public void Trial_displays_remaining_in_ASCII_digits()
    {
        var result = DashboardAccessPresentation.From(InactivePaid(),
            Trial(86400 + 7200 + 300), true);
        Assert.True(result.Authorized);
        Assert.Contains("التجربة المجانية", result.Text);
        Assert.Contains("1 يوم، 2 ساعة، 5 دقيقة", result.Text);
    }
    [Fact]
    public void Disconnected_session_is_never_labeled_authorized()
    {
        var result = DashboardAccessPresentation.From(
            new("active", "valid", "online_valid"), Trial(500), false);
        Assert.False(result.Authorized);
        Assert.Contains("غير مصرح", result.Text);
    }
    [Fact]
    public void Unverified_paid_result_does_not_mask_expired_trial()
    {
        var result = DashboardAccessPresentation.From(
            new("active", "forged", "not_checked"),
            new("locked", "trial_expired", "منتهية", 0), true);
        Assert.False(result.Authorized);
        Assert.DoesNotContain("الترخيص المدفوع: مفعل", result.Text);
    }
}
