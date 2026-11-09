using HadramoutNET.Core.Cards;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class VoucherBatchOutputTests
{
    private static Voucher Card(string name) => new("0001", name, "pwd");

    [Fact]
    public void Draft_is_printable_without_router_access()
    {
        var cards = new[] { Card("a"), Card("b") };
        Assert.Equal(2, VoucherBatchOutput.Select(cards, false, false, []).Count);
    }

    [Fact]
    public void Partial_upload_outputs_only_confirmed_usernames()
    {
        var cards = new[] { Card("a"), Card("b"), Card("c") };
        var selected = VoucherBatchOutput.Select(cards, true, false, ["b"]);
        Assert.Equal("b", Assert.Single(selected).Username);
        Assert.Equal(3, cards.Length);
    }

    [Fact]
    public void Uncertain_upload_without_confirmed_records_blocks_output()
    {
        var cards = new[] { Card("a") };
        Assert.Throws<CardGenerationException>(() => VoucherBatchOutput.Select(
            cards, true, false, []));
        Assert.Throws<CardGenerationException>(() => VoucherBatchOutput.Select(
            cards, true, false, ["unknown"]));
    }

    [Fact]
    public void Complete_publication_preserves_whole_batch()
    {
        var cards = new[] { Card("a"), Card("b") };
        Assert.Equal(2, VoucherBatchOutput.Select(cards, true, true, ["a", "b"]).Count);
    }

    [Fact]
    public void Preview_sample_has_valid_metadata_without_persisting_cards()
    {
        var o = new CardGenerationOptions(1, 5, "ab", Format: "code_only");
        var meta = new VoucherCardMetadataOptions(10m, 1m, "GB", 1, 30, 5);
        var when = new DateTimeOffset(2026, 11, 10, 5, 0, 0, TimeSpan.Zero);
        var sample = Assert.Single(VoucherPreviewSample.Create(o, meta, when, _ => 0));
        Assert.Equal("aaaaa", sample.Username);
        Assert.Empty(sample.Password);
        Assert.Equal(1_073_741_824L, sample.LimitBytes);
        Assert.Equal("1h30m", sample.LimitUptime);
    }
}
