using System.Net;
using System.Net.Sockets;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>
/// Regression at real RouterOS TCP protocol boundary. Incomplete read
/// preflights must never authorize a remote /add mutation.
/// </summary>
public sealed class VoucherPublishPreflightCompletenessTests
{
    private static readonly DateTimeOffset Time =
        new(2026, 11, 9, 11, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("hotspot", "truncated_users")]
    [InlineData("ppp", "truncated_users")]
    [InlineData("hotspot", "malformed_user")]
    [InlineData("ppp", "oversized_users")]
    [InlineData("hotspot", "truncated_profiles")]
    [InlineData("ppp", "malformed_profile")]
    [InlineData("hotspot", "bad_count")]
    public async Task Unsafe_preflight_rejects_before_any_router_write(
        string service, string failure)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(17));
        string users = service == "hotspot" ?
            "/ip/hotspot/user/print" : "/ppp/secret/print";
        string profiles = service == "hotspot" ?
            "/ip/hotspot/user/profile/print" : "/ppp/profile/print";

        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(stop.Token);
            using var stream = peer.GetStream();
            await RouterOsSentenceCodec.ReadAsync(stream, stop.Token);
            await Reply(stream, "!done");
            await RouterOsSentenceCodec.ReadAsync(stream, stop.Token);
            await Reply(stream, "!re", "=name=Verified");
            await Reply(stream, "!done");

            var count = await RouterOsSentenceCodec.ReadAsync(stream, stop.Token);
            Assert.Contains(users, count);
            Assert.Contains("=count-only=yes", count);
            await Reply(stream, "!done", "=ret=" + (failure switch
            {
                "truncated_users" => "3",
                "malformed_user" => "1",
                "oversized_users" => "20001",
                "bad_count" => "unknown",
                _ => "1"
            }));

            if (failure is "oversized_users" or "bad_count")
            {
                await AssertNoMoreCommands(stream);
                return;
            }

            var list = await RouterOsSentenceCodec.ReadAsync(stream, stop.Token);
            Assert.Contains(users, list);
            Assert.Contains("=.proplist=name", list);
            if (failure == "malformed_user")
                await Reply(stream, "!re", "=disabled=no");
            else if (failure == "truncated_users")
                await Reply(stream, "!re", "=name=non-colliding");
            else
                await Reply(stream, "!re", "=name=other-account");
            await Reply(stream, "!done");
            if (failure is "truncated_users" or "malformed_user")
            {
                await AssertNoMoreCommands(stream);
                return;
            }

            var profileCount = await RouterOsSentenceCodec.ReadAsync(stream, stop.Token);
            Assert.Contains(profiles, profileCount);
            Assert.Contains("=count-only=yes", profileCount);
            await Reply(stream, "!done", "=ret=2");

            var profileList = await RouterOsSentenceCodec.ReadAsync(stream, stop.Token);
            Assert.Contains(profiles, profileList);
            Assert.Contains("=.proplist=name", profileList);
            if (failure == "malformed_profile")
            {
                await Reply(stream, "!re", "=name=weekly");
                await Reply(stream, "!re", "=disabled=no");
            }
            else
                await Reply(stream, "!re", "=name=weekly");
            await Reply(stream, "!done");
            await AssertNoMoreCommands(stream);
        }, stop.Token);

        var manager = RouterLoginCoordinator.CreateForTests(
            () => new LicenseAccess("active", "Valid", "online_valid",
                "0123456789ABCDEF0123456789ABCDEF"),
            () => new TrialAccess("locked", "trial_expired", "expired", 0),
            () => new TrialAccess("locked", "trial_expired", "expired", 0));

        var login = await manager.LoginAsync(new RouterLoginRequest(
            "127.0.0.1", port.ToString(), "user", "secret"), Time, stop.Token);
        Assert.True(login.Success);
        await using var session = login.Session!;
        var report = await new RouterVoucherPublisher().PublishAsync(session,
            [new Voucher("1", "voucher001", "pass123")],
            new VoucherPublishOptions(service, "weekly", Confirmed: true),
            stop.Token);
        Assert.False(report.Success);
        Assert.Equal("preflight_unavailable", report.Reason);
        Assert.Equal(0, report.Added);
        await session.DisposeAsync();
        await server;
    }

    private static Task Reply(NetworkStream stream, params string[] words) =>
        stream.WriteAsync(RouterOsSentenceCodec.Encode(words)).AsTask();

    private static async Task AssertNoMoreCommands(NetworkStream stream)
    {
        byte[] one = new byte[1];
        using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(550));
        try
        {
            // No command is permitted (including further read commands).
            Assert.Equal(0, await stream.ReadAsync(one, quiet.Token));
        }
        catch (OperationCanceledException) { }
    }
}
