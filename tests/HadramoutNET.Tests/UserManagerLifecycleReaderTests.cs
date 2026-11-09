using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class UserManagerLifecycleReaderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 9, 11, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_authoritative_read_is_cached_and_never_requests_passwords(
        bool truncateUsers)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            await Reply(stream, "!re", "=name=Router");
            await Reply(stream, "!done");
            var probe = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/user-manager/profile/print", probe);
            await Reply(stream, "!re", "=name=weekly");
            await Reply(stream, "!done");

            var countUsers = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/user-manager/user/print", countUsers);
            Assert.Contains("=count-only=yes", countUsers);
            await Reply(stream, "!done", "=ret=2");
            var users = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/user-manager/user/print", users);
            Assert.Contains(users, w => w.StartsWith("=.proplist=",
                StringComparison.Ordinal));
            Assert.DoesNotContain(users, w => w.Contains("password",
                StringComparison.OrdinalIgnoreCase));
            await Reply(stream, "!re", "=.id=*1", "=name=active",
                "=disabled=no", "=transfer-limit=100", "=total-download-used=10");
            if (!truncateUsers)
                await Reply(stream, "!re", "=.id=*2", "=name=expired",
                    "=disabled=no", "=transfer-limit=100",
                    "=total-download-used=100");
            await Reply(stream, "!done");
            if (truncateUsers)
            {
                await AssertIdle(stream);
                return;
            }

            var countProfiles = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/user-manager/user-profile/print", countProfiles);
            Assert.Contains("=count-only=yes", countProfiles);
            await Reply(stream, "!done", "=ret=2");
            var assignments = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Contains("/user-manager/user-profile/print", assignments);
            Assert.Contains(assignments, x => x.Contains("user,state"));
            await Reply(stream, "!re", "=user=active", "=state=running active");
            await Reply(stream, "!re", "=user=expired", "=state=used");
            await Reply(stream, "!done");
            await AssertIdle(stream); // Second read uses cache, no replay.
        }, timeout.Token);

        var loginCoordinator = RouterLoginCoordinator.CreateForTests(
            () => new LicenseAccess("active", "Valid", "online_valid",
                "0123456789ABCDEF0123456789ABCDEF"),
            () => new TrialAccess("locked", "expired", "expired", 0),
            () => new TrialAccess("locked", "expired", "expired", 0));
        var login = await loginCoordinator.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "tester", "pwd"),
            Now, timeout.Token);
        Assert.True(login.Success);
        await using var session = login.Session!;
        var reader = new UserManagerLifecycleReader();
        var first = await reader.ReadAsync(session, timeout.Token);
        if (truncateUsers)
        {
            Assert.False(first.Ready);
            Assert.Equal("incomplete", first.Status);
            Assert.Null(first.Snapshot);
        }
        else
        {
            Assert.True(first.Ready);
            Assert.Equal(1, first.Snapshot!.Active);
            Assert.Equal(1, first.Snapshot.Expired);
            var cached = await reader.ReadAsync(session, timeout.Token);
            Assert.True(cached.Cached);
            Assert.Same(first.Snapshot, cached.Snapshot);
        }
        await session.DisposeAsync();
        await server;
    }

    private static Task Reply(NetworkStream stream, params string[] words) =>
        stream.WriteAsync(RouterOsSentenceCodec.Encode(words)).AsTask();

    private static async Task AssertIdle(NetworkStream stream)
    {
        byte[] b = new byte[1];
        using var idle = new CancellationTokenSource(TimeSpan.FromMilliseconds(450));
        try { Assert.Equal(0, await stream.ReadAsync(b, idle.Token)); }
        catch (OperationCanceledException) { }
    }
}
