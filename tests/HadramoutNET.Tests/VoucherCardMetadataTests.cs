using System.Globalization;
using System.Text.Json;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Data;
using HadramoutNET.Core.Printing;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class VoucherCardMetadataTests
{
    [Fact]
    public void Attaching_original_card_fields_preserves_credentials_and_renders_real_values()
    {
        var baseCards = VoucherGenerator.Generate(
            new CardGenerationOptions(1, 8, "0123456789", UsernamePrefix: "12345678"),
            _ => false);
        var original = Assert.Single(baseCards);
        var options = new VoucherCardMetadataOptions(
            Price: 10.5m, Capacity: 512m, CapacityUnit: "MB",
            Hours: 1, Minutes: 30, ValidityDays: 7);
        DateTimeOffset time = new(2026, 11, 9, 18, 45, 0, TimeSpan.FromHours(3));
        Voucher annotated = Assert.Single(VoucherCardMetadata.Attach(baseCards, options, time));
        Assert.Equal(original.Username, annotated.Username);
        Assert.Equal(original.Password, annotated.Password);
        Assert.Equal("1h30m", annotated.LimitUptime);
        Assert.Equal(512L * 1024 * 1024, annotated.LimitBytes);
        Assert.Equal(10.5m, annotated.Price);
        Assert.Equal(7, annotated.ValidityDays);
        Assert.StartsWith("2026-11-09", annotated.CreatedAt);

        var art = CardArtwork.Create(annotated, 50, 19,
            new CardArtOptions(ShowPrice: true, ShowCapacity: true,
                ShowDuration: true, ShowValidity: true, ShowDate: true));
        Assert.Contains(art.Texts, x => x.Key == "price" && x.Value == "10.5");
        Assert.Contains(art.Texts, x => x.Key == "capacity" && x.Value == "512 MB");
        Assert.Contains(art.Texts, x => x.Key == "duration" && x.Value == "1h30m");
        Assert.Contains(art.Texts, x => x.Key == "validity" && x.Value == "7 يوم");
        Assert.Contains(art.Texts, x => x.Key == "date" && x.Value == "2026-11-09");
    }

    [Theory]
    [InlineData(0, 0, "")]
    [InlineData(0, 90, "1h30m")]
    [InlineData(2, 120, "4h")]
    [InlineData(24, 0, "24h")]
    public void Uptime_normalization_matches_original_Python_cards(
        int hours, int minutes, string expected) =>
        Assert.Equal(expected, VoucherCardMetadata.UptimeString(hours, minutes));

    [Theory]
    [InlineData(0, "B", "0.0 B")]
    [InlineData(512, "MB", "512 MB")]
    [InlineData(1.5, "GB", "1.5 GB")]
    public void Capacity_display_matches_original_binary_unit_scaling(
        double qty, string unit, string expected)
    {
        var byteCount = VoucherCardMetadata.BytesTotal((decimal)qty, unit);
        Assert.Equal(expected, VoucherCardMetadata.ReadableBytes(byteCount));
    }

    [Fact]
    public void Source_Python_card_metadata_preset_persists_after_restart()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "hnet-metadata-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(root, "hadramout-net.db");
            var original = new VoucherCardMetadataOptions(
                Price: 185.75m, Capacity: 2.5m, CapacityUnit: "GB",
                Hours: 3, Minutes: 12, ValidityDays: 14);
            var settings = VoucherCardMetadataPresetCodec.ToSettings(original);
            Assert.Equal(185.75m, settings["create.price"].GetDecimal());
            Assert.Equal("GB", settings["create.limit_unit"].GetString());
            var store = new PackageSettingsStore(file);
            store.SavePackage("profileA", settings);
            store.SavePackage("profileB", VoucherCardMetadataPresetCodec.ToSettings(
                new VoucherCardMetadataOptions()));
            var restarted = new PackageSettingsStore(file);
            var packages = restarted.ListPackages();
            Assert.Equal(original, VoucherCardMetadataPresetCodec.FromSettings(
                Assert.Single(packages, p => p.Name == "profileA").Settings));
            Assert.Equal(new VoucherCardMetadataOptions(),
                VoucherCardMetadataPresetCodec.FromSettings(
                Assert.Single(packages, p => p.Name == "profileB").Settings));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("create.price", "-1")]
    [InlineData("create.price", "\"NaN\"")]
    [InlineData("create.limit_bytes", "-4")]
    [InlineData("create.limit_unit", "\"PB\"")]
    [InlineData("create.limit_uptime_h", "-1")]
    [InlineData("create.limit_uptime_m", "\"17.2\"")]
    [InlineData("create.validity_days", "-5")]
    [InlineData("create.validity_days", "null")]
    public void Invalid_or_unknown_package_limits_are_rejected_before_generating_cards(
        string key, string json)
    {
        var settings = VoucherCardMetadataPresetCodec.ToSettings(
            new VoucherCardMetadataOptions());
        settings[key] = JsonDocument.Parse(json).RootElement.Clone();
        Assert.Throws<CardGenerationException>(() =>
            VoucherCardMetadataPresetCodec.FromSettings(settings));
    }

    [Fact]
    public void Extremely_large_capacity_is_rejected_instead_of_overflowing()
    {
        Assert.Throws<CardGenerationException>(() => VoucherCardMetadata.BytesTotal(
            9_000_000_000_000m, "TB"));
    }
}
