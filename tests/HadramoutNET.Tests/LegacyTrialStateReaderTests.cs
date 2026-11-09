using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HadramoutNET.Core.Licensing;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class LegacyTrialStateReaderTests
{
    private const string Machine = "TEST-TRIAL-FINGERPRINT";
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset First = Now.AddDays(-10);

    private static byte[] Protected(DateTimeOffset first, DateTimeOffset last,
        int schema = 2, string fingerprint = Machine, int revision = 0)
    {
        var payload = new Dictionary<string, object?>
        {
            ["schema"] = schema,
            ["first"] = first.ToString("O"),
            ["last"] = last.ToString("O"),
            ["license_id"] = "",
            ["activation_rev"] = revision
        };
        if (schema == 2) payload["machine_id"] = fingerprint;
        byte[] raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        return ProtectedData.Protect(raw, null, DataProtectionScope.LocalMachine);
    }

    private static byte[] ProtectedJson(string rawJson) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(rawJson), null, DataProtectionScope.LocalMachine);

    [Fact]
    public void Merges_original_mirrors_by_oldest_start_newest_last_and_highest_revision()
    {
        byte[] user = Protected(First.AddDays(1), Now.AddHours(-3));
        byte[] registry = Protected(First, Now.AddHours(-2), revision: 3);
        byte[] machine = Protected(First.AddDays(2), Now.AddHours(-1), revision: 1);
        byte[] userHash = SHA256.HashData(user), registryHash = SHA256.HashData(registry);
        var result = LegacyTrialStateReader.ReadMirrors(user, registry, machine, Machine, Now);
        Assert.Equal("ok", result.Status);
        Assert.True(result.HasHistory);
        Assert.True(result.NeedsRepair);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(First, result.Snapshot.First);
        Assert.Equal(Now.AddHours(-1), result.Snapshot.Last);
        Assert.Equal(3, result.Snapshot.ActivationRevision);
        Assert.Equal(userHash, SHA256.HashData(user));
        Assert.Equal(registryHash, SHA256.HashData(registry));
        Assert.Equal("trial", TrialAccessPolicy.Evaluate(result.Snapshot, Now, Machine).Mode);
    }

    [Fact]
    public void Identical_protected_records_are_read_without_writing()
    {
        byte[] original = Protected(First, Now);
        var result = LegacyTrialStateReader.ReadMirrors(original, original, original, Machine, Now);
        Assert.Equal("ok", result.Status);
        Assert.False(result.NeedsRepair);
        Assert.Equal(First, result.Snapshot!.First);
        Assert.Equal(Now, result.Snapshot.Last);
    }

    [Fact]
    public void Empty_is_not_started_but_unreadable_or_wrong_machine_is_never_a_fresh_trial()
    {
        var missing = LegacyTrialStateReader.ReadMirrors(null, null, null, Machine, Now);
        Assert.Equal("not_started", missing.Status);
        Assert.False(missing.HasHistory);
        Assert.Null(missing.Snapshot);
        var corrupt = LegacyTrialStateReader.ReadMirrors([1, 2, 3], [4, 5], null, Machine, Now);
        Assert.Equal("state_invalid", corrupt.Status);
        Assert.True(corrupt.HasHistory);
        Assert.Null(corrupt.Snapshot);
        var wrongMachine = LegacyTrialStateReader.ReadMirrors(
            Protected(First, Now, fingerprint: "WRONG-WINDOWS"), null, null, Machine, Now);
        Assert.Equal("state_invalid", wrongMachine.Status);
        Assert.True(wrongMachine.HasHistory);
        Assert.Null(wrongMachine.Snapshot);
    }

    [Fact]
    public void Valid_mirror_recovers_when_other_copy_is_corrupted_but_does_not_modify_any_file()
    {
        byte[] valid = Protected(First, Now.AddMinutes(-1));
        byte[] invalid = [1, 2, 3];
        byte[] hash = SHA256.HashData(valid);
        var result = LegacyTrialStateReader.ReadMirrors(valid, invalid, null, Machine, Now);
        Assert.Equal("ok", result.Status);
        Assert.True(result.NeedsRepair);
        Assert.Equal(First, result.Snapshot!.First);
        Assert.Equal(hash, SHA256.HashData(valid));
        Assert.Equal([1, 2, 3], invalid);
    }

    [Fact]
    public void Older_schema_one_keeps_original_first_without_new_trial()
    {
        byte[] old = Protected(First, Now.AddMinutes(-1), schema: 1);
        var result = LegacyTrialStateReader.ReadMirrors(old, null, null, Machine, Now);
        Assert.Equal("ok", result.Status);
        Assert.Equal(First, result.Snapshot!.First);
        Assert.Equal(Machine, result.Snapshot.MachineId);
        Assert.Equal(10L * 86400, TrialAccessPolicy.Evaluate(result.Snapshot, Now, Machine).RemainingSeconds);
    }

    [Fact]
    public void Duplicated_fields_or_oversized_payloads_cannot_become_trial_grants()
    {
        byte[] bad = ProtectedJson("{\"schema\":2,\"schema\":1,\"first\":\"2026-10-20T10:00:00+00:00\",\"last\":\"2026-11-09T10:00:00+00:00\",\"license_id\":\"\",\"machine_id\":\"" + Machine + "\"}");
        Assert.Equal("state_invalid",
            LegacyTrialStateReader.ReadMirrors(bad, null, null, Machine, Now).Status);
        Assert.Equal("state_invalid",
            LegacyTrialStateReader.ReadMirrors(new byte[LegacyTrialStateReader.MaxStateBytes + 1], null, null, Machine, Now).Status);
    }

    [Fact]
    public void Read_only_file_paths_do_not_mutate_original_state()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hnet-trial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string user = Path.Combine(folder, "activation-state.bin");
            string machine = Path.Combine(folder, "trial-machine.bin");
            byte[] bytes = Protected(First, Now.AddMinutes(-1));
            File.WriteAllBytes(user, bytes);
            File.WriteAllBytes(machine, bytes);
            byte[] before = SHA256.HashData(File.ReadAllBytes(user));
            var result = LegacyTrialStateReader.ReadFromPaths(user, machine, null, Machine, Now);
            Assert.Equal("ok", result.Status);
            Assert.Equal(First, result.Snapshot!.First);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(user)));
            Assert.Equal(bytes, File.ReadAllBytes(machine));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Windows_identity_hash_matches_original_trial_contract()
    {
        const string guid = "4b3a-test-guid";
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "NIDI-MIKROTIK-CARDS-TRIAL-V2|" + guid)));
        Assert.Equal(expected, LegacyTrialStateReader.HashTrialMachineIdentifier(guid.ToUpperInvariant()));
    }
}
