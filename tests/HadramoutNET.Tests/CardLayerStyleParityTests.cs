using System.Text.Json;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Printing;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>
/// Original Python render.py supports separate card.size.* factors and
/// card.color.* for each text layer. These must be honored by the shared
/// PDF/WPF art scene, not merely preserved as unrendered JSON extras.
/// </summary>
public sealed class CardLayerStyleParityTests
{
    [Fact]
    public void Per_layer_font_scale_and_color_affect_only_selected_layer()
    {
        Voucher card = new("0001", "123456", "secret");
        CardArtScene standard = CardArtwork.Create(card, 50, 19,
            new CardArtOptions(ShowPrice: true));
        CardArtOptions styled = new(ShowPrice: true,
            LayerScales: new Dictionary<string, double>
            {
                ["price"] = 2.0,
                ["username"] = 1.2
            },
            LayerColors: new Dictionary<string, string>
            {
                ["price"] = "#FF0011"
            });
        CardArtScene rendered = CardArtwork.Create(card, 50, 19, styled);
        var regularPrice = Assert.Single(standard.Texts, t => t.Key == "price");
        var styledPrice = Assert.Single(rendered.Texts, t => t.Key == "price");
        Assert.Equal(regularPrice.FontPt * 2, styledPrice.FontPt, 6);
        Assert.Equal("#FF0011", styledPrice.Color);
        Assert.Equal(Assert.Single(standard.Texts,
            t => t.Key == "username").FontPt * 1.2,
            Assert.Single(rendered.Texts, t => t.Key == "username").FontPt, 6);
        Assert.Equal(Assert.Single(standard.Texts,
            t => t.Key == "password").FontPt,
            Assert.Single(rendered.Texts, t => t.Key == "password").FontPt, 6);
        Assert.Equal(Assert.Single(standard.Texts,
            t => t.Key == "password").Color,
            Assert.Single(rendered.Texts, t => t.Key == "password").Color);
    }

    [Fact]
    public void Layer_color_scale_are_recognized_on_original_json_import_and_export()
    {
        const string json = """
            {"card.add_title":true,"card.title_text":"شبكة حضرموت",
             "card.color.title":"#ABC123","card.size.title":1.5,
             "card.size.username":1.1,"card.dpi":300}
            """;
        CardTemplateImport imported =
            CardTemplateExchange.Import(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal("#ABC123", imported.Artwork.LayerColors!["title"]);
        Assert.Equal(1.5, imported.Artwork.LayerScales!["title"]);
        Assert.Equal(1.1, imported.Artwork.LayerScales["username"]);
        Assert.Single(imported.UnrecognizedCardSettings);
        Assert.True(imported.UnrecognizedCardSettings.ContainsKey("card.dpi"));

        byte[] bytes = CardTemplateExchange.Export(
            imported.Artwork, imported.UnrecognizedCardSettings);
        CardTemplateImport roundtrip = CardTemplateExchange.Import(bytes);
        Assert.Equal("#ABC123", roundtrip.Artwork.LayerColors!["title"]);
        Assert.Equal(1.5, roundtrip.Artwork.LayerScales!["title"]);
        Assert.Equal(300, roundtrip.UnrecognizedCardSettings["card.dpi"].GetInt32());
    }

    [Fact]
    public void Username_color_always_controls_password_color_as_in_python()
    {
        var options = new CardArtOptions(
            UsernameColor: "#000099",
            LayerColors: new Dictionary<string, string>
            {
                ["title"] = "#AABBCC"
            },
            ShowTitle: true);
        CardArtScene art = CardArtwork.Create(
            new Voucher("1", "123456", "123456"), 50, 19, options);
        Assert.Equal("#000099", Assert.Single(art.Texts,
            t => t.Key == "username").Color);
        Assert.Equal("#000099", Assert.Single(art.Texts,
            t => t.Key == "password").Color);
        Assert.Equal("#AABBCC", Assert.Single(art.Texts,
            t => t.Key == "title").Color);
    }

    [Theory]
    [InlineData("card.size.username", "0.39")]
    [InlineData("card.size.username", "4.01")]
    [InlineData("card.size.price", "\"NaN\"")]
    [InlineData("card.color.title", "\"red\"")]
    [InlineData("card.color.price", "\"#GG2211\"")]
    public void Invalid_layer_styling_is_rejected_before_preset_is_applied(
        string key, string raw)
    {
        Dictionary<string, JsonElement> settings =
            CardArtPresetCodec.ToSettings(new CardArtOptions());
        settings[key] = JsonDocument.Parse(raw).RootElement.Clone();
        Assert.Throws<PrintLayoutException>(() =>
            CardArtPresetCodec.FromSettings(settings));
    }

    [Fact]
    public void Disallowed_layer_names_rejected_when_saved_by_internal_api()
    {
        Assert.Throws<PrintLayoutException>(() =>
            CardArtPresetCodec.ToSettings(new CardArtOptions(
                LayerScales: new Dictionary<string, double>
                {
                    ["arbitrary"] = 3.0
                })));
        Assert.Throws<PrintLayoutException>(() =>
            CardArtPresetCodec.ToSettings(new CardArtOptions(
                LayerColors: new Dictionary<string, string>
                {
                    ["password"] = "#EEAA33"
                })));
    }
}
