using System.Text.Json;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Data;
using HadramoutNET.Core.Printing;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class CardArtStyleParityTests
{
    [Fact]
    public void Source_style_controls_change_one_shared_card_scene_used_by_Pdf_and_Wpf()
    {
        var config = new CardArtOptions(
            ShowTitle: true, FontSize: 24, FontBold: false,
            FontColor: "#A0B0C0", UsernameColor: "#010203",
            BorderColor: "#ABCDEF", BorderWidth: 4, BorderRadius: 8);
        var scene = CardArtwork.Create(new Voucher("1", "123456", "abcdef"),
            50, 19, config);
        Assert.Equal("#ABCDEF", scene.Border);
        Assert.Equal(4d * 50 / 280, scene.BorderWidthMm, precision: 5);
        Assert.Equal(8d * 50 / 280, scene.BorderRadiusMm, precision: 5);
        Assert.Equal("#010203", Assert.Single(scene.Texts,
            x => x.Key == "username").Color);
        Assert.Equal("#010203", Assert.Single(scene.Texts,
            x => x.Key == "password").Color);
        Assert.Equal("#A0B0C0", Assert.Single(scene.Texts,
            x => x.Key == "title").Color);
        Assert.True(scene.Texts[0].FontPt > 9);
    }

    [Fact]
    public void Saved_font_and_border_style_preserves_per_package_state_after_restart()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "hnet-style-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new CardArtOptions(
                FontSize: 18, FontBold: false,
                FontColor: "#990011", UsernameColor: "#223344",
                BorderColor: "#ABCDEF", BorderWidth: 3,
                BorderRadius: 10);
            var saved = CardArtPresetCodec.ToSettings(settings);
            Assert.Equal(18, saved["card.font_size"].GetDouble());
            Assert.False(saved["card.font_bold"].GetBoolean());
            Assert.Equal("#ABCDEF", saved["card.border_color"].GetString());
            var store = new PackageSettingsStore(
                Path.Combine(root, "hadramout-net.db"));
            store.SavePackage("personalized", saved);
            var reloaded = CardArtPresetCodec.FromSettings(Assert.Single(
                new PackageSettingsStore(Path.Combine(root, "hadramout-net.db"))
                    .ListPackages()).Settings);
            Assert.Equal(settings.FontSize, reloaded.FontSize);
            Assert.Equal(settings.FontColor, reloaded.FontColor);
            Assert.Equal(settings.UsernameColor, reloaded.UsernameColor);
            Assert.Equal(settings.BorderColor, reloaded.BorderColor);
            Assert.Equal(settings.BorderWidth, reloaded.BorderWidth);
            Assert.Equal(settings.BorderRadius, reloaded.BorderRadius);
            Assert.Equal(settings.FontBold, reloaded.FontBold);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("card.font_size", "301")]
    [InlineData("card.font_bold", "\"true\"")]
    [InlineData("card.font_color", "\"blue\"")]
    [InlineData("card.color.username", "\"#00ZZ11\"")]
    [InlineData("card.border_color", "\"#FFF\"")]
    [InlineData("card.border_width", "-1")]
    [InlineData("card.border_width", "101")]
    [InlineData("card.border_radius", "-1")]
    [InlineData("card.border_radius", "101")]
    public void Malformed_style_must_be_rejected_not_silently_hidden(
        string key, string json)
    {
        var bad = CardArtPresetCodec.ToSettings(new CardArtOptions());
        bad[key] = JsonDocument.Parse(json).RootElement.Clone();
        Assert.Throws<PrintLayoutException>(() => CardArtPresetCodec.FromSettings(bad));
    }

    [Fact]
    public void Unprintable_border_thickness_fails_before_pdf_or_printer_mutation()
    {
        var style = new CardArtOptions(BorderWidth: 100);
        Assert.Throws<PrintLayoutException>(() =>
            CardArtwork.Create(new Voucher("1", "87654", ""), 10, 2, style));
    }
}
