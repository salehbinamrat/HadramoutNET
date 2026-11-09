using HadramoutNET.Core.Visual;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class WorkspaceSidebarCatalogTests
{
    [Fact]
    public void Ten_original_Python_sidebar_routes_preserve_order_icons_and_destinations()
    {
        var routes = WorkspaceSidebarCatalog.Items;
        Assert.Equal(new[]
        {
            "dashboard", "packages", "create", "manage", "active_cards",
            "expired_cards", "activity", "maintenance", "monitor", "network"
        }, routes.Select(route => route.Key));
        Assert.Equal(new[]
        {
            "الرئيسية", "باقات الشبكة", "إنشاء وطباعة الكروت",
            "إدارة المستخدمين", "الكروت النشطة", "الكروت المنتهية",
            "النشاط", "التحكم بالشبكة والصيانة",
            "المتصلون والهوتسبوت", "الشبكة والتشخيص"
        }, routes.Select(route => route.Label));
        Assert.All(routes, route =>
        {
            Assert.EndsWith(".png", route.Icon);
            Assert.NotEmpty(route.Label);
            Assert.True(route.PrimaryTabIndex is >= 0 and < 8 ||
                        route.Key is "active_cards" or "expired_cards");
        });
        Assert.Equal(10, routes.Select(route => route.Key).Distinct().Count());
        Assert.Equal(4, Assert.Single(routes, r => r.Key == "activity").NetworkTabIndex);
        Assert.Equal(3, Assert.Single(routes, r => r.Key == "monitor").NetworkTabIndex);
        Assert.Equal(0, Assert.Single(routes, r => r.Key == "maintenance").NetworkTabIndex);
    }

    [Fact]
    public void Active_and_expired_are_modal_views_not_fake_top_level_pages()
    {
        var active = WorkspaceSidebarCatalog.Find("active_cards");
        var expired = WorkspaceSidebarCatalog.Find("expired_cards");
        Assert.Equal(-1, active.PrimaryTabIndex);
        Assert.Equal(-1, expired.PrimaryTabIndex);
        Assert.Equal("active", active.LifecycleStatus);
        Assert.Equal("expired", expired.LifecycleStatus);
        Assert.Throws<ArgumentException>(() => WorkspaceSidebarCatalog.Find("missing-route"));
    }
}
