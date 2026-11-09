using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RouterConnectionHistoryStoreTests
{
    private sealed class TestProtector : IRouterHistoryProtector
    {
        public byte[] Protect(byte[] bytes) => bytes.Select(b => (byte)(b ^ 0xA7)).ToArray();
        public byte[] Unprotect(byte[] bytes) => Protect(bytes);
    }

    private static RouterConnectionProfile Profile(string user = "admin", string password = "secret") =>
        new("192.168.88.1", 8728, user, false, false, false, password);

    [Fact]
    public void Saving_endpoint_without_password_opt_in_does_not_persist_password()
    {
        string root = Path.Combine(Path.GetTempPath(), "net-history-" + Guid.NewGuid());
        try
        {
            var store = new RouterConnectionHistoryStore(root, new TestProtector());
            store.Upsert(Profile(), rememberPassword: false);
            Assert.Equal("", Assert.Single(store.Read()).Password);
            store.Upsert(Profile("guest", "private"), true);
            Assert.Equal("private", store.Read()[0].Password);
            store.Upsert(Profile("guest", "new"), false);
            Assert.Equal(2, store.Read().Count);
            Assert.Equal("", store.Read()[0].Password);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Delete_removes_only_selected_endpoint()
    {
        string root = Path.Combine(Path.GetTempPath(), "net-history-" + Guid.NewGuid());
        try
        {
            var store = new RouterConnectionHistoryStore(root, new TestProtector());
            store.Upsert(Profile("first"), true);
            store.Upsert(Profile("second"), true);
            store.Delete(Profile("first").Key);
            Assert.Equal("second", Assert.Single(store.Read()).Username);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Corrupted_ciphertext_fails_closed_instead_of_becoming_empty_history()
    {
        string root = Path.Combine(Path.GetTempPath(), "net-history-" + Guid.NewGuid());
        try
        {
            var store = new RouterConnectionHistoryStore(root, new TestProtector());
            store.Upsert(Profile(), true);
            File.WriteAllBytes(Path.Combine(root, "HadramoutNET", "connections", "history.dpapi"),
                [0xAB, 0xCD]);
            Assert.ThrowsAny<Exception>(() => store.Read());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Invalid_endpoint_does_not_create_storage()
    {
        string root = Path.Combine(Path.GetTempPath(), "net-history-" + Guid.NewGuid());
        var store = new RouterConnectionHistoryStore(root, new TestProtector());
        Assert.Throws<ArgumentException>(() => store.Upsert(
            Profile() with { Port = 70000 }, true));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Windows_DPAPI_roundtrips_using_same_current_user_without_plaintext()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "net-history-" + Guid.NewGuid());
        try
        {
            new RouterConnectionHistoryStore(root).Upsert(Profile(password: "NeverStoreAsPlaintext123"), true);
            byte[] disk = File.ReadAllBytes(Path.Combine(root, "HadramoutNET", "connections", "history.dpapi"));
            Assert.DoesNotContain("NeverStoreAsPlaintext123", System.Text.Encoding.UTF8.GetString(disk));
            Assert.Equal("NeverStoreAsPlaintext123",
                Assert.Single(new RouterConnectionHistoryStore(root).Read()).Password);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
