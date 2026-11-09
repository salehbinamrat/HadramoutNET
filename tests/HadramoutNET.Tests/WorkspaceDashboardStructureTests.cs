using System.Xml.Linq;
using HadramoutNET.Core.Visual;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>
/// Structural acceptance for the WPF shell. Actual pixel/print parity still
/// requires real Windows rendering and physical printer checks.
/// </summary>
public sealed class WorkspaceDashboardStructureTests
{
    private static DirectoryInfo RepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src",
                    "HadramoutNET.App", "WorkspaceWindow.xaml")))
                return current;
            current = current.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repository root for source-compatible WPF layout test.");
    }

    [Fact]
    public void Original_navigation_icons_exist_and_are_linked_as_WPF_resources()
    {
        string root = RepoRoot().FullName;
        foreach (var route in WorkspaceSidebarCatalog.Items)
        {
            Assert.True(File.Exists(Path.Combine(root, "reference",
                "python-v1.1.3", "assets", "dashboard_icons", route.Icon)),
                "Missing original PNG: " + route.Icon);
        }
        Assert.True(File.Exists(Path.Combine(root, "reference",
            "python-v1.1.3", "assets", "router_banner.jpg")));
        string project = File.ReadAllText(Path.Combine(root, "src",
            "HadramoutNET.App", "HadramoutNET.App.csproj"));
        Assert.Contains("Assets/DashboardIcons/%(Filename)%(Extension)", project);
        Assert.Contains("Assets/router_banner.jpg", project);
    }

    [Fact]
    public void Dashboard_has_source_metric_cards_and_six_bands_without_scroll()
    {
        string file = Path.Combine(RepoRoot().FullName, "src",
            "HadramoutNET.App", "WorkspaceWindow.xaml");
        var xaml = XDocument.Load(file);
        var ns = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var xns = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        XElement dashboard = Assert.Single(xaml.Descendants(ns + "TabItem"),
            e => (string?)e.Attribute("Header") == "الرئيسية");
        Assert.Empty(dashboard.Descendants(ns + "ScrollViewer"));
        string[] exact = [
            "DashboardTotalCardsValue", "DashboardConnectedValue",
            "DashboardActiveCardsValue", "DashboardExpiredCardsValue",
            "CpuValue", "RamValue", "DashboardRecentActivity",
            "DashboardRouterName", "DashboardRouterHost",
            "DashboardLifecycleNote", "DashboardLicenseText"
        ];
        foreach (string name in exact)
            Assert.Single(dashboard.Descendants(),
                e => (string?)e.Attribute(xns + "Name") == name);
        Assert.Single(xaml.Descendants(),
            e => (string?)e.Attribute(xns + "Name") == "SidebarNavigation");
        Assert.Single(xaml.Descendants(),
            e => (string?)e.Attribute(xns + "Name") == "SidebarConnectionLamp");
    }
}
