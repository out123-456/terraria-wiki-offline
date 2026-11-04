using Android.Content;
using Android.OS;
using AndroidApplication = Android.App.Application;

namespace Terraria_Wiki.Services;

/// <summary>
/// 悬浮窗的开关入口。界面层只碰这里,不直接碰 Service.
/// </summary>
public static class AndroidOverlayController
{
    private const string ActionExtra = "action";

    /// <summary>当前是否在运行.由 Start/CloseAll 与 Service 的 OnDestroy 共同维护.</summary>
    public static bool IsRunning { get; private set; }

    /// <summary>
    /// 状态变化(开始/停止)。界面层订阅后刷新按钮，
    /// 否则在悬浮球那边关掉窗口，主界面这里还显示"已开启"。
    /// </summary>
    public static event Action? RunningChanged;

    private static void SetRunning(bool value)
    {
        if (IsRunning == value)
            return;
        IsRunning = value;
        RunningChanged?.Invoke();
    }

    /// <summary>显示悬浮球(已显示则什么都不做).</summary>
    public static void ShowBall() => Start("ball");

    /// <summary>直接展开成面板(球会被换成面板).</summary>
    public static void Expand() => Start("panel");

    /// <summary>彻底关掉悬浮窗,不碰主界面.</summary>
    public static void CloseAll()
    {
        var context = AndroidApplication.Context;
        context.StopService(new Intent(context, typeof(Terraria_Wiki.Platforms.Android.OverlayService)));
        SetRunning(false);
    }

    private static void Start(string action)
    {
        if (!AndroidPermissionService.IsGranted(AndroidPermissionType.FloatingWindow))
        {
            SetRunning(false);
            return;
        }

        var context = AndroidApplication.Context;
        var intent = new Intent(context, typeof(Terraria_Wiki.Platforms.Android.OverlayService));
        intent.PutExtra(ActionExtra, action);

        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(intent);
            else
                context.StartService(intent);

            SetRunning(true);
        }
        catch (System.Exception ex)
        {
            // 后台启动被系统拒绝、缺权限之类的情况,不能让界面层直接崩.
            Android.Util.Log.Warn("TerrariaWiki", "overlay start failed: " + ex);
            SetRunning(false);
        }
    }

    internal static void NotifyStopped() => SetRunning(false);
}
