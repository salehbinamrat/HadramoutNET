using System.Text;
using HadramoutNET.Core.Exporting;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class CardExportParityTests
{
    private static IReadOnlyDictionary<string, string?> Row(params (string Key, string? Value)[] entries) =>
        entries.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    [Fact]
    public void RouterOs_quotes_password_metacharacters_without_command_injection()
    {
        string input = "a\"$\\\r\n\t\u001f";
        Assert.Equal("\"a\\\"\\$\\\\\\r\\n\\t\\1F\"", CardExports.QuoteRouterOs(input));
    }

    [Fact]
    public void Hotspot_script_uses_original_guard_and_optional_fields()
    {
        var card = Row(("username", "007123"), ("password", "0000"),
            ("server", "all"), ("profile", "day-1"), ("limit_uptime", "1h30m"),
            ("limit_bytes", "1048576"), ("comment", "one"));
        string text = CardExports.ScriptText([card]);
        Assert.StartsWith("# Hadramout Network Cards - RouterOS 6/7\n", text);
        Assert.Contains(":if ([:len [/ip hotspot user find where name=\"007123\"]] = 0) do={", text);
        Assert.Contains("/ip hotspot user add name=\"007123\" password=\"0000\" profile=\"day-1\" limit-uptime=\"1h30m\" limit-bytes-total=1048576 comment=\"one\"", text);
        Assert.DoesNotContain("server=", text);
        Assert.Contains("Voucher already exists; stopped", text);
    }

    [Fact]
    public void Ppp_and_user_manager_6_and_7_use_distinct_commands()
    {
        string ppp = CardExports.ScriptText([Row(("username", "u"), ("password", "p"),
            ("service", "ppp"), ("profile", "silver"), ("comment", "vip"))]);
        Assert.Contains("/ppp secret add service=pppoe name=\"u\" password=\"p\" profile=\"silver\" comment=\"vip\"", ppp);
        Assert.Contains("/ppp secret find where name=\"u\"", ppp);

        string v6 = CardExports.ScriptText([Row(("username", "u6"), ("password", "p"),
            ("service", "userman"), ("um_version", "6"), ("customer", "owner"), ("profile", "daily"))]);
        Assert.Contains("/tool user-manager user find where username=\"u6\"", v6);
        Assert.Contains("/tool user-manager user create-and-activate-profile numbers=$voucherId customer=\"owner\" profile=\"daily\"", v6);
        Assert.Contains("[:len $voucherId] != 1", v6);

        string v7 = CardExports.ScriptText([Row(("username", "u7"), ("password", "p"),
            ("service", "userman"), ("um_version", "7"), ("profile", "daily"))]);
        Assert.Contains("/user-manager user add name=\"u7\" password=\"p\"", v7);
        Assert.Contains("/user-manager user-profile add user=\"u7\" profile=\"daily\"", v7);
    }

    [Fact]
    public void Unknown_service_and_unresolved_user_manager_6_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => CardExports.ScriptText([
            Row(("username", "u"), ("service", "telnet"))
        ]));
        Assert.Throws<ArgumentException>(() => CardExports.ScriptText([
            Row(("username", "u"), ("service", "userman"), ("um_version", "6"))
        ]));
    }

    [Fact]
    public void Duplicate_and_blank_usernames_are_rejected_before_any_output_is_created()
    {
        Assert.Throws<ArgumentException>(() => CardExports.ScriptText([
            Row(("username", "u")), Row(("username", "u"))
        ]));
        Assert.Throws<ArgumentException>(() => CardExports.ScriptText([Row(("username", "  "))]));
    }

    [Fact]
    public void Csv_preserves_leading_zero_credentials_and_neutralizes_formulas()
    {
        string csv = CardExports.CsvText([Row(("sequence", "0001"),
            ("username", "007123"), ("password", "=2+2"), ("comment", "\tmalicious"))]);
        Assert.StartsWith("sequence,username,password,profile,server,", csv);
        Assert.Contains("0001,007123,'=2+2,", csv);
        Assert.Contains(",'\tmalicious,", csv);
        Assert.EndsWith("\r\n", csv);
    }

    [Fact]
    public void Text_export_keeps_two_columns_and_escapes_comma()
    {
        string content = CardExports.TextCsv([Row(("username", "001"), ("password", "a,b"))]);
        Assert.Equal("001,\"a,b\"\n", content);
    }

    [Fact]
    public void Invalid_script_does_not_overwrite_existing_file()
    {
        string folder = Path.Combine(Path.GetTempPath(), "HadramoutNET-export-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string target = Path.Combine(folder, "cards.rsc");
            File.WriteAllText(target, "old data");
            Assert.Throws<ArgumentException>(() => CardExports.SaveScript([
                Row(("username", "same")), Row(("username", "same"))
            ], target));
            Assert.Equal("old data", File.ReadAllText(target));
            Assert.Single(Directory.GetFiles(folder));
            string written = CardExports.SaveScript([Row(("username", "ok"))], target);
            Assert.Equal(target, written);
            Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(target).Take(3).ToArray());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
