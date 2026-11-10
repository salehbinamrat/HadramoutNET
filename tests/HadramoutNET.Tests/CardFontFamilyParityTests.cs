using HadramoutNET.Core.Printing;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class CardFontFamilyParityTests
{
    [Theory]
    [InlineData("Arial")]
    [InlineData("Tahoma")]
    public void Source_card_font_family_roundtrips_through_package_preset(string family)
    {
        var artwork = new CardArtOptions(FontFamily: family);
        var saved = CardArtPresetCodec.ToSettings(artwork);
        Assert.Equal(family, saved["card.font_family"].GetString());
        var loaded = CardArtPresetCodec.FromSettings(saved);
        Assert.Equal(family, loaded.FontFamily);
    }

    [Fact]
    public void Older_preset_without_font_family_stays_arabic_windows_arial_default()
    {
        var saved = CardArtPresetCodec.ToSettings(new CardArtOptions());
        saved.Remove("card.font_family");
        Assert.Equal("Arial", CardArtPresetCodec.FromSettings(saved).FontFamily);
    }

    [Theory]
    [InlineData("Courier New")]
    [InlineData("")]
    [InlineData("../../Fonts")]
    public void Unknown_or_unsafe_font_family_is_rejected_before_pdf_or_print(string family)
    {
        Assert.Throws<PrintLayoutException>(() =>
            CardArtPresetCodec.ToSettings(new CardArtOptions(FontFamily: family)));
    }
}
