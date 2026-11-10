using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Printing;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class VoucherInlinePreviewTests
{
    [Fact]
    public void Existing_unchanged_batch_previews_exact_first_generated_voucher()
    {
        var first = new Voucher("0022", "99887766", "password", Price: 100);
        var options = new CardGenerationOptions(250, 8, "0123456789");
        var metadata = new VoucherCardMetadataOptions();
        var selected = VoucherInlinePreview.Choose(options, metadata,
            [first], generatedStillCurrent: true, DateTimeOffset.UtcNow);
        Assert.Same(first, selected);
        CardArtScene scene = CardArtwork.Create(selected, 50, 19,
            new CardArtOptions(ShowPrice: true));
        Assert.Equal("99887766", Assert.Single(scene.Texts, t => t.Key == "username").Value);
    }

    [Fact]
    public void Stale_batch_previews_one_fresh_sample_and_does_not_replace_generated_cards()
    {
        var old = new Voucher("0001", "OLD", "OLDPASS");
        var opts = new CardGenerationOptions(10000, 6, "0123456789", StartSequence: 5);
        var when = new DateTimeOffset(2026, 11, 10, 6, 0, 0, TimeSpan.Zero);
        Voucher selected = VoucherInlinePreview.Choose(opts,
            new VoucherCardMetadataOptions(15m, 2m, "GB", 1, 0, 7),
            [old], generatedStillCurrent: false, when, _ => 0);
        Assert.Equal("000000", selected.Username);
        Assert.Equal("0005", selected.Sequence);
        Assert.Equal("1h", selected.LimitUptime);
        Assert.Equal("OLD", old.Username);
    }
}
