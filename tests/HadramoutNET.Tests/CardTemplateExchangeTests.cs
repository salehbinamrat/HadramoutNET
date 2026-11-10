using System.Text;
using System.Text.Json;
using HadramoutNET.Core.Printing;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class CardTemplateExchangeTests
{
    [Fact]
    public void Export_import_roundtrip_preserves_python_keys_and_red_visual_settings()
    {
        var style = new CardArtOptions(ShowTitle: true, Title: "حضرموت",
            ShowQr: true, QrMode: "username", QrSizeMm: 5,
            FontSize: 17, FontColor: "#990011", UsernameColor: "#010203",
            BorderRadius: 8, BackgroundMode: "crop");
        byte[] bytes = CardTemplateExchange.Export(style);
        Assert.InRange(bytes.Length, 1, CardTemplateExchange.MaxJsonBytes);
        string json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"card.title_text\"", json);
        Assert.DoesNotContain("\"create.profile\"", json);
        var result = CardTemplateExchange.Import(bytes);
        Assert.Equal(style.Title, result.Artwork.Title);
        Assert.Equal(style.QrMode, result.Artwork.QrMode);
        Assert.Equal(style.FontColor, result.Artwork.FontColor);
        Assert.Equal(style.BorderRadius, result.Artwork.BorderRadius);
        Assert.Equal("crop", result.Artwork.BackgroundMode);
        Assert.Empty(result.UnrecognizedCardSettings);
    }

    [Fact]
    public void Imported_unsupported_original_card_attributes_are_reported_and_preserved()
    {
        const string json = """
            {"card.title_text":"شبكة حضرموت","card.add_title":true,
             "card.size.username":1.4,"card.dpi":300,"card.font_family":"Arial",
             "create.profile":"NEVER-IMPORT-ROUTER-DATA"}
            """;
        var imported = CardTemplateExchange.Import(Encoding.UTF8.GetBytes(json));
        Assert.True(imported.Artwork.ShowTitle);
        Assert.Equal("شبكة حضرموت", imported.Artwork.Title);
        // card.font_family is now a supported original property.
        // card.dpi remains unsupported and must be preserved, not rendered.
        Assert.Single(imported.UnrecognizedCardSettings);
        Assert.Equal("Arial", imported.Artwork.FontFamily);
        Assert.True(imported.UnrecognizedCardSettings.ContainsKey("card.dpi"));
        Assert.Equal(1.4, imported.Artwork.LayerScales!["username"]);
        Assert.DoesNotContain("create.profile", imported.UnrecognizedCardSettings.Keys);
        byte[] reexport = CardTemplateExchange.Export(
            imported.Artwork, imported.UnrecognizedCardSettings);
        var again = CardTemplateExchange.Import(reexport);
        Assert.Equal(imported.UnrecognizedCardSettings.Count,
            again.UnrecognizedCardSettings.Count);
        Assert.Equal(1.4, again.Artwork.LayerScales!["username"]);
        Assert.Equal("Arial", again.Artwork.FontFamily);
        Assert.Equal(300, again.UnrecognizedCardSettings["card.dpi"].GetInt32());
    }

    [Theory]
    [InlineData("{\"print.rows\":10}")]
    [InlineData("[]")]
    [InlineData("{\"card.background_path\":\"\\\\server\\share\\image.png\"}")]
    [InlineData("{\"card.background_path\":\"C:\\\\Users\\\\Public\\\\card.png\"}")]
    [InlineData("{\"card.positions\":{\"username\":[-9,2]}}")]
    [InlineData("{\"card.title_text\":123}")]
    [InlineData("{\"card.add_qr\":\"true\"}")]
    public void Invalid_unsafe_or_nontemplate_JSON_rejected_without_changing_existing_art(
        string json)
    {
        Assert.Throws<PrintLayoutException>(() =>
            CardTemplateExchange.Import(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Unreasonably_large_JSON_is_rejected_before_parsing_or_allocation()
    {
        byte[] bad = new byte[CardTemplateExchange.MaxJsonBytes + 1];
        Assert.Throws<PrintLayoutException>(() => CardTemplateExchange.Import(bad));
    }

    [Fact]
    public void Unknown_card_setting_cannot_override_supported_verified_setting()
    {
        var fake = new Dictionary<string, JsonElement>
        {
            ["card.font_size"] = JsonSerializer.SerializeToElement(999),
        };
        Assert.Throws<PrintLayoutException>(() =>
            CardTemplateExchange.Export(new CardArtOptions(), fake));
    }

    [Fact]
    public void Atomic_template_export_preserves_existing_file_if_validation_fails()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "hnet-template-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string destination = Path.Combine(directory, "design.json");
            File.WriteAllText(destination, "original-design");
            Assert.Throws<PrintLayoutException>(() =>
                CardTemplateExchange.SaveFile(destination,
                    new CardArtOptions(FontColor: "INVALID")));
            Assert.Equal("original-design", File.ReadAllText(destination));
            string saved = CardTemplateExchange.SaveFile(destination,
                new CardArtOptions(Title: "شبكة حضرموت"));
            Assert.Equal(destination, saved);
            Assert.Equal("شبكة حضرموت", CardTemplateExchange.Import(
                File.ReadAllBytes(destination)).Artwork.Title);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void UTF8_BOM_Python_template_is_accepted_without_current_state_mutation()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"card.title_text\":\"صالح\"}");
        byte[] bytes = [0xEF,0xBB,0xBF, ..payload];
        var loaded = CardTemplateExchange.Import(bytes);
        Assert.Equal("صالح", loaded.Artwork.Title);
    }
}
