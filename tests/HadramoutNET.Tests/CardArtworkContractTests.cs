using System.Text.Json;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Printing;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class CardArtworkContractTests
{
    private static readonly Voucher Card = new("0001", "4172816", "123456");

    [Fact]
    public void Python_default_card_art_hides_optional_layers_but_renders_required_credentials()
    {
        var scene = CardArtwork.Create(Card, 50, 19, new CardArtOptions());
        Assert.Equal(new[] { "username", "password" },
            scene.Texts.Select(x => x.Key).ToArray());
        Assert.Null(scene.Qr);
        Assert.Equal("#7D0F24", scene.Accent);
        Assert.All(scene.Texts, t => {
            Assert.InRange(t.X, 0, scene.Width);
            Assert.InRange(t.Y, 0, scene.Height);
            Assert.True(t.X + t.Width <= scene.Width + 0.01);
            Assert.True(t.Y + t.Height <= scene.Height + 0.01);
        });
    }

    [Fact]
    public void Optional_title_sequence_phone_and_notes_are_shared_scene_layers()
    {
        var config = new CardArtOptions(
            ShowTitle: true, ShowSequence: true,
            ShowPhone: true, ShowOther: true, ShowOther2: true,
            Title: "شبكة حضرموت", Phone: "777123456",
            Other: "العرض", Other2: "ثابت");
        var scene = CardArtwork.Create(Card, 50, 19, config);
        Assert.Equal(new[] { "title", "sequence", "username", "password",
                "phone", "other", "other2" },
            scene.Texts.Select(x => x.Key).ToArray());
        Assert.Contains(scene.Texts, t => t.Key == "sequence" && t.Value == "#0001");
        Assert.Contains(scene.Texts, t => t.Key == "title" && t.Value == "شبكة حضرموت");
    }

    [Theory]
    [InlineData("credentials", "4172816|123456")]
    [InlineData("username", "4172816")]
    public void Qr_payload_matches_original_python_options_without_logging_passwords(
        string mode, string expected)
    {
        var options = new CardArtOptions(ShowQr: true, QrMode: mode, QrSizeMm: 6,
            Positions: new Dictionary<string, CardArtPoint> {
                ["qr"] = new(12, 70)
            });
        var scene = CardArtwork.Create(Card, 50, 19, options);
        Assert.NotNull(scene.Qr);
        Assert.Equal(expected, scene.Qr!.Payload);
        Assert.True(scene.Qr.Modules.GetLength(0) >= 21);
        Assert.Equal(scene.Qr.Modules.GetLength(0), scene.Qr.Modules.GetLength(1));
        Assert.Contains(scene.Qr.Modules.Cast<bool>(), m => m);
    }

    [Fact]
    public void Qr_login_url_percent_encodes_username_and_password_like_python()
    {
        var voucher = new Voucher("1", "a+b &", "p/ #");
        var url = CardArtwork.QrPayload(new CardArtOptions(QrMode: "login_url",
            QrLoginUrl: "http://hotspot.local/login?src=one"), voucher);
        Assert.Contains("src=one", url);
        Assert.Contains("username=a%2Bb%20%26", url);
        Assert.Contains("password=p%2F%20%23", url);
        Assert.DoesNotContain("p/ #", url);
    }

    [Fact]
    public void Per_package_card_positions_and_flags_survive_JSON_roundtrip()
    {
        var config = new CardArtOptions(
            ShowTitle: true, ShowSequence: true, ShowQr: true,
            Title: "حضرموت الحمراء", QrMode: "username", QrSizeMm: 5,
            Positions: new Dictionary<string, CardArtPoint> {
                ["username"] = new(35, 12), ["password"] = new(35, 70),
                ["qr"] = new(220, 75)
            });
        var map = CardArtPresetCodec.ToSettings(config);
        Assert.All(map.Keys, k => Assert.StartsWith("card.", k));
        Assert.Equal(config.Title, CardArtPresetCodec.FromSettings(map).Title);
        var loaded = CardArtPresetCodec.FromSettings(map);
        Assert.True(loaded.ShowTitle);
        Assert.True(loaded.ShowQr);
        Assert.Equal("username", loaded.QrMode);
        Assert.Equal(new CardArtPoint(35, 12), loaded.Positions!["username"]);
        Assert.Equal(new CardArtPoint(220, 75), loaded.Positions["qr"]);
    }

    [Theory]
    [InlineData("card.qr_mode", "\"bad\"")]
    [InlineData("card.qr_size_mm", "\"NaN\"")]
    [InlineData("card.qr_size_mm", "0")]
    [InlineData("card.title_text", "null")]
    [InlineData("card.positions", "[]")]
    [InlineData("card.positions", "{\"username\":[-1,0]}")]
    [InlineData("card.positions", "{\"password\":[20,999]}")]
    [InlineData("card.add_qr", "\"yes\"")]
    public void Invalid_or_malformed_preset_rejected_before_printing(
        string key, string json)
    {
        var settings = new Dictionary<string, JsonElement>
        {
            [key] = JsonDocument.Parse(json).RootElement.Clone()
        };
        Assert.Throws<PrintLayoutException>(() =>
            CardArtPresetCodec.FromSettings(settings));
    }

    [Fact]
    public void Overlapping_username_and_password_or_QR_are_rejected_before_output()
    {
        var badText = new CardArtOptions(Positions: new Dictionary<string, CardArtPoint> {
            ["username"] = new(80, 35), ["password"] = new(85, 35)
        });
        Assert.Throws<PrintLayoutException>(() => CardArtwork.Create(Card, 50, 19, badText));
        var badQr = new CardArtOptions(ShowQr: true, QrSizeMm: 9,
            Positions: new Dictionary<string, CardArtPoint> {
                ["qr"] = new(80, 5)
            });
        Assert.Throws<PrintLayoutException>(() => CardArtwork.Create(Card, 50, 19, badQr));
    }

    [Fact]
    public void Blank_or_oversize_credentials_cannot_produce_printable_scene()
    {
        Assert.Throws<PrintLayoutException>(() =>
            CardArtwork.Create(new Voucher("1", "", ""), 50, 19, new()));
        Assert.Throws<PrintLayoutException>(() =>
            CardArtwork.Create(new Voucher("1", new string('9', 65), ""), 50, 19, new()));
    }
}
