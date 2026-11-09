using System.IO;
using System.Text;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RouterOsWordCodecTests
{
    [Theory]
    [InlineData(0, "00")]
    [InlineData(127, "7f")]
    [InlineData(128, "8080")]
    [InlineData(16383, "bfff")]
    [InlineData(16384, "c04000")]
    [InlineData(2097151, "dfffff")]
    [InlineData(2097152, "e0200000")]
    [InlineData(33554432, "e2000000")]
    [InlineData(268435456, "f010000000")]
    public void Encoding_must_match_original_Python_protocol(int length, string expectedHex)
    {
        Assert.Equal(expectedHex, Convert.ToHexString(RouterOsWordCodec.EncodeLength(length)).ToLowerInvariant());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(16383)]
    [InlineData(16384)]
    [InlineData(2097151)]
    [InlineData(2097152)]
    [InlineData(33554432)]
    public void Encoded_lengths_roundtrip_up_to_original_read_limit(int length)
    {
        byte[] encoded = RouterOsWordCodec.EncodeLength(length);
        Assert.Equal(length, RouterOsWordCodec.DecodeLength(encoded, out int consumed));
        Assert.Equal(encoded.Length, consumed);
    }

    [Fact]
    public void Rejects_oversized_read_word()
    {
        byte[] encoded = RouterOsWordCodec.EncodeLength(33554433);
        Assert.Throws<InvalidDataException>(() => RouterOsWordCodec.DecodeLength(encoded, out _));
    }

    [Fact]
    public void Rejects_invalid_prefix_and_truncated_length()
    {
        Assert.Throws<InvalidDataException>(() => RouterOsWordCodec.DecodeLength(new byte[] { 0xF1 }, out _));
        Assert.Throws<EndOfStreamException>(() => RouterOsWordCodec.DecodeLength(new byte[] { 0xC0, 0x01 }, out _));
    }

    [Fact]
    public void Encodes_arabic_as_utf8_and_prefixes_byte_length()
    {
        string arabic = "حضرموت";
        byte[] word = RouterOsWordCodec.EncodeWord(arabic);
        byte[] payload = Encoding.UTF8.GetBytes(arabic);
        Assert.Equal(payload.Length, RouterOsWordCodec.DecodeLength(word, out int offset));
        Assert.Equal(payload, word.AsSpan(offset).ToArray());
    }
}
