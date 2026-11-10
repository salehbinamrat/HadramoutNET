using HadramoutNET.Core.Cards;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class VoucherDraftSettingsTests
{
    private static CardGenerationOptions Generation() =>
        new(Quantity: 20, CodeLength: 8, UsernameCharacters: "0123456789",
            Format: "code_password", PasswordLength: 6,
            PasswordCharacters: "abcdef0123456789", SequenceMode: "0000",
            StartSequence: 1, UsernamePrefix: "12");

    private static VoucherCardMetadataOptions Metadata() =>
        new(Price: 10.5m, Capacity: 512m, CapacityUnit: "MB",
            Hours: 1, Minutes: 30, ValidityDays: 7);

    [Fact]
    public void Unchanged_generation_and_card_metadata_allow_current_draft()
    {
        VoucherDraftSettings saved = VoucherDraftSettings.Capture(Generation(), Metadata());
        Assert.True(saved.Matches(Generation(), Metadata()));
    }

    [Fact]
    public void Changing_quantity_format_or_credentials_rejects_existing_draft()
    {
        VoucherDraftSettings saved = VoucherDraftSettings.Capture(Generation(), Metadata());
        Assert.False(saved.Matches(Generation() with { Quantity = 21 }, Metadata()));
        Assert.False(saved.Matches(Generation() with { Format = "code_only" }, Metadata()));
        Assert.False(saved.Matches(Generation() with { UsernamePrefix = "13" }, Metadata()));
        Assert.False(saved.Matches(Generation() with { PasswordCharacters = "xyz" }, Metadata()));
        Assert.False(saved.Matches(Generation() with { StartSequence = 2 }, Metadata()));
    }

    [Fact]
    public void Changing_price_capacity_time_or_validity_rejects_existing_draft()
    {
        VoucherDraftSettings saved = VoucherDraftSettings.Capture(Generation(), Metadata());
        Assert.False(saved.Matches(Generation(), Metadata() with { Price = 11m }));
        Assert.False(saved.Matches(Generation(), Metadata() with { Capacity = 1024m }));
        Assert.False(saved.Matches(Generation(), Metadata() with { Hours = 2 }));
        Assert.False(saved.Matches(Generation(), Metadata() with { ValidityDays = 30 }));
    }

    [Fact]
    public void Capturing_invalid_draft_options_fails_without_creating_a_snapshot()
    {
        Assert.Throws<CardGenerationException>(() =>
            VoucherDraftSettings.Capture(Generation() with { Quantity = 0 }, Metadata()));
        Assert.Throws<CardGenerationException>(() =>
            VoucherDraftSettings.Capture(Generation(), Metadata() with { Price = -1 }));
    }
}
