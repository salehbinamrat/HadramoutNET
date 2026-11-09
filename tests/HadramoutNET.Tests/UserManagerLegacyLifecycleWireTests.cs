using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>
/// Full wire-protocol RouterOS v6 acceptance: lookup of profile, user
/// inventory and linked positive transfer-limit without any mutation.
/// </summary>
public sealed class UserManagerLegacyLifecycleWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_ProfileLimit_is_used_once_and_missing_link_data_is_disclosed(
        bool denyLinks)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!re", "=name=Router");
            await Reply(stream, "!done");

            var v7 = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/user-manager/profile/print", v7);
            await Reply(stream, "!trap", "=message=no such command");
            await Reply(stream, "!done");

            var v6 = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/tool/user-manager/profile/print", v6);
            await Reply(stream, "!re", "=name=weekly");
            await Reply(stream, "!done");

            await VerifyRead(stream, "/tool/user-manager/user/print",
                count: 1, [ ["=username=spent", "=name=", "=actual-profile=weekly",
                    "=disabled=no", "=uptime-used=1h", "=transfer-limit=0",
                    "=total-download-used=500MiB"] ], deadline.Token);
            await VerifyRead(stream, "/tool/user-manager/profile/print",
                count: 1, [ ["=.id=*1", "=name=weekly"] ], deadline.Token);
            await VerifyRead(stream, "/tool/user-manager/profile/limitation/print",
                count: 1, [ ["=.id=*A", "=name=small",
                    "=transfer-limit=500MiB"] ], deadline.Token);

            var countLinks = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/tool/user-manager/profile/profile-limitation/print", countLinks);
            Assert.Contains("=count-only=yes", countLinks);
            if (denyLinks)
            {
                // RouterOS ends a trapped command with !done. Without it the
                // TCP client must keep waiting and must not reuse the socket.
                await Reply(stream, "!trap", "=message=not enough permissions");
                await Reply(stream, "!done");
            }
            else
            {
                await Reply(stream, "!done", "=ret=1");
                var links = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
                Assert.Contains("=.proplist=profile,limitation", links);
                await Reply(stream, "!re", "=profile=*1", "=limitation=*A");
                await Reply(stream, "!done");
            }
            await AssertNoAdditionalCommands(stream);
        }, deadline.Token);

        var coordinator = RouterLoginCoordinator.CreateForTests(
            () => new LicenseAccess("active", "Valid", "online_valid",
                "0123456789ABCDEF0123456789ABCDEF"),
            () => new TrialAccess("locked", "expired", "expired", 0),
            () => new TrialAccess("locked", "expired", "expired", 0));
        var login = await coordinator.LoginAsync(new RouterLoginRequest(
            "127.0.0.1", port.ToString(), "tester", "pwd"),
            new DateTimeOffset(2026, 11, 9, 11, 0, 0, TimeSpan.Zero),
            deadline.Token);
        Assert.True(login.Success);
        await using var session = login.Session!;
        var reader = new UserManagerLifecycleReader();
        UserManagerLifecycleReadResult first = await reader.ReadAsync(session, deadline.Token);
        Assert.True(first.Ready);
        Assert.Equal(6, first.Snapshot!.Version);
        Assert.Equal(!denyLinks, first.LimitsVerified);
        Assert.Equal(denyLinks ? 0 : 1, first.Snapshot.Expired);
        Assert.Equal(denyLinks ? "active" : "expired",
            Assert.Single(first.Snapshot.Rows).State);
        var cached = await reader.ReadAsync(session, deadline.Token);
        Assert.True(cached.Cached);
        Assert.Equal(first.LimitsVerified, cached.LimitsVerified);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task V6_projection_silent_state_omission_uses_one_complete_sanitized_fallback()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!re", "=name=Router");
            await Reply(stream, "!done");

            var v7 = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/user-manager/profile/print", v7);
            await Reply(stream, "!trap", "=message=no such command");
            await Reply(stream, "!done");
            var v6 = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/tool/user-manager/profile/print", v6);
            await Reply(stream, "!re", "=name=weekly");
            await Reply(stream, "!done");

            // RouterOS 6 can acknowledge a .proplist read but silently omit
            // dynamic accounting fields, even when they exist in full rows.
            await VerifyRead(stream, "/tool/user-manager/user/print",
                count: 1, [["=username=spent", "=name="]], deadline.Token);
            var count = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/tool/user-manager/user/print", count);
            Assert.Contains("=count-only=yes", count);
            await Reply(stream, "!done", "=ret=1");
            var full = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/tool/user-manager/user/print", full);
            Assert.DoesNotContain(full, word =>
                word.StartsWith("=.proplist=", StringComparison.Ordinal));
            await Reply(stream, "!re", "=username=spent", "=name=",
                "=actual-profile=weekly", "=uptime-used=1h",
                "=transfer-limit=100", "=total-download-used=100",
                "=password=must-never-be-saved");
            await Reply(stream, "!done");

            await VerifyRead(stream, "/tool/user-manager/profile/print",
                count: 0, [], deadline.Token);
            await VerifyRead(stream, "/tool/user-manager/profile/limitation/print",
                count: 0, [], deadline.Token);
            await VerifyRead(stream,
                "/tool/user-manager/profile/profile-limitation/print",
                count: 0, [], deadline.Token);
            await AssertNoAdditionalCommands(stream);
        }, deadline.Token);

        var coordinator = RouterLoginCoordinator.CreateForTests(
            () => new LicenseAccess("active", "Valid", "online_valid",
                "0123456789ABCDEF0123456789ABCDEF"),
            () => new TrialAccess("locked", "expired", "expired", 0),
            () => new TrialAccess("locked", "expired", "expired", 0));
        var login = await coordinator.LoginAsync(new RouterLoginRequest(
            "127.0.0.1", port.ToString(), "tester", "pwd"),
            new DateTimeOffset(2026, 11, 9, 11, 0, 0, TimeSpan.Zero),
            deadline.Token);
        Assert.True(login.Success);
        await using var session = login.Session!;
        var result = await new UserManagerLifecycleReader()
            .ReadAsync(session, deadline.Token);
        Assert.True(result.Ready);
        Assert.Equal(1, result.Snapshot!.Expired);
        var card = Assert.Single(result.Snapshot.Rows);
        Assert.Equal("spent", card.Username);
        Assert.DoesNotContain("must-never-be-saved", string.Join("|",
            card.Username, card.Profile, card.Reason));
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task Legacy_read_rejects_truncated_user_list_as_unknown_not_zero()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!re", "=name=Router");
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!trap", "=message=no such command");
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            await Reply(stream, "!done");
            var count = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("=count-only=yes", count);
            await Reply(stream, "!done", "=ret=2");
            var users = await RouterOsSentenceCodec.ReadAsync(stream, deadline.Token);
            Assert.Contains("/tool/user-manager/user/print", users);
            await Reply(stream, "!re", "=username=only-one",
                "=actual-profile=weekly", "=uptime-used=1h");
            await Reply(stream, "!done");
            await AssertNoAdditionalCommands(stream);
        }, deadline.Token);

        var coordinator = RouterLoginCoordinator.CreateForTests(
            () => new LicenseAccess("active", "Valid", "online_valid",
                "0123456789ABCDEF0123456789ABCDEF"),
            () => new TrialAccess("locked", "expired", "expired", 0),
            () => new TrialAccess("locked", "expired", "expired", 0));
        var login = await coordinator.LoginAsync(new RouterLoginRequest(
            "127.0.0.1", port.ToString(), "tester", "pwd"),
            new DateTimeOffset(2026, 11, 9, 11, 0, 0, TimeSpan.Zero),
            deadline.Token);
        Assert.True(login.Success);
        await using var session = login.Session!;
        var result = await new UserManagerLifecycleReader().ReadAsync(session, deadline.Token);
        Assert.Equal("incomplete", result.Status);
        Assert.False(result.Ready);
        Assert.Null(result.Snapshot);
        await session.DisposeAsync();
        await server;
    }

    private static async Task VerifyRead(
        NetworkStream stream, string path, int count,
        string[][] returnedRows, CancellationToken ct)
    {
        var countCommand = await RouterOsSentenceCodec.ReadAsync(stream, ct);
        Assert.Contains(path, countCommand);
        Assert.Contains("=count-only=yes", countCommand);
        Assert.DoesNotContain(countCommand, word =>
            word.Contains("password", StringComparison.OrdinalIgnoreCase));
        await Reply(stream, "!done", "=ret=" + count);
        var listCommand = await RouterOsSentenceCodec.ReadAsync(stream, ct);
        Assert.Contains(path, listCommand);
        Assert.Contains(listCommand, x => x.StartsWith("=.proplist=", StringComparison.Ordinal));
        Assert.DoesNotContain(listCommand, word =>
            word.Contains("password", StringComparison.OrdinalIgnoreCase));
        foreach (var row in returnedRows)
            await Reply(stream, ["!re", ..row]);
        await Reply(stream, "!done");
    }

    private static Task Reply(NetworkStream stream, params string[] words) =>
        stream.WriteAsync(RouterOsSentenceCodec.Encode(words)).AsTask();

    private static async Task AssertNoAdditionalCommands(NetworkStream stream)
    {
        using var idle = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        byte[] buf = new byte[1];
        try { Assert.Equal(0, await stream.ReadAsync(buf, idle.Token)); }
        catch (OperationCanceledException) { }
    }
}
