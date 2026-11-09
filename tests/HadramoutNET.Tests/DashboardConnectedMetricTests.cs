using HadramoutNET.Core.Visual;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class DashboardConnectedMetricTests
{
    [Theory]
    [InlineData(6, 4, "10", false)]
    [InlineData(0, 0, "0", false)]
    [InlineData(6, null, "≥6", true)]
    [InlineData(null, 4, "≥4", true)]
    [InlineData(null, null, "—", true)]
    public void Connected_clients_are_NAS_sessions_not_Radius_and_partial_is_never_exact(
        int? hotspot, int? ppp, string expected, bool partial)
    {
        var result = DashboardConnectedMetric.FromSources(hotspot, ppp);
        Assert.Equal(expected, result.Display);
        Assert.Equal(partial, result.Partial);
    }
}
