using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class UserManagerVoucherStatusPagerTests
{
    [Fact]
    public void Original_status_window_uses_pages_of_300_and_filters_by_user_or_profile()
    {
        var rows = Enumerable.Range(0, 1050)
            .Select(i => new UserManagerLifecycleRow(
                "card" + i.ToString("D5"), i % 2 == 0 ? "weekly" : "daily",
                i % 3 == 0 ? "expired" : "active", "استنفد", 500, 500))
            .ToArray();
        var pager = new UserManagerVoucherStatusPager(rows, "expired");
        Assert.Equal(350, pager.Count);
        Assert.Equal(2, pager.Pages);
        Assert.Equal(300, pager.Page(0).Count);
        Assert.Equal(50, pager.Page(1).Count);
        Assert.Empty(pager.Page(2));
        pager.Search = "card00009";
        Assert.Equal(1, pager.Count);
        Assert.Equal("card00009", Assert.Single(pager.Page(0)).Username);
        pager.Search = "weekly";
        Assert.All(pager.Page(0), x => Assert.Equal("weekly", x.Profile));
        Assert.True(pager.Count > 0);
    }

    [Fact]
    public void Status_pager_does_not_expand_unclassified_into_expired()
    {
        var rows = new[]
        {
            new UserManagerLifecycleRow("a", "daily", "active", "", 2, 10),
            new UserManagerLifecycleRow("b", "daily", "unclassified", "", 10, 0),
            new UserManagerLifecycleRow("c", "daily", "expired", "", 10, 10),
        };
        Assert.Equal("c", Assert.Single(
            new UserManagerVoucherStatusPager(rows, "expired").Page(0)).Username);
        Assert.Throws<ArgumentException>(() =>
            new UserManagerVoucherStatusPager(rows, "disabled"));
    }
}
