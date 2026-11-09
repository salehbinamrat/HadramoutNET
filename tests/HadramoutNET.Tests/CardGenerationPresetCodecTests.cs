using System.Text.Json;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Data;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class CardGenerationPresetCodecTests
{
    [Fact]
    public void Original_Python_generation_keys_roundtrip_All_fields()
    {
        var input = new CardGenerationOptions(28, 12, "abcdef012345",
            Format: "code_password", PasswordLength: 9,
            PasswordCharacters: "abcXYZ", SequenceMode: "000",
            AvoidDuplicate: false, StartSequence: 240, UsernamePrefix: "ab");
        var encoded = CardGenerationPresetCodec.ToSettings(input);
        Assert.Equal("code_password", encoded["create.card_format"].GetString());
        Assert.Equal("ab", encoded["create.username_prefix"].GetString());
        Assert.Equal(9, encoded["create.password_length"].GetInt32());
        Assert.Equal("000", encoded["create.sequence_mode"].GetString());
        Assert.Equal(input, CardGenerationPresetCodec.FromSettings(encoded));
        var card = Assert.Single(VoucherGenerator.Generate(
            input with { Quantity = 1, UsernamePrefix = "abcdefghijkl" }, _ => false));
        Assert.Equal("240", card.Sequence.Length == 3 ? "240" : card.Sequence);
    }

    [Fact]
    public void Historic_NET_preset_keys_are_read_without_erasing_existing_settings()
    {
        var legacy = new Dictionary<string, JsonElement> {
            ["create.quantity"] = JsonSerializer.SerializeToElement("26"),
            ["create.code_length"] = JsonSerializer.SerializeToElement("8"),
            ["create.characters"] = JsonSerializer.SerializeToElement("0123456789"),
            ["create.format"] = JsonSerializer.SerializeToElement("code_only"),
            ["create.profile"] = JsonSerializer.SerializeToElement("weekly")
        };
        var restored = CardGenerationPresetCodec.FromSettings(legacy);
        Assert.Equal(26, restored.Quantity);
        Assert.Equal(8, restored.CodeLength);
        Assert.Equal("0123456789", restored.UsernameCharacters);
        Assert.Equal("code_only", restored.Format);
        Assert.Equal(6, restored.PasswordLength);
        Assert.Equal("", restored.UsernamePrefix);
        Assert.Equal("weekly", legacy["create.profile"].GetString());
    }

    [Theory]
    [InlineData("create.quantity", "\"-1\"")]
    [InlineData("create.code_length", "\"8.1\"")]
    [InlineData("create.password_length", "999")]
    [InlineData("create.start_sequence", "-1")]
    [InlineData("create.card_format", "\"invalid\"")]
    [InlineData("create.username_chars", "\"a b\"")]
    [InlineData("create.password_chars", "\"ab\\u0000\"")]
    [InlineData("create.username_prefix", "\"abcdefghi\"")]
    [InlineData("create.sequence_mode", "\"invalid\"")]
    [InlineData("create.avoid_duplicate", "\"no\"")]
    public void Corrupted_preset_rejected_atomically_before_it_updates_UI(
        string key, string json)
    {
        var preset = CardGenerationPresetCodec.ToSettings(
            new CardGenerationOptions(10, 8, "ab0123"));
        preset[key] = JsonDocument.Parse(json).RootElement.Clone();
        Assert.Throws<CardGenerationException>(() =>
            CardGenerationPresetCodec.FromSettings(preset));
    }

    [Fact]
    public void Two_package_creation_options_survive_restart_independently()
    {
        string root = Path.Combine(Path.GetTempPath(), "hnet-create-opt-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(root, "hadramout-net.db");
            var store = new PackageSettingsStore(file);
            var one = new CardGenerationOptions(12, 6, "abc123",
                Format: "code_password", PasswordLength: 8,
                PasswordCharacters: "ABCD", UsernamePrefix: "a",
                SequenceMode: "00", StartSequence: 44);
            var two = new CardGenerationOptions(54, 8, "0123456789",
                Format: "code_same_password", UsernamePrefix: "22",
                SequenceMode: "0000", StartSequence: 100);
            store.SavePackage("P1", CardGenerationPresetCodec.ToSettings(one));
            store.SavePackage("P2", CardGenerationPresetCodec.ToSettings(two));
            var reopened = new PackageSettingsStore(file);
            var entries = reopened.ListPackages();
            Assert.Equal(one, CardGenerationPresetCodec.FromSettings(
                Assert.Single(entries, p => p.Name == "P1").Settings));
            Assert.Equal(two, CardGenerationPresetCodec.FromSettings(
                Assert.Single(entries, p => p.Name == "P2").Settings));
        }
        finally {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Draft_upload_target_is_immutable_and_does_not_log_credentials()
    {
        var target = VoucherDraftTarget.Create("hotspot", "package-A", "");
        Assert.True(target.Matches("hotspot", "package-A", ""));
        Assert.False(target.Matches("ppp", "package-A", ""));
        Assert.False(target.Matches("hotspot", "package-B", ""));
        Assert.False(target.Matches("hotspot", "package-A", "newOwner"));
        Assert.DoesNotContain("password", target.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_router_target_is_rejected_before_draft_generation()
    {
        Assert.Throws<CardGenerationException>(() =>
            VoucherDraftTarget.Create("ftp", "default", ""));
        Assert.Throws<CardGenerationException>(() =>
            VoucherDraftTarget.Create("hotspot", "", ""));
        Assert.Throws<CardGenerationException>(() =>
            VoucherDraftTarget.Create("userman", "default", "owner\ninvalid"));
    }
}
