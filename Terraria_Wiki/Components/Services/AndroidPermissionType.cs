namespace Terraria_Wiki.Services;

public enum AndroidPermissionType
{
    Network,
    Notifications,
    Background,

    /// <summary>悬浮窗(显示在其他应用上层)。系统里没有运行时弹窗,只能跳设置页让用户手动开.</summary>
    FloatingWindow
}
