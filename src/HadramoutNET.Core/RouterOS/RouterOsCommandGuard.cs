using System.Text.RegularExpressions;

namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// Fail-closed command restriction copied in intent from core/mikrotik.py.
/// The application is a card/profile manager, never a general RouterOS shell.
/// </summary>
public static class RouterOsCommandGuard
{
    private static readonly Regex CommandPath = new(@"^(?:/[a-z0-9-]+)+$", RegexOptions.CultureInvariant);
    private static readonly Regex SingleId = new(@"^\*[0-9A-Fa-f]+$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AllowedMutations = new(StringComparer.Ordinal)
    {
        "/ip/hotspot/user/add", "/ip/hotspot/user/set", "/ip/hotspot/user/remove",
        "/ip/hotspot/active/remove", "/ip/hotspot/set",
        "/ip/hotspot/user/profile/add", "/ip/hotspot/user/profile/set", "/ip/hotspot/user/profile/remove",
        "/ip/hotspot/ip-binding/add", "/ip/hotspot/ip-binding/remove",
        "/ppp/secret/add", "/ppp/secret/set", "/ppp/secret/remove",
        "/ppp/active/remove",
        "/ppp/profile/add", "/ppp/profile/set", "/ppp/profile/remove",
        "/user-manager/user/add", "/user-manager/user/set", "/user-manager/user/remove",
        "/user-manager/user-profile/add",
        "/user-manager/profile/add", "/user-manager/profile/set", "/user-manager/profile/remove",
        "/user-manager/limitation/add", "/user-manager/limitation/set", "/user-manager/limitation/remove",
        "/user-manager/profile-limitation/add", "/user-manager/profile-limitation/remove",
        "/tool/user-manager/user/add", "/tool/user-manager/user/set", "/tool/user-manager/user/remove",
        "/tool/user-manager/user/create-and-activate-profile",
        "/tool/user-manager/profile/add", "/tool/user-manager/profile/set", "/tool/user-manager/profile/remove",
        "/tool/user-manager/profile/limitation/add", "/tool/user-manager/profile/limitation/set",
        "/tool/user-manager/profile/limitation/remove",
        "/tool/user-manager/profile/profile-limitation/add",
        "/tool/user-manager/profile/profile-limitation/remove"
    };

    private static readonly HashSet<string> ScriptKeys = new(StringComparer.Ordinal)
        { "on-login", "on-logout", "on-up", "on-down", "source" };

    public static void Validate(string path, IReadOnlyDictionary<string, string>? attributes = null,
                                IEnumerable<string>? queries = null)
    {
        string command = (path ?? string.Empty).Trim();
        if (!CommandPath.IsMatch(command)) Deny("Invalid RouterOS command path.");

        attributes ??= new Dictionary<string, string>();
        string[] queryList = queries?.ToArray() ?? [];

        if (command is "/system/reboot" or "/user-manager/database/optimize-db")
        {
            if (attributes.Count != 0 || queryList.Length != 0)
                Deny("Maintenance command may not have parameters.");
            return;
        }

        string action = command[(command.LastIndexOf('/') + 1)..];
        if (command != "/login" && action != "print" && !AllowedMutations.Contains(command))
            Deny("This RouterOS command is outside the allowed card-manager scope.");

        if (action == "print" && attributes.Keys.Any(key => key is not (".proplist" or "count-only")))
            Deny("Read command can cause side effects.");

        if (command == "/ip/hotspot/set")
        {
            if (attributes.Count != 2 || !attributes.ContainsKey(".id") ||
                !attributes.TryGetValue("disabled", out string? disabled) ||
                (disabled != "yes" && disabled != "no"))
                Deny("Only the disabled state of one hotspot server may be changed.");
        }

        if (action is "set" or "remove")
        {
            if (!attributes.TryGetValue(".id", out string? id) || !SingleId.IsMatch(id))
                Deny("A single current RouterOS record ID is required.");
        }

        if (command == "/tool/user-manager/user/create-and-activate-profile" &&
            (!attributes.TryGetValue("numbers", out string? numbers) || !SingleId.IsMatch(numbers)))
            Deny("A single User Manager account ID is required.");

        if (attributes.Keys.Any(ScriptKeys.Contains))
            Deny("Writing RouterOS scripts is forbidden.");

        if (queryList.Any(q => action != "print" || !q.StartsWith("?", StringComparison.Ordinal)))
            Deny("Invalid read-only RouterOS query.");
    }

    private static void Deny(string message) => throw new InvalidOperationException(message);
}
