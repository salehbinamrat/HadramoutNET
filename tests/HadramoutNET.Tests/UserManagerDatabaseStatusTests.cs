using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>Real RouterOS sentence exchange over loopback; no mocked API results.</summary>
public sealed class UserManagerDatabaseStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static Task Reply(NetworkStream stream, params string[] words) =>
        stream.WriteAsync(RouterOsSentenceCodec.Encode(words)).AsTask();

    private static async Task<RouterAuthorizedSession> Login(int port, CancellationToken ct)
    {
        var coordinator = RouterLoginCoordinator.CreateForTests(
            () => new("active", "صالح", "online_valid", "0123456789ABCDEF0123456789ABCDEF"),
            () => new("locked", "expired", "منتهي", 0),
            () => new("locked", "expired", "منتهي", 0));
        var result = await coordinator.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "admin", "secret"), Now, ct);
        Assert.True(result.Success);
        return result.Session!;
    }

    private static async Task Handshake(NetworkStream stream, CancellationToken ct)
    {
        Assert.Contains("/login", await RouterOsSentenceCodec.ReadAsync(stream, ct));
        await Reply(stream, "!done");
        Assert.Contains("/system/identity/print", await RouterOsSentenceCodec.ReadAsync(stream, ct));
        await Reply(stream, "!re", "=name=Router");
        await Reply(stream, "!done");
    }

    private static async Task AssertNoFurtherRequests(NetworkStream stream)
    {
        var b = new byte[1];
        using var delay = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        try { Assert.Equal(0, await stream.ReadAsync(b, delay.Token)); }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task RouterOS7_reads_only_database_size_and_free_disk_space()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await Handshake(stream, timeout.Token);
            var detect = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Equal("/user-manager/profile/print", detect[0]);
            Assert.Contains("=count-only=yes", detect);
            await Reply(stream, "!done", "=ret=3");

            var database = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Equal("/user-manager/database/print", database[0]);
            Assert.Contains("=.proplist=db-size,free-disk-space", database);
            Assert.DoesNotContain(database, x => x.Contains("password", StringComparison.OrdinalIgnoreCase));
            await Reply(stream, "!re", "=db-size=123456", "=free-disk-space=456789",
                "=password=SECRET", "=local-path=/secret/path", "=other=DO_NOT_LEAK");
            await Reply(stream, "!done");
            await AssertNoFurtherRequests(stream);
        }, timeout.Token);
        await using var session = await Login(port, timeout.Token);
        var result = await new UserManagerDatabaseStatus().ReadAsync(session, timeout.Token);
        Assert.Equal("ready", result.Status);
        Assert.Equal(7, result.Version);
        Assert.Equal("123456", result.DatabaseSize);
        Assert.Equal("456789", result.FreeDiskSpace);
        Assert.DoesNotContain("SECRET", result.ToString());
        Assert.DoesNotContain("DO_NOT_LEAK", result.ToString());
        await session.DisposeAsync();
        await peer;
    }

    [Fact]
    public async Task RouterOS6_falls_back_only_on_unsupported_v7_command()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await Handshake(stream, timeout.Token);
            Assert.Contains("/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token));
            await Reply(stream, "!trap", "=message=no such command");
            await Reply(stream, "!done");

            var fallback = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Equal("/tool/user-manager/profile/print", fallback[0]);
            Assert.Contains("=count-only=yes", fallback);
            await Reply(stream, "!done", "=ret=4");

            var database = await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token);
            Assert.Equal("/tool/user-manager/database/print", database[0]);
            Assert.Contains("=.proplist=db-size,free-disk-space", database);
            await Reply(stream, "!re", "=db-size=12MiB", "=free-disk-space=20MiB",
                "=password=NEVER_SAVED");
            await Reply(stream, "!done");
        }, timeout.Token);
        await using var session = await Login(port, timeout.Token);
        var result = await new UserManagerDatabaseStatus().ReadAsync(session, timeout.Token);
        Assert.Equal("ready", result.Status);
        Assert.Equal(6, result.Version);
        Assert.Equal("12MiB", result.DatabaseSize);
        Assert.Equal("20MiB", result.FreeDiskSpace);
        Assert.DoesNotContain("NEVER_SAVED", result.ToString());
        await peer;
    }

    [Fact]
    public async Task Permission_denied_does_not_try_alternate_menu_or_fake_zero()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await Handshake(stream, timeout.Token);
            Assert.Contains("/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token));
            await Reply(stream, "!trap", "=message=not enough permissions: secret");
            await Reply(stream, "!done");
            await AssertNoFurtherRequests(stream);
        }, timeout.Token);
        await using var session = await Login(port, timeout.Token);
        var result = await new UserManagerDatabaseStatus().ReadAsync(session, timeout.Token);
        Assert.Equal("permission_denied", result.Status);
        Assert.Equal(0, result.Version);
        Assert.Null(result.DatabaseSize);
        Assert.Null(result.FreeDiskSpace);
        Assert.DoesNotContain("secret", result.ToString());
        await session.DisposeAsync();
        await peer;
    }

    [Fact]
    public async Task Missing_both_UserManager_menus_is_not_installed_not_empty()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await Handshake(stream, timeout.Token);
            Assert.Contains("/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token));
            await Reply(stream, "!trap", "=message=unknown command");
            await Reply(stream, "!done");
            Assert.Contains("/tool/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token));
            await Reply(stream, "!trap", "=message=bad command name");
            await Reply(stream, "!done");
            await AssertNoFurtherRequests(stream);
        }, timeout.Token);
        await using var session = await Login(port, timeout.Token);
        var result = await new UserManagerDatabaseStatus().ReadAsync(session, timeout.Token);
        Assert.Equal("not_installed", result.Status);
        Assert.Equal(0, result.Version);
        Assert.Null(result.DatabaseSize);
        await session.DisposeAsync();
        await peer;
    }

    [Theory]
    [InlineData("empty_response")]
    [InlineData("fields_unavailable")]
    [InlineData("database_unavailable")]
    public async Task Missing_fields_empty_response_or_denied_database_are_never_reported_as_ready(
        string scenario)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await Handshake(stream, timeout.Token);
            Assert.Equal("/user-manager/profile/print",
                (await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token))[0]);
            await Reply(stream, "!done", "=ret=0");
            Assert.Equal("/user-manager/database/print",
                (await RouterOsSentenceCodec.ReadAsync(stream, timeout.Token))[0]);
            if (scenario == "database_unavailable")
            {
                await Reply(stream, "!trap", "=message=not enough permissions: private");
                await Reply(stream, "!done");
            }
            else if (scenario == "fields_unavailable")
            {
                await Reply(stream, "!re", "=db-size=1234", "=password=NOT_SAVED");
                await Reply(stream, "!done");
            }
            else await Reply(stream, "!done");
        }, timeout.Token);
        await using var session = await Login(port, timeout.Token);
        var result = await new UserManagerDatabaseStatus().ReadAsync(session, timeout.Token);
        Assert.Equal(scenario, result.Status);
        Assert.Equal(7, result.Version);
        Assert.Null(result.DatabaseSize);
        Assert.Null(result.FreeDiskSpace);
        Assert.DoesNotContain("NOT_SAVED", result.ToString());
        Assert.DoesNotContain("private", result.ToString());
        await peer;
    }

    [Fact]
    public async Task Closed_or_expired_session_must_refuse_read_before_any_router_request()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await Handshake(stream, timeout.Token);
            await AssertNoFurtherRequests(stream);
        }, timeout.Token);
        var session = await Login(port, timeout.Token);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new UserManagerDatabaseStatus().ReadAsync(session, timeout.Token));
        await peer;
    }
}
