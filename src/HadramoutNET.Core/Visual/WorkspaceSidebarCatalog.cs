namespace HadramoutNET.Core.Visual;

/// <summary>
/// The exact ten core sidebar destinations from pinned Python app.py. A
/// separate immutable catalog keeps ordering, text and icon mapping in one
/// place, rather than ten disconnected WPF button definitions.
/// </summary>
public sealed record WorkspaceSidebarRoute(
    string Key, string Label, string Icon,
    int PrimaryTabIndex, int NetworkTabIndex = -1,
    string LifecycleStatus = "");

public static class WorkspaceSidebarCatalog
{
    public static IReadOnlyList<WorkspaceSidebarRoute> Items { get; } =
    [
        new("dashboard", "الرئيسية", "router_info.png", 0),
        new("packages", "باقات الشبكة", "packages.png", 3),
        new("create", "إنشاء وطباعة الكروت", "create_cards.png", 1),
        new("manage", "إدارة المستخدمين", "manage_cards.png", 2),
        new("active_cards", "الكروت النشطة", "active_cards.png", -1,
            LifecycleStatus: "active"),
        new("expired_cards", "الكروت المنتهية", "expired_cards.png", -1,
            LifecycleStatus: "expired"),
        new("activity", "النشاط", "total_cards.png", 7, 4),
        new("maintenance", "التحكم بالشبكة والصيانة", "maintenance.png", 7, 0),
        new("monitor", "المتصلون والهوتسبوت", "connected_users.png", 7, 3),
        new("network", "الشبكة والتشخيص", "router_info.png", 7, 2)
    ];

    public static WorkspaceSidebarRoute Find(string key) =>
        Items.FirstOrDefault(item => item.Key == key) ??
        throw new ArgumentException("Sidebar route not found.", nameof(key));
}
