using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>Exercises real RouterOS wire sentences against isolated loopback peers.</summary>
public sealed class UserManagerCatalogTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static async Task Reply(NetworkStream stream, params string[] words) =>
        await stream.WriteAsync(RouterOsSentenceCodec.Encode(words));

    private static async Task<RouterAuthorizedSession> LoginAsync(
        int port, CancellationToken token)
    {
        var coordinator = RouterLoginCoordinator.CreateForTests(
            () => new("active", "صالح", "online_valid", "0123456789ABCDEF0123456789ABCDEF"),
            () => new("locked", "trial_expired", "منتهي", 0),
            () => new("locked", "trial_expired", "منتهي", 0));
        var result = await coordinator.LoginAsync(
            new RouterLoginRequest("127.0.0.1", port.ToString(), "tester", "secret"),
            Now, token);
        Assert.True(result.Success);
        return result.Session!;
    }

    private static async Task AcceptLoginAsync(TcpListener listener, Func<NetworkStream, Task> run,
        CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        using var stream = peer.GetStream();
        Assert.Contains("/login", await RouterOsSentenceCodec.ReadAsync(stream, token));
        await Reply(stream, "!done");
        Assert.Equal("/system/identity/print",
            Assert.Single(await RouterOsSentenceCodec.ReadAsync(stream, token)));
        await Reply(stream, "!re", "=name=Router");
        await Reply(stream, "!done");
        await run(stream);
    }

    [Fact]
    public async Task RouterOS7_detects_correct_menu_and_reads_profiles_and_users_without_password()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(16));
        Task server = AcceptLoginAsync(listener, async stream =>
        {
            var profiles = await RouterOsSentenceCodec.ReadAsync(stream, stop.Token);
            Assert.Contains("/user-manager/profile/print", profiles);
            Assert.DoesNotContain(profiles, x => x.Contains("password", StringComparison.OrdinalIgnoreCase));
            await Reply(stream, "!re", "=name=weekly", "=.id=*3");
            await Reply(stream, "!re", "=name=monthly", "=.id=*4");
            await Reply(stream, "!done");
            var users = await RouterOsSentenceCodec.ReadAsync(stream, stop.Token);
            Assert.Contains("/user-manager/user/print", users);
            Assert.DoesNotContain(users, x => x.Contains("password", StringComparison.OrdinalIgnoreCase));
            await Reply(stream, "!re", "=name=9012", "=disabled=no");
            await Reply(stream, "!re", "=name=9013", "=disabled=yes");
            await Reply(stream, "!done");
        }, stop.Token);

        await using var session = await LoginAsync(port, stop.Token);
        var result = await new UserManagerCatalog().ReadAsync(session, stop.Token);
        Assert.Equal("ready", result.Status);
        Assert.Equal(7, result.Version);
        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal("weekly", result.Profiles[0].Name);
        Assert.Equal(new[] { "9012", "9013" }, result.Users.Select(x => x.Username));
        Assert.True(result.Users[1].Disabled);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task RouterOS6_unsupported_v7_menu_falls_back_to_tool_user_manager()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(16));
        Task server = AcceptLoginAsync(listener, async stream =>
        {
            Assert.Contains("/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, stop.Token));
            await Reply(stream, "!trap", "=message=no such command");
            await Reply(stream, "!done");

            var profiles = await RouterOsSentenceCodec.ReadAsync(stream, stop.Token);
            Assert.Contains("/tool/user-manager/profile/print", profiles);
            await Reply(stream, "!re", "=name=default", "=owner=admin");
            await Reply(stream, "!done");

            var users = await RouterOsSentenceCodec.ReadAsync(stream, stop.Token);
            Assert.Contains("/tool/user-manager/user/print", users);
            Assert.DoesNotContain(users, x => x.Contains("password", StringComparison.OrdinalIgnoreCase));
            await Reply(stream, "!re", "=username=110011", "=actual-profile=default",
                "=disabled=no", "=customer=admin");
            await Reply(stream, "!done");
        }, stop.Token);

        await using var session = await LoginAsync(port, stop.Token);
        var result = await new UserManagerCatalog().ReadAsync(session, stop.Token);
        Assert.Equal("ready", result.Status);
        Assert.Equal(6, result.Version);
        Assert.Equal("admin", Assert.Single(result.Profiles).Owner);
        Assert.Equal("110011", Assert.Single(result.Users).Username);
        Assert.Equal("default", Assert.Single(result.Users).Profile);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task Permission_denied_is_not_misclassified_as_uninstalled_v7()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(16));
        Task server = AcceptLoginAsync(listener, async stream =>
        {
            Assert.Contains("/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, stop.Token));
            await Reply(stream, "!trap", "=message=not enough permissions");
            await Reply(stream, "!done");
            byte[] extra = new byte[1];
            using var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try { Assert.Equal(0, await stream.ReadAsync(extra, shortWait.Token)); }
            catch (OperationCanceledException) { }
        }, stop.Token);

        await using var session = await LoginAsync(port, stop.Token);
        var result = await new UserManagerCatalog().ReadAsync(session, stop.Token);
        Assert.Equal("permission_denied", result.Status);
        Assert.Equal(0, result.Version);
        Assert.Empty(result.Users);
        Assert.Empty(result.Profiles);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task Missing_both_UM_packages_is_distinct_from_zero_users()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(16));
        Task server = AcceptLoginAsync(listener, async stream =>
        {
            Assert.Contains("/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, stop.Token));
            await Reply(stream, "!trap", "=message=bad command name user-manager");
            await Reply(stream, "!done");
            Assert.Contains("/tool/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, stop.Token));
            await Reply(stream, "!trap", "=message=no such command");
            await Reply(stream, "!done");
        }, stop.Token);

        await using var session = await LoginAsync(port, stop.Token);
        var result = await new UserManagerCatalog().ReadAsync(session, stop.Token);
        Assert.Equal("not_installed", result.Status);
        Assert.Equal(0, result.Version);
        Assert.Empty(result.Users);
        await session.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task User_query_forbidden_retains_profiles_but_does_not_claim_zero_users()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(16));
        Task server = AcceptLoginAsync(listener, async stream =>
        {
            Assert.Contains("/user-manager/profile/print",
                await RouterOsSentenceCodec.ReadAsync(stream, stop.Token));
            await Reply(stream, "!re", "=name=package");
            await Reply(stream, "!done");
            Assert.Contains("/user-manager/user/print",
                await RouterOsSentenceCodec.ReadAsync(stream, stop.Token));
            await Reply(stream, "!trap", "=message=permission denied");
            await Reply(stream, "!done");
        }, stop.Token);

        await using var session = await LoginAsync(port, stop.Token);
        var result = await new UserManagerCatalog().ReadAsync(session, stop.Token);
        Assert.Equal("users_unavailable", result.Status);
        Assert.Equal(7, result.Version);
        Assert.Single(result.Profiles);
        Assert.Empty(result.Users);
        Assert.False(result.UsersComplete);
        await session.DisposeAsync();
        await server;
    }
}
