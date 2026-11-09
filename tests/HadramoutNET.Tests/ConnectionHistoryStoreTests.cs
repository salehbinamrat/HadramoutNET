using System.Runtime.Versioning;
using System.Security.Cryptography;
using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

[SupportedOSPlatform("windows")]
public sealed class ConnectionHistoryStoreTests
{
    private static SavedRouterConnection Connection(int n, string password = "router-secret") =>
        new($"10.20.30.{n}", "8728", "admin", password, "local", false, true);

    private static string Root()
    {
        string root = Path.Combine(Path.GetTempPath(), "hnet-connection-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Reading_missing_history_never_creates_or_mutates_storage()
    {
        string root = Root();
        try
        {
            var store = new ConnectionHistoryStore(root);
            Assert.Empty(store.Load());
            Assert.False(File.Exists(store.ProtectedPath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Persist_only_after_explicit_success_and_remember_password_choice()
    {
        string root = Root();
        try
        {
            var store = new ConnectionHistoryStore(root);
            store.SaveAuthenticated(Connection(1), rememberPassword: true);
            Assert.True(File.Exists(store.ProtectedPath));
            byte[] encrypted = File.ReadAllBytes(store.ProtectedPath);
            Assert.DoesNotContain("router-secret", Convert.ToBase64String(encrypted), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("router-secret", System.Text.Encoding.UTF8.GetString(encrypted), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("router-secret", Assert.Single(new ConnectionHistoryStore(root).Load()).Password);
            store.SaveAuthenticated(Connection(1, "new-secret"), rememberPassword: false);
            var saved = Assert.Single(store.Load());
            Assert.Equal("", saved.Password);
            Assert.Equal("10.20.30.1", saved.Host);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Corrupt_protected_history_fails_closed_and_is_not_overwritten()
    {
        string root = Root();
        try
        {
            var store = new ConnectionHistoryStore(root);
            Directory.CreateDirectory(Path.GetDirectoryName(store.ProtectedPath)!);
            byte[] damaged = RandomNumberGenerator.GetBytes(120);
            File.WriteAllBytes(store.ProtectedPath, damaged);
            Assert.Throws<InvalidDataException>(() => store.Load());
            Assert.Throws<InvalidDataException>(() => store.SaveAuthenticated(Connection(3), true));
            Assert.Equal(damaged, File.ReadAllBytes(store.ProtectedPath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Duplicate_connections_replace_previous_entry_and_history_is_bounded()
    {
        string root = Root();
        try
        {
            var store = new ConnectionHistoryStore(root);
            for (int i = 1; i <= 25; i++)
                store.SaveAuthenticated(Connection(i), false);
            Assert.Equal(20, store.Load().Count);
            store.SaveAuthenticated(Connection(25, "new-secret"), true);
            Assert.Equal(20, store.Load().Count);
            Assert.Equal("new-secret", store.Load()[0].Password);
            store.Forget(store.Load()[0]);
            Assert.DoesNotContain(store.Load(), item => item.Host == "10.20.30.25");
        }
        finally { Directory.Delete(root, true); }
    }
}
