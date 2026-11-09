using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RouterOsCommandGuardTests
{
    [Fact]
    public void Allows_hotspot_user_add()
    {
        RouterOsCommandGuard.Validate("/ip/hotspot/user/add", new Dictionary<string, string> { ["name"] = "abc" });
    }

    [Theory]
    [InlineData("/ip/firewall/filter/add")]
    [InlineData("/system/reset-configuration")]
    [InlineData("/execute")]
    [InlineData("/ip/address/set")]
    public void Denies_out_of_scope_mutations(string path)
    {
        Assert.Throws<InvalidOperationException>(() => RouterOsCommandGuard.Validate(path));
    }

    [Fact]
    public void Print_must_be_read_only()
    {
        RouterOsCommandGuard.Validate("/ip/hotspot/user/print", new Dictionary<string, string> { [".proplist"] = "name" });
        Assert.Throws<InvalidOperationException>(() => RouterOsCommandGuard.Validate("/ip/hotspot/user/print", new Dictionary<string, string> { ["file"] = "dump" }));
    }

    [Fact]
    public void Set_requires_exact_router_record_id()
    {
        Assert.Throws<InvalidOperationException>(() => RouterOsCommandGuard.Validate("/ip/hotspot/user/set",
            new Dictionary<string, string> { [".id"] = "admin", ["disabled"] = "yes" }));
        RouterOsCommandGuard.Validate("/ip/hotspot/user/set",
            new Dictionary<string, string> { [".id"] = "*1A", ["disabled"] = "yes" });
    }

    [Fact]
    public void Does_not_allow_router_scripts()
    {
        Assert.Throws<InvalidOperationException>(() => RouterOsCommandGuard.Validate("/ip/hotspot/user/add",
            new Dictionary<string, string> { ["name"] = "u", ["on-login"] = ":put 1" }));
    }

    [Fact]
    public void Two_privileged_maintenance_commands_accept_no_extra_parameters()
    {
        RouterOsCommandGuard.Validate("/system/reboot");
        RouterOsCommandGuard.Validate("/user-manager/database/optimize-db");
        Assert.Throws<InvalidOperationException>(() => RouterOsCommandGuard.Validate("/system/reboot",
            new Dictionary<string,string>{{"force","yes"}}));
    }
}
