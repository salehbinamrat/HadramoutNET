using HadramoutNET.Core.Cards;
using Xunit;

namespace HadramoutNET.Tests;

public class VoucherCreationPresentationTests
{
    [Theory]
    [InlineData("code_only", "username_only", "code_password")]
    [InlineData("code_same_password", "username_password", "code_same_password")]
    [InlineData("code_password", "username_password", "code_password")]
    public void Existing_card_formats_have_exact_original_two_step_presentation(
        string format, string expectedMode, string expectedRelation)
    {
        var presentation = VoucherCreationPresentation.FromStoredFormat(format);
        Assert.Equal(expectedMode, presentation.Mode);
        Assert.Equal(expectedRelation, presentation.LastPasswordRelation);
    }

    [Fact]
    public void Switching_to_username_only_and_back_restores_last_selected_relation()
    {
        Assert.Equal("code_only", VoucherCreationPresentation.ResolveFormat(
            "username_only", "code_same_password"));
        Assert.Equal("code_same_password", VoucherCreationPresentation.ResolveFormat(
            "username_password", "code_same_password"));
        Assert.Equal("code_password", VoucherCreationPresentation.ResolveFormat(
            "username_password", "code_password"));
    }

    [Theory]
    [InlineData("digits", "0123456789")]
    [InlineData("letters", "abcdefghijklmnopqrstuvwxyz")]
    [InlineData("mixed", "0123456789abcdefghijklmnopqrstuvwxyz")]
    public void Original_charset_modes_have_english_digits_and_lowercase_letters(
        string mode, string expected)
    {
        Assert.Equal(expected, VoucherCreationPresentation.CharactersForMode(mode));
        Assert.Equal(mode, VoucherCreationPresentation.DetectCharacterMode(expected));
    }

    [Fact]
    public void Custom_charset_is_not_rewritten_when_a_package_is_loaded()
    {
        Assert.Equal("custom",
            VoucherCreationPresentation.DetectCharacterMode("abcdef0123"));
        Assert.Equal("custom",
            VoucherCreationPresentation.DetectCharacterMode("خاص123"));
        Assert.Throws<ArgumentException>(() =>
            VoucherCreationPresentation.CharactersForMode("custom"));
    }

    [Theory]
    [InlineData("bad", "code_password")]
    [InlineData("username_password", "bad")]
    public void Invalid_modes_are_rejected_instead_of_falling_back_silently(
        string mode, string relation)
    {
        Assert.Throws<CardGenerationException>(() =>
            VoucherCreationPresentation.ResolveFormat(mode, relation));
    }
}
