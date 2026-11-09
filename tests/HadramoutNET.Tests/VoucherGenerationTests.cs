using HadramoutNET.Core.Cards;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>
/// Original contract: reference/python-v1.1.3/core/cards.py at the pinned source SHA.
/// </summary>
public sealed class VoucherGenerationTests
{
    [Fact]
    public void Code_only_preserves_sequence_and_does_not_invent_password()
    {
        var opts = new CardGenerationOptions(1, 4, "0123456789",
            SequenceMode: "0000", StartSequence: 7, UsernamePrefix: "1234");
        var cards = VoucherGenerator.Generate(opts, _ => false);
        var card = Assert.Single(cards);
        Assert.Equal("0007", card.Sequence);
        Assert.Equal("1234", card.Username);
        Assert.Equal("", card.Password);
    }

    [Fact]
    public void Same_password_mode_matches_generated_username()
    {
        var opts = new CardGenerationOptions(1, 3, "ABC",
            Format: "code_same_password", UsernamePrefix: "XYZ");
        var card = Assert.Single(VoucherGenerator.Generate(opts, _ => false));
        Assert.Equal("xyz", card.Username);
        Assert.Equal(card.Username, card.Password);
    }

    [Fact]
    public void Distinct_password_is_different_even_when_random_initially_matches()
    {
        var opts = new CardGenerationOptions(1, 2, "ab",
            Format: "code_password", PasswordLength: 2, PasswordCharacters: "ab");
        var card = Assert.Single(VoucherGenerator.Generate(opts, _ => false, _ => 0));
        Assert.Equal("aa", card.Username);
        Assert.Equal("ba", card.Password);
    }

    [Fact]
    public void Namespace_exhaustion_is_reported_without_infinite_loop()
    {
        var opts = new CardGenerationOptions(2, 1, "a");
        Assert.Throws<CardGenerationException>(() => VoucherGenerator.Generate(opts, _ => false));
    }

    [Fact]
    public void Existing_usernames_are_never_reused_even_if_avoid_duplicate_is_false()
    {
        var opts = new CardGenerationOptions(1, 1, "a", AvoidDuplicate: false);
        Assert.Throws<CardGenerationException>(() => VoucherGenerator.Generate(opts, name => name == "a"));
    }

    [Fact]
    public void Digit_only_prefix_rejects_english_letters()
    {
        var opts = new CardGenerationOptions(1, 4, "0123456789", UsernamePrefix: "AB");
        Assert.Throws<CardGenerationException>(() => VoucherGenerator.Generate(opts, _ => false));
    }

    [Fact]
    public void Letter_only_prefix_rejects_digits()
    {
        var opts = new CardGenerationOptions(1, 4, "ABC", UsernamePrefix: "a1");
        Assert.Throws<CardGenerationException>(() => VoucherGenerator.Generate(opts, _ => false));
    }

    [Fact]
    public void Whitespace_or_control_characters_in_alphabet_are_rejected()
    {
        var opts = new CardGenerationOptions(1, 2, "a\nb");
        Assert.Throws<CardGenerationException>(() => VoucherGenerator.Generate(opts, _ => false));
    }

    [Fact]
    public void Fixed_password_collision_removes_the_only_possible_username()
    {
        var opts = new CardGenerationOptions(1, 2, "a",
            Format: "code_password", PasswordLength: 2, PasswordCharacters: "a");
        Assert.Throws<CardGenerationException>(() => VoucherGenerator.Generate(opts, _ => false));
    }

    [Fact]
    public void Invalid_card_count_is_rejected()
    {
        var opts = new CardGenerationOptions(10001, 6, "abcdef");
        Assert.Throws<CardGenerationException>(() => VoucherGenerator.Generate(opts, _ => false));
    }
}
