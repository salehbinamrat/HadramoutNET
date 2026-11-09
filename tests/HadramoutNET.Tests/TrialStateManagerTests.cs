using System.Runtime.Versioning;
using System.Security.Cryptography;
using HadramoutNET.Core.Licensing;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>All protected files use fresh temporary directories on the CI Windows runner.</summary>
[SupportedOSPlatform("windows")]
public sealed class TrialStateManagerTests
{
    private static readonly DateTimeOffset First = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Device = "F7B54676F52E84676D8735368296BB9F63CD01C204D7A3847F101918825013B2";

    private static (TrialStateManager manager, string dir, string user, string common) Fresh(
        Func<DateTimeOffset, LegacyTrialReadResult>? legacy = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "hnet-trial-" + Guid.NewGuid().ToString("N"));
        string user = Path.Combine(root, "user");
        string common = Path.Combine(root, "shared");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(common);
        return (TrialStateManager.CreateForTests(user, common, Device,
            legacy ?? (_ => new("not_started", false, false, null))), root, user, common);
    }

    [Fact]
    public void Preview_is_read_only_and_never_starts_480_hours()
    {
        var (store, root, user, common) = Fresh();
        try
        {
            var result = store.Preview(First);
            Assert.Equal("ready", result.Mode);
            Assert.Equal("not_started", result.Reason);
            Assert.Equal(480L * 3600L, result.RemainingSeconds);
            Assert.Empty(Directory.GetFiles(user, "*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(common, "*", SearchOption.AllDirectories));
            Assert.Equal("ready", store.Preview(First.AddDays(15)).Mode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Only_explicit_after_successful_router_login_creates_an_immutable_start()
    {
        var (store, root, _, __) = Fresh();
        try
        {
            Assert.Equal("ready", store.Preview(First).Mode);
            var started = store.ConfirmAfterAuthenticatedRouterLogin(First);
            Assert.True(started.Permitted);
            Assert.Equal("trial", started.Mode);
            Assert.Equal(First, started.TrialStarted);
            Assert.Equal(First.AddHours(480), started.TrialExpires);

            var reboot = TrialStateManager.CreateForTests(Path.Combine(root, "user"),
                Path.Combine(root, "shared"), Device,
                _ => new("not_started", false, false, null));
            var progress = reboot.Preview(First.AddDays(7));
            Assert.Equal(First, progress.TrialStarted);
            Assert.Equal(13L * 86400, progress.RemainingSeconds);
            Assert.Equal("trial", reboot.ConfirmAfterAuthenticatedRouterLogin(First.AddDays(7)).Mode);
            Assert.Equal(First, reboot.Preview(First.AddDays(7)).TrialStarted);
            Assert.Equal("trial_expired", reboot.Preview(First.AddHours(480)).Reason);
            Assert.False(reboot.Preview(First.AddDays(21)).Permitted);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Existing_python_trial_timestamp_is_not_reset_or_written_to()
    {
        var original = new TrialSnapshot(First.AddDays(-4), First.AddDays(1), Device, 6);
        var legacy = new LegacyTrialReadResult("ok", true, false, original);
        var (store, root, user, common) = Fresh(_ => legacy);
        try
        {
            var result = store.Preview(First.AddDays(5));
            Assert.Equal(First.AddDays(-4), result.TrialStarted);
            Assert.Equal(11L * 86400, result.RemainingSeconds);
            Assert.Empty(Directory.GetFiles(user, "*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(common, "*", SearchOption.AllDirectories));

            var afterLogin = store.ConfirmAfterAuthenticatedRouterLogin(First.AddDays(5));
            Assert.Equal(First.AddDays(-4), afterLogin.TrialStarted);
            var reread = store.Preview(First.AddDays(7));
            Assert.Equal(First.AddDays(-4), reread.TrialStarted);
            Assert.Equal(6, store.ReadSnapshot(First.AddDays(7))!.ActivationRevision);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Corrupt_existing_legacy_mirror_cannot_start_new_trial()
    {
        var (store, root, _, __) = Fresh(_ => new("state_invalid", true, false, null));
        try
        {
            Assert.Equal("state_invalid", store.Preview(First).Reason);
            Assert.Equal("state_invalid",
                store.ConfirmAfterAuthenticatedRouterLogin(First).Reason);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Broken_new_state_or_wrong_device_cannot_be_used_to_reset_trial()
    {
        var (store, root, user, _) = Fresh();
        try
        {
            Assert.Equal("trial", store.ConfirmAfterAuthenticatedRouterLogin(First).Mode);
            var wrong = TrialStateManager.CreateForTests(user, Path.Combine(root, "shared"),
                new string('A', 64), _ => new("not_started", false, false, null));
            Assert.Equal("state_invalid", wrong.Preview(First.AddDays(1)).Reason);
            // Corrupt both copies: there is history but no valid recovery.
            foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                File.WriteAllBytes(path, [1, 2, 3, 4]);
            Assert.Equal("state_invalid", store.Preview(First.AddDays(1)).Reason);
            Assert.Equal("state_invalid",
                store.ConfirmAfterAuthenticatedRouterLogin(First.AddDays(1)).Reason);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Trial_renewal_revision_and_clock_rollback_never_extend_start()
    {
        var (store, root, _, __) = Fresh();
        try
        {
            store.ConfirmAfterAuthenticatedRouterLogin(First);
            Assert.Equal("trial", store.AdvanceVerifiedClock(First.AddHours(6)).Mode);
            Assert.Equal("clock_rollback", store.Preview(First.AddHours(-1)).Reason);
            Assert.Equal("trial", store.Preview(First.AddHours(6)).Mode);
            var later = store.AdvanceVerifiedClock(First.AddHours(10));
            Assert.Equal(First, later.TrialStarted);
            Assert.Equal(First.AddHours(480), later.TrialExpires);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Missing_shared_storage_denies_first_start_instead_of_creating_unshared_trials()
    {
        var (store, root, _, common) = Fresh();
        try
        {
            Directory.Delete(common, true);
            File.WriteAllText(common, "block shared folder creation");
            Assert.Equal("state_invalid",
                store.ConfirmAfterAuthenticatedRouterLogin(First).Reason);
            Assert.Equal("ready", store.Preview(First).Mode);
        }
        finally { Directory.Delete(root, true); }
    }
}
