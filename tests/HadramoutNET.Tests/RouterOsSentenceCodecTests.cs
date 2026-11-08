using System.IO;
using System.Linq;
using System.Text;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RouterOsSentenceCodecTests
{
    [Fact]
    public void Encodes_sentence_words_and_zero_terminator()
    {
        byte[] packet = RouterOsSentenceCodec.Encode(["/login", "=name=admin", "=password=test"]);
        Assert.Equal(0, packet[^1]);
        Assert.Equal(6, packet[0]);
        Assert.Equal("/login", Encoding.UTF8.GetString(packet, 1, 6));
    }

    [Fact]
    public async Task Decodes_complete_UTF8_sentence()
    {
        byte[] packet = RouterOsSentenceCodec.Encode(["!re", "=name=حضرموت", "=comment=123=456"]);
        using var stream = new MemoryStream(packet);
        var words = await RouterOsSentenceCodec.ReadAsync(stream);
        Assert.Equal(["!re", "=name=حضرموت", "=comment=123=456"], words);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void Parses_reply_fields_without_losing_equals_in_value()
    {
        var reply = RouterOsSentenceCodec.Parse(["!trap", "=message=denied=for=policy"]);
        Assert.Equal("!trap", reply.Kind);
        Assert.Equal("denied=for=policy", reply.Data["message"]);
    }

    [Fact]
    public async Task Short_body_throws_EOF_instead_of_reusing_partial_reply()
    {
        using var stream = new MemoryStream([5, (byte)'a', (byte)'b']);
        await Assert.ThrowsAsync<EndOfStreamException>(() => RouterOsSentenceCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task Oversized_response_word_is_rejected_before_allocation()
    {
        using var stream = new MemoryStream(RouterOsWordCodec.EncodeLength(33 * 1024 * 1024));
        await Assert.ThrowsAsync<InvalidDataException>(() => RouterOsSentenceCodec.ReadAsync(stream));
    }

    [Fact]
    public void Oversized_request_word_count_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => RouterOsSentenceCodec.Encode(Enumerable.Repeat("a", 4097)));
    }

    [Fact]
    public async Task Handles_empty_data_marker_without_treating_it_as_done()
    {
        using var stream = new MemoryStream(RouterOsSentenceCodec.Encode(["!empty"]));
        var reply = RouterOsSentenceCodec.Parse(await RouterOsSentenceCodec.ReadAsync(stream));
        Assert.Equal("!empty", reply.Kind);
    }
}
