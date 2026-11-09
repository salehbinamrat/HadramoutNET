using System.Text.Json;
using HadramoutNET.Core.Printing;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class CardBackgroundContractTests
{
    // Valid 2x2 24-bit PNG. No font files or external assets.
    private const string TinyPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGOs5VdhYGBgYmBgYGBgAAAIwwC0spTHRgAAAABJRU5ErkJggg==";

    [Theory]
    [InlineData("stretch")]
    [InlineData("fill")]
    [InlineData("crop")]
    [InlineData("fit")]
    [InlineData("tile")]
    public void Embedded_png_and_mode_roundtrip_through_original_package_prefix(string mode)
    {
        var cfg = new CardArtOptions(BackgroundData: TinyPng, BackgroundMode: mode);
        var saved = CardArtPresetCodec.ToSettings(cfg);
        Assert.Equal(TinyPng, saved["card.background_data"].GetString());
        Assert.Equal(mode, saved["card.background_mode"].GetString());
        var loaded = CardArtPresetCodec.FromSettings(saved);
        Assert.Equal(cfg.BackgroundData, loaded.BackgroundData);
        Assert.Equal(mode, loaded.BackgroundMode);
        CardBackgroundPng image = Assert.IsType<CardBackgroundPng>(
            CardBackgroundAsset.Decode(loaded.BackgroundData));
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(79, image.Png.Length);
    }

    [Fact]
    public void Fit_background_uses_original_white_letterbox_in_both_renderers()
    {
        var config = new CardArtOptions(BackgroundData: TinyPng,
            BackgroundMode: "fit");
        var scene = CardArtwork.Create(
            new HadramoutNET.Core.Cards.Voucher("1", "123456", ""),
            50, 19, config);
        Assert.Equal("#FFFFFF", scene.Background);
        Assert.Equal("fit", CardArtPresetCodec.FromSettings(
            CardArtPresetCodec.ToSettings(config)).BackgroundMode);
    }

    [Fact]
    public void Empty_background_preserves_default_red_cream_card()
    {
        var cfg = CardArtPresetCodec.FromSettings(
            new Dictionary<string, JsonElement>());
        Assert.Null(cfg.BackgroundData);
        Assert.Equal("stretch", cfg.BackgroundMode);
        Assert.Null(CardBackgroundAsset.Decode(cfg.BackgroundData));
    }

    [Theory]
    [InlineData("card.background_mode", "\"invalid\"")]
    [InlineData("card.background_data", "\"not base64!\"")]
    [InlineData("card.background_data", "12")]
    public void Invalid_template_image_is_rejected_before_a_print_or_preset_save(
        string key, string json)
    {
        var bad = new Dictionary<string, JsonElement> {
            [key] = JsonDocument.Parse(json).RootElement.Clone()
        };
        Assert.Throws<PrintLayoutException>(() => CardArtPresetCodec.FromSettings(bad));
    }

    [Fact]
    public void Invalid_header_or_oversize_pixel_count_rejected_before_png_decoding()
    {
        var png = Convert.FromBase64String(TinyPng);
        png[0] = 0x01;
        Assert.Throws<PrintLayoutException>(() =>
            CardBackgroundAsset.Decode(Convert.ToBase64String(png)));

        png = Convert.FromBase64String(TinyPng);
        // Set the IHDR pixel dimensions to 100,000 x 100,000 (decompression bomb).
        png[16] = png[20] = 0x00; png[17] = png[21] = 0x01;
        png[18] = png[22] = 0x86; png[19] = png[23] = 0xA0;
        Assert.Throws<PrintLayoutException>(() =>
            CardBackgroundAsset.Decode(Convert.ToBase64String(png)));
    }

    [Fact]
    public void Separate_package_backgrounds_remain_isolated_after_sqlite_restart()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "hnet-art-store-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "hadramout-net.db");
        try
        {
            var store = new HadramoutNET.Core.Data.PackageSettingsStore(path);
            var withPhoto = CardArtPresetCodec.ToSettings(
                new CardArtOptions(BackgroundData: TinyPng, BackgroundMode: "fill"));
            var withoutPhoto = CardArtPresetCodec.ToSettings(
                new CardArtOptions(BackgroundMode: "stretch"));
            store.SavePackage("with-background", withPhoto);
            store.SavePackage("without-background", withoutPhoto);

            var reopened = new HadramoutNET.Core.Data.PackageSettingsStore(path);
            var packages = reopened.ListPackages();
            Assert.Equal(2, packages.Count);
            var first = CardArtPresetCodec.FromSettings(
                Assert.Single(packages, row => row.Name == "with-background").Settings);
            var second = CardArtPresetCodec.FromSettings(
                Assert.Single(packages, row => row.Name == "without-background").Settings);
            Assert.Equal(TinyPng, first.BackgroundData);
            Assert.Equal("fill", first.BackgroundMode);
            Assert.True(string.IsNullOrEmpty(second.BackgroundData));
            Assert.Equal("stretch", second.BackgroundMode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Invalid_text_fields_cannot_be_used_to_hide_image_errors()
    {
        var config = new CardArtOptions(BackgroundData: "%%%not-an-image%%%");
        Assert.Throws<PrintLayoutException>(() => CardArtPresetCodec.Validate(config));
    }
}
