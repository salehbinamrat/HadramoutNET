using System.Globalization;

namespace HadramoutNET.Core.Visual;

/// <summary>
/// The Python dashboard's combined online client count is Hotspot + PPPoE
/// NAS sessions. User Manager RADIUS sessions must NOT be added again.
/// An incomplete source is a lower bound, not a false exact total.
/// </summary>
public sealed record DashboardConnectedMetric(string Display, bool Partial)
{
    public static DashboardConnectedMetric FromSources(int? hotspot, int? ppp)
    {
        if (hotspot is null && ppp is null)
            return new DashboardConnectedMetric("—", Partial: true);
        long known = Math.Max(0, hotspot ?? 0) + (long)Math.Max(0, ppp ?? 0);
        bool partial = hotspot is null || ppp is null;
        string count = known.ToString("N0", CultureInfo.InvariantCulture);
        return new DashboardConnectedMetric(partial ? "≥" + count : count, partial);
    }
}
