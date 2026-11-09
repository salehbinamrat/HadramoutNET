using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RouterCardServerSelectionTests
{
    [Fact]
    public void Existing_explicit_hotspot_server_survives_live_list_refresh()
    {
        Assert.Equal("Hotspot-A",
            RouterCardServerSelection.Retain("Hotspot-A", new[] { "Hotspot-A", "Hotspot-B" }));
    }

    [Fact]
    public void Missing_saved_server_never_silently_expands_to_all()
    {
        Assert.Null(RouterCardServerSelection.Retain("Removed-Server",
            new[] { "Hotspot-A", "Hotspot-B" }));
        Assert.Null(RouterCardServerSelection.Retain("", new[] { "Hotspot-A" }));
        Assert.Null(RouterCardServerSelection.Retain(null, new[] { "Hotspot-A" }));
    }

    [Fact]
    public void Only_explicit_all_option_remains_all_after_refresh()
    {
        Assert.Equal("all", RouterCardServerSelection.Retain("all", Array.Empty<string>()));
        Assert.Null(RouterCardServerSelection.Retain("ALL", new[] { "Hotspot-A" }));
    }
}
