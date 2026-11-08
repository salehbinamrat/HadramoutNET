using System.IO;
using System.Text;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RouterOsCommandExchangeTests
{
    private static byte[] Reply(params string[][] replies) =>
        replies.SelectMany(RouterOsSentenceCodec.Encode).ToArray();

    [Fact]
    public async Task Read_only_command_returns_rows_and_done()
    {
        var stream = new DuplexStream(Reply(
            ["!re", "=name=حضرموت", "=uptime=1h"],
            ["!empty"],
            ["!done", "=ret=ok"]));
        var result = await RouterOsCommandExchange.ExecuteAsync(
            stream, "/ip/hotspot/user/print",
            new Dictionary<string, string> { [".proplist"] = "name,uptime" });
        Assert.Single(result.Rows);
        Assert.Equal("حضرموت", result.Rows[0]["name"]);
        Assert.Equal("1h", result.Rows[0]["uptime"]);
        Assert.Equal("ok", result.Done["ret"]);
        var words = await RouterOsSentenceCodec.ReadAsync(new MemoryStream(stream.Written));
        Assert.Equal(["/ip/hotspot/user/print", "=.proplist=name,uptime"], words);
    }

    [Fact]
    public async Task Trap_must_consume_done_before_next_command()
    {
        var stream = new DuplexStream(Reply(
            ["!trap", "=message=permission denied"],
            ["!done"],
            ["!re", "=name=second"],
            ["!done"]));
        var e = await Assert.ThrowsAsync<RouterOsTrapException>(
            () => RouterOsCommandExchange.ExecuteAsync(stream, "/system/identity/print"));
        Assert.Equal("permission denied", e.Message);
        var next = await RouterOsCommandExchange.ExecuteAsync(stream, "/system/identity/print");
        Assert.Equal("second", Assert.Single(next.Rows)["name"]);
    }

    [Fact]
    public async Task Fatal_is_not_a_successful_reply()
    {
        var stream = new DuplexStream(Reply(["!fatal", "=message=closed"]));
        var e = await Assert.ThrowsAsync<RouterOsProtocolException>(
            () => RouterOsCommandExchange.ExecuteAsync(stream, "/system/identity/print"));
        Assert.Contains("closed", e.Message);
    }

    [Fact]
    public async Task Unknown_reply_kind_fails_closed()
    {
        var stream = new DuplexStream(Reply(["!something-else"]));
        await Assert.ThrowsAsync<RouterOsProtocolException>(
            () => RouterOsCommandExchange.ExecuteAsync(stream, "/system/identity/print"));
    }

    [Theory]
    [InlineData("/ip/firewall/filter/add")]
    [InlineData("/system/reset-configuration")]
    public async Task Unauthorized_operation_is_blocked_before_network_write(string command)
    {
        var stream = new DuplexStream([]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RouterOsCommandExchange.ExecuteAsync(stream, command));
        Assert.Empty(stream.Written);
    }

    [Fact]
    public async Task Truncated_response_is_detected()
    {
        var stream = new DuplexStream([8, (byte)'!', (byte)'r']);
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => RouterOsCommandExchange.ExecuteAsync(stream, "/system/identity/print"));
    }

    [Fact]
    public async Task More_than_allowed_rows_is_rejected_before_collecting_extra_row()
    {
        var stream = new DuplexStream(Reply(["!re", "=name=a"], ["!re", "=name=b"], ["!done"]));
        await Assert.ThrowsAsync<RouterOsProtocolException>(
            () => RouterOsCommandExchange.ExecuteAsync(
                stream, "/system/identity/print", maxRows: 1));
    }

    [Fact]
    public async Task User_password_is_not_part_of_guard_rejection_error()
    {
        var stream = new DuplexStream([]);
        string secret = "not-a-real-password-2026";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RouterOsCommandExchange.ExecuteAsync(stream, "/system/reboot",
                new Dictionary<string, string> { ["password"] = secret }));
        Assert.DoesNotContain(secret, ex.ToString());
    }

    private sealed class DuplexStream(byte[] replyBytes) : Stream
    {
        private readonly MemoryStream _input = new(replyBytes, writable: false);
        private readonly MemoryStream _output = new();
        public byte[] Written => _output.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) =>
            _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) =>
            _output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _output.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
