using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class UserManagerLegacyProfileLimitsTests
{
    private static IReadOnlyDictionary<string, string> Row(params string[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            string[] fields = pair.Split('=', 2);
            map.Add(fields[0], fields[1]);
        }
        return map;
    }

    [Fact]
    public void V6_effective_limit_chooses_smallest_positive_of_profile_linked_limitations()
    {
        IReadOnlyDictionary<string, string>[] profiles =
        [
            Row(".id=*1", "name=weekly"),
            Row(".id=*2", "name=daily")
        ];
        IReadOnlyDictionary<string, string>[] limitations =
        [
            Row(".id=*A", "name=unlimited", "transfer-limit=0"),
            Row(".id=*B", "name=small", "transfer-limit=500MiB"),
            Row(".id=*C", "name=large", "transfer-limit=1GiB")
        ];
        IReadOnlyDictionary<string, string>[] links =
        [
            Row("profile=*1", "limitation=*A"),
            Row("profile=weekly", "limitation=small"),
            Row("profile=*1", "limitation=*C"),
            Row("profile=daily", "limitation=unlimited")
        ];
        var limits = UserManagerLegacyProfileLimits.Build(profiles,
            limitations, links);
        Assert.Equal(500L * 1024 * 1024, limits["weekly"]);
        Assert.Equal(limits["weekly"], limits["*1"]);
        Assert.Equal(0, limits["daily"]);

        IReadOnlyDictionary<string, string>[] users =
        [
            Row("username=usedUp", "actual-profile=weekly",
                "total-download-used=500MiB", "uptime-used=1h"),
            Row("username=notStarted", "actual-profile=weekly",
                "total-download-used=0"),
            Row("username=unlimited", "actual-profile=daily",
                "total-download-used=5GiB", "uptime-used=1h")
        ];
        var snapshot = UserManagerLifecycleClassifier.Classify(6, users, [],
            limits);
        Assert.Equal(1, snapshot.Expired);
        Assert.Equal("expired", snapshot.Rows[0].State);
        Assert.Equal("valid", snapshot.Rows[1].State);
        Assert.Equal("active", snapshot.Rows[2].State);
    }

    [Fact]
    public void Unknown_limit_links_and_zero_limits_are_not_expiry_proof()
    {
        var limits = UserManagerLegacyProfileLimits.Build(
            [Row(".id=*1", "name=weekly")],
            [Row(".id=*A", "name=quota", "transfer-limit=0")],
            [Row("profile=*1", "limitation=*A"),
             Row("profile=*1", "limitation=*DOES_NOT_EXIST")]);
        Assert.Equal(0, limits["weekly"]);
        Assert.Equal(0, limits["*1"]);
        var snapshot = UserManagerLifecycleClassifier.Classify(6,
            [Row("username=u", "actual-profile=weekly",
                "total-download-used=900000000", "uptime-used=1h")],
            [], limits);
        Assert.Equal(0, snapshot.Expired);
        Assert.Equal(1, snapshot.Active);
    }
}
