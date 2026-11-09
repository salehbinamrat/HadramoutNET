using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Exporting;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class VoucherExportRowsTests
{
    [Fact]
    public void Export_keeps_original_batch_metadata_instead_of_empty_quota_columns()
    {
        var vouchers = new[] { new Voucher("0001", "user001", "pw001",
            Price: 25.75m, LimitBytes: 536870912, LimitUptime: "1h30m",
            ValidityDays: 7, CreatedAt: "2026-11-09T18:45:00") };
        var row = Assert.Single(VoucherExportRows.Build(vouchers, "hotspot", "gold",
            "", 0, false));
        Assert.Equal("536870912", row["limit_bytes"]);
        Assert.Equal("1h30m", row["limit_uptime"]);
        Assert.Equal("7", row["validity_days"]);
        Assert.Equal("25.75", row["price"]);
        Assert.Equal("2026-11-09T18:45:00", row["created_at"]);
        Assert.Contains("536870912", CardExports.CsvText(new[] { row }));
        Assert.Contains("limit-bytes-total=536870912", CardExports.ScriptText(new[] { row }));
        Assert.Contains("limit-uptime=\"1h30m\"", CardExports.ScriptText(new[] { row }));
    }

    [Fact]
    public void Export_does_not_mutate_immutable_vouchers_or_lose_original_profile()
    {
        var cards = new[] { new Voucher("0001", "user001", "pw001") };
        var row = Assert.Single(VoucherExportRows.Build(cards, "hotspot", "old-profile",
            "", 0, true));
        Assert.Equal("old-profile", row["profile"]);
        Assert.Equal("published", row["status"]);
        Assert.Equal("user001", cards[0].Username);
    }

    [Fact]
    public void Unknown_user_manager_version_is_rejected_before_script_export()
    {
        var cards = new[] { new Voucher("0001", "user001", "pw001") };
        Assert.Throws<ArgumentException>(() => VoucherExportRows.Build(cards, "userman",
            "basic", "admin", 0, false));
    }
}
