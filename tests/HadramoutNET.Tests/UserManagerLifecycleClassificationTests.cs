using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class UserManagerLifecycleClassificationTests
{
    private static IReadOnlyDictionary<string, string> Row(params string[] pairs)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in pairs)
        {
            var parts = pair.Split('=', 2);
            result.Add(parts[0], parts[1]);
        }
        return result;
    }

    [Fact]
    public void V7_lifecycle_counts_users_not_profile_assignments_and_only_transfer_exhaustion_expires()
    {
        var users = new[]
        {
            Row(".id=*1", "name=runningActive", "disabled=no"),
            Row(".id=*2", "name=ready", "disabled=no"),
            Row(".id=*3", "name=spent", "disabled=no", "transfer-limit=100",
                "total-download-used=70", "total-upload-used=30"),
            Row(".id=*4", "name=timeSpent", "disabled=no", "transfer-limit=0",
                "total-download-used=500"),
            Row(".id=*5", "name=off", "disabled=yes", "transfer-limit=100",
                "total-download-used=100"),
            Row(".id=*6", "name=noProfile", "disabled=no"),
        };
        var profiles = new[]
        {
            Row("user=runningActive", "state=running active"),
            Row("user=*1", "state=used"),
            Row("user=*2", "state=running"),
            Row("user=*3", "state=used"),
            Row("user=*4", "state=used"),
            Row("user=*5", "state=used")
        };
        var state = UserManagerLifecycleClassifier.Classify(7, users, profiles);
        Assert.Equal(6, state.Total);
        Assert.Equal(1, state.Active);
        Assert.Equal(1, state.Valid);
        Assert.Equal(1, state.Expired);
        Assert.Equal(1, state.Disabled);
        Assert.Equal(2, state.Unclassified);
        Assert.Equal("active", Assert.Single(state.Rows,
            x => x.Username == "runningActive").State);
        Assert.Equal("expired", Assert.Single(state.Rows,
            x => x.Username == "spent").State);
        Assert.Equal("unclassified", Assert.Single(state.Rows,
            x => x.Username == "timeSpent").State);
        Assert.Equal("disabled", Assert.Single(state.Rows,
            x => x.Username == "off").State);
    }

    [Fact]
    public void V6_unused_active_expired_are_distinguished_by_usage_and_positive_transfer_limit()
    {
        var users = new[]
        {
            Row("username=new", "actual-profile=weekly", "disabled=no",
                "total-download-used=0"),
            Row("username=began", "actual-profile=weekly", "disabled=no",
                "uptime-used=1h20m", "total-download-used=10",
                "transfer-limit=100"),
            Row("username=exhausted", "actual-profile=", "disabled=no",
                "total-download-used=1KiB", "total-upload-used=512",
                "transfer-limit=1.5KiB"),
            Row("username=unknown", "actual-profile=", "disabled=no",
                "uptime-used=1h30m", "transfer-limit=0"),
            Row("username=paused", "actual-profile=weekly", "disabled=yes",
                "total-download-used=900", "transfer-limit=100")
        };
        var state = UserManagerLifecycleClassifier.Classify(6, users, []);
        Assert.Equal(5, state.Total);
        Assert.Equal(1, state.Valid);
        Assert.Equal(1, state.Active);
        Assert.Equal(1, state.Expired);
        Assert.Equal(1, state.Disabled);
        Assert.Equal(1, state.Unclassified);
        Assert.Equal("expired", Assert.Single(state.Rows,
            x => x.Username == "exhausted").State);
    }

    [Fact]
    public void Missing_usage_evidence_or_missing_limit_never_marks_an_expired_card()
    {
        var users = new[]
        {
            Row("username=hasOnlyTimeLeft", "actual-profile=weekly",
                "time-left=0s", "total-time-left=0s"),
            Row("username=hasMissingLimit", "actual-profile=weekly",
                "total-download-used=1000000", "uptime-used=1h"),
            Row("username=negativeLimit", "actual-profile=weekly",
                "total-download-used=99999", "transfer-limit=-10")
        };
        var r = UserManagerLifecycleClassifier.Classify(6, users, []);
        Assert.Equal(0, r.Expired);
        Assert.Equal("unclassified", r.Rows[0].State);
        Assert.Equal("active", r.Rows[1].State);
        Assert.Equal("active", r.Rows[2].State);
    }

    [Fact]
    public void Invalid_users_duplicate_names_or_unknown_version_fail_closed()
    {
        IReadOnlyDictionary<string, string>[] rows = [Row("name=same"), Row("name=same")];
        Assert.Throws<InvalidOperationException>(() =>
            UserManagerLifecycleClassifier.Classify(7, rows, []));
        Assert.Throws<ArgumentException>(() =>
            UserManagerLifecycleClassifier.Classify(5, [], []));
        Assert.Throws<InvalidOperationException>(() =>
            UserManagerLifecycleClassifier.Classify(6, [Row("disabled=no")], []));
    }

    [Fact]
    public void Large_inventory_classifies_in_one_pass_without_assignments_count_inflation()
    {
        var users = Enumerable.Range(0, 25000)
            .Select(i => (IReadOnlyDictionary<string, string>)Row(
                "name=" + i, ".id=*" + i.ToString("X"), "disabled=no",
                "transfer-limit=10", "total-download-used=10")).ToArray();
        var assignments = users.Select(x => Row(
            "user=" + x["name"], "state=used")).ToArray();
        var snapshot = UserManagerLifecycleClassifier.Classify(7, users, assignments);
        Assert.Equal(25000, snapshot.Total);
        Assert.Equal(25000, snapshot.Expired);
        Assert.Equal(25000, snapshot.Rows.Count);
    }
}
