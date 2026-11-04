using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.App;
using Microsoft.Maui.Storage;
using Terraria_Wiki.Services;
using AndroidApplication = Android.App.Application;
using Android.Runtime;
using View = Android.Views.View;
using Color = Android.Graphics.Color;
using Rect = Android.Graphics.Rect;
using Paint = Android.Graphics.Paint;

namespace Terraria_Wiki.Platforms.Android;

/// <summary>
/// 悬浮窗外壳。两个形态:
///   · 球(Ball): 40dp 白球,可拖动,单击展开面板.
///   · 面板(Panel): 顶部/底部一条工具栏 + 内容区,可拖动、可按住下边缘与右下角缩放.
/// 内容区这一版只显示当前页标题,wiki 页面等桥接层做完再接.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public class OverlayService : Service
{
    private const string ChannelId = "overlay_channel";
    private const int NotificationId = 2003;

    // ---- 几何比例(相对屏幕物理像素) ----
    private const float PanelWidthRatio = 0.75f;
    private const float PanelHeightRatio = 0.50f;
    private const float PanelLeftRatio = 0.15f;
    private const float PanelTopRatio = 0.05f;
    private const float BarHeightRatio = 0.05f;
    /// <summary>顶边进入屏幕顶部这一段时,工具栏翻到窗口底部.</summary>
    private const float FlipLineRatio = 0.025f;

    private const int BallSizeDp = 40;
    /// <summary>面板最小尺寸就是球本身: 再小就没法拿捏了, 但不人为加 240x320 那种限制.</summary>
    private const int MinPanelSizeDp = BallSizeDp;
    private const int CornerGripDp = 28;
    private const int EdgeGripDp = 14;
    private const int BallMarginDp = 24;

    // ★ 球和面板的位置必须分开存: 共用一组键时, 收成球会把面板的几何覆盖掉,
    //   下次重启就会拿球的位置去当窗口位置.
    private const string PrefBallX = "Overlay.BallX";
    private const string PrefBallY = "Overlay.BallY";
    private const string PrefPanelX = "Overlay.PanelX";
    private const string PrefPanelY = "Overlay.PanelY";
    private const string PrefPanelW = "Overlay.PanelW";
    private const string PrefPanelH = "Overlay.PanelH";
    private const string PrefBarBottom = "Overlay.BarAtBottom";

    private enum Mode { None, Ball, Panel }

    private Mode _mode;
    private IViewManager? _wm;

    private ImageView? _ballView;
    private WindowManagerLayoutParams? _ballLp;

    private FrameLayout? _panel;
    private FrameLayout? _column;
    private FrameLayout.LayoutParams? _barLp;
    private FrameLayout.LayoutParams? _contentLp;
    private FrameLayout.LayoutParams? _edgeGripLp;
    private FrameLayout.LayoutParams? _cornerGripLp;
    private LinearLayout? _bar;
    /// <summary>宽度不够时按这个顺序依次消失(标题最先), ✕ 不在列表里 = 永不消失.</summary>
    private readonly List<View> _overflowOrder = new();
    /// <summary>最近一次手指按下的屏幕坐标: 收成球时球要落在这里, 不是窗口中心.</summary>
    private int _lastTouchX;
    private int _lastTouchY;
    /// <summary>状态栏高度: 球的 y 至少要让它完全露在状态栏下方, 否则触摸会被状态栏吃掉.</summary>
    private int _topInsetPx;
    private TextView? _titleView;
    private WindowManagerLayoutParams? _panelLp;
    private bool _barAtBottom;

    private int _screenW;
    private int _screenH;
    private float _density;
    private int _barHeightPx;
    private int _panelW;
    private int _panelH;
    private int _ballSizePx;
    private int _minW;
    private int _minH;
    private int _touchSlop;

    private float _downRawX;
    private float _downRawY;
    private int _startX;
    private int _startY;
    private int _startW;
    private int _startH;
    private bool _dragging;

    /// <summary>面板被收成球、但手指还按着(继续拖动条)的状态.</summary>
    private bool _followingPointer;

    private AppState? _appState;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();

        var metrics = Resources!.DisplayMetrics!;
        _screenW = metrics.WidthPixels;
        _screenH = metrics.HeightPixels;
        _density = metrics.Density;
        // 工具栏与按钮都做成球那么大, 不再让 5% 屏高把顶栏撑得比按钮高.
        _barHeightPx = Dp(BallSizeDp);
        _ballSizePx = Dp(BallSizeDp);
        _minW = Dp(MinPanelSizeDp);
        _minH = Dp(MinPanelSizeDp);
        _topInsetPx = GetStatusBarHeight();
        _touchSlop = ViewConfiguration.Get(this)!.ScaledTouchSlop;
        _wm = GetSystemService(WindowService)!.JavaCast<IViewManager>();
        _appState = IPlatformApplication.Current?.Services.GetService<AppState>();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = BuildNotification();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, notification);

        var action = intent?.GetStringExtra("action") ?? "ball";
        if (action == "panel")
        {
            if (_mode == Mode.Ball)
                ExpandFromBall();
            else if (_mode == Mode.None)
                ShowPanelAtStoredGeometry();
        }
        else
        {
            ShowBallAtStoredPosition();
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        RemoveBall();
        RemovePanel();
        AndroidOverlayController.NotifyStopped();
        base.OnDestroy();
    }

    private int Dp(float value) => (int)(value * Resources!.DisplayMetrics!.Density);

    /// <summary>状态栏高度(px)。拿不到就按 24dp 估, 宁可多留一点也不要让球钻进状态栏。</summary>
    private int GetStatusBarHeight()
    {
        var id = Resources!.GetIdentifier("status_bar_height", "dimen", "android");
        return id > 0 ? Resources.GetDimensionPixelSize(id) : Dp(24);
    }

    // ============================================================
    // 通知
    // ============================================================

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
            return;

        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        var channel = new NotificationChannel(
            ChannelId,
            App.Localization?.Get("OverlayService.ChannelName") ?? "Floating window",
            NotificationImportance.Low);
        manager.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification()
    {
        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle(App.Localization?.Get("OverlayService.Title") ?? "Floating window")
            .SetContentText(App.Localization?.Get("OverlayService.Running") ?? "Tap to return to the app")
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetOngoing(true);

        var launch = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        if (launch != null)
        {
            launch.AddFlags(ActivityFlags.NewTask);
            builder.SetContentIntent(PendingIntent.GetActivity(
                this, 0, launch, PendingIntentFlags.Immutable)!);
        }

        return builder.Build();
    }

    // ============================================================
    // 球
    // ============================================================

    private void ShowBallAtStoredPosition()
    {
        if (_mode == Mode.Ball)
            return;

        RemovePanel();

        var storedX = Preferences.Default.Get(PrefBallX, -1.0);
        var x = (int)(storedX * _screenW);
        var y = (int)(Preferences.Default.Get(PrefBallY, -1.0) * _screenH);
        if (storedX < 0)
        {
            x = _screenW - _ballSizePx - Dp(BallMarginDp);
            y = Dp(BallMarginDp);
        }

        ShowBallCore(x, y);
    }

    private void ShowBallCore(int x, int y)
    {
        var ball = new ImageView(this)
        {
            Clickable = true,
            Focusable = false
        };

        var bitmap = BuildBallBitmap(_ballSizePx, _density);
        if (bitmap != null)
            ball.SetImageBitmap(bitmap);

        ball.Touch += OnBallTouch;

        var lp = CreateLayoutParams(
            _ballSizePx, _ballSizePx,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal | WindowManagerFlags.LayoutInScreen);

        // y 至少压到状态栏下沿: 浮在状态栏底下时, 点击会被状态栏先吃掉, 球就再也拖不动了.
        lp.X = Clamp(x, 0, Math.Max(0, _screenW - _ballSizePx));
        lp.Y = Clamp(y, _topInsetPx, Math.Max(_topInsetPx, _screenH - _ballSizePx));

        _wm!.AddView(ball, lp);
        _ballView = ball;
        _ballLp = lp;
        _mode = Mode.Ball;
    }

    private void RemoveBall()
    {
        if (_ballView == null)
            return;

        _ballView.Touch -= OnBallTouch;
        try { _wm?.RemoveView(_ballView); } catch { /* 已被系统收走 */ }
        _ballView = null;
        _ballLp = null;
        if (_mode == Mode.Ball)
            _mode = Mode.None;
    }

    private void OnBallTouch(object? sender, View.TouchEventArgs e)
    {
        var motion = e.Event;
        if (motion == null || _ballLp == null || _ballView == null)
        {
            e.Handled = false;
            return;
        }

        switch (motion.ActionMasked)
        {
            case MotionEventActions.Down:
                _downRawX = motion.RawX;
                _downRawY = motion.RawY;
                _startX = _ballLp.X;
                _startY = _ballLp.Y;
                _dragging = false;
                break;

            case MotionEventActions.Move:
                var dx = (int)(motion.RawX - _downRawX);
                var dy = (int)(motion.RawY - _downRawY);
                if (!_dragging && (Math.Abs(dx) > _touchSlop || Math.Abs(dy) > _touchSlop))
                    _dragging = true;

                if (_dragging)
                {
                    _ballLp.X = Clamp(_startX + dx, 0, Math.Max(0, _screenW - _ballSizePx));
                    _ballLp.Y = Clamp(_startY + dy, _topInsetPx, Math.Max(_topInsetPx, _screenH - _ballSizePx));
                    _wm!.UpdateViewLayout(_ballView, _ballLp);
                }
                break;

            case MotionEventActions.Up:
                if (!_dragging)
                {
                    SaveBallPosition();
                    ExpandFromBall();
                }
                else
                {
                    SaveBallPosition();
                }
                _dragging = false;
                break;

            case MotionEventActions.Cancel:
                _dragging = false;
                break;
        }

        e.Handled = true;
    }

    private void SaveBallPosition()
    {
        if (_ballLp == null || _screenW <= 0 || _screenH <= 0)
            return;

        // 存之前再压一次: 万一屏幕旋转/状态栏高度变化把旧值顶进禁区, 下次读到的仍是有效位置.
        var y = Math.Max(_ballLp.Y, _topInsetPx);
        Preferences.Default.Set(PrefBallX, (double)_ballLp.X / _screenW);
        Preferences.Default.Set(PrefBallY, (double)y / _screenH);
    }

    // ============================================================
    // 面板
    // ============================================================

    private void ShowPanelAtStoredGeometry()
    {
        var wRatio = Preferences.Default.Get(PrefPanelW, (double)PanelWidthRatio);
        var hRatio = Preferences.Default.Get(PrefPanelH, (double)PanelHeightRatio);
        var xRatio = Preferences.Default.Get(PrefPanelX, (double)PanelLeftRatio);
        var yRatio = Preferences.Default.Get(PrefPanelY, (double)PanelTopRatio);

        var w = Clamp((int)(wRatio * _screenW), _minW, _screenW);
        var h = Clamp((int)(hRatio * _screenH), _minH, _screenH);
        var x = Clamp((int)(xRatio * _screenW), 0, Math.Max(0, _screenW - w));
        var y = Clamp((int)(yRatio * _screenH), 0, Math.Max(0, _screenH - h));

        ShowPanelCore(x, y, w, h);
    }

    private void ExpandFromBall()
    {
        if (_mode == Mode.Panel)
            return;

        var centerX = _ballLp == null ? _screenW / 2 : _ballLp.X + _ballSizePx / 2;
        var centerY = _ballLp == null ? _screenH / 2 : _ballLp.Y + _ballSizePx / 2;

        var w = Clamp((int)(Preferences.Default.Get(PrefPanelW, (double)PanelWidthRatio) * _screenW),
            _minW, _screenW);
        var h = Clamp((int)(Preferences.Default.Get(PrefPanelH, (double)PanelHeightRatio) * _screenH),
            _minH, _screenH);

        var (x, y) = AnchorToBall(centerX, centerY, w, h);

        // 锚定后若工具栏不可见(窗口太高),退回默认位置.
        if (!BarVisibleAt(y, h))
        {
            x = Clamp((int)(PanelLeftRatio * _screenW), 0, Math.Max(0, _screenW - w));
            y = Clamp((int)(PanelTopRatio * _screenH), 0, Math.Max(0, _screenH - h));
            _barAtBottom = false;
        }

        RemoveBall();
        ShowPanelCore(x, y, w, h);
    }

    private void ShowPanelCore(int x, int y, int w, int h)
    {
        // ★ 用 FrameLayout 而不是 LinearLayout：工具栏换到顶部/底部只改 LayoutParams.Gravity，
        //   不把 bar 从父容器里摘出来。移除正在接收触摸的 View 会让系统发 CANCEL，
        //   拖动到屏幕顶部那一刻手就被"掐断"，这就是之前翻转打断拖动的原因。
        var column = new FrameLayout(this);
        var bar = BuildBar();
        var content = BuildContent();

        var barLp = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, _barHeightPx)
        {
            Gravity = GravityFlags.Top
        };
        var contentLp = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent)
        {
            TopMargin = _barHeightPx
        };

        // 先加内容再加工具栏：工具栏在内容之上，圆角/内边距才不会被盖住
        column.AddView(content, contentLp);
        column.AddView(bar, barLp);

        var panel = new FrameLayout(this);
        panel.AddView(column, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // 缩放手柄：始终在工具栏的反面（工具栏在上 → 手柄在下；工具栏在下 → 手柄在上）
        var edge = new View(this);
        var corner = new View(this);
        var edgeParams = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(EdgeGripDp))
        {
            Gravity = GravityFlags.Bottom
        };
        var cornerParams = new FrameLayout.LayoutParams(
            Dp(CornerGripDp), Dp(CornerGripDp))
        {
            Gravity = GravityFlags.Bottom | GravityFlags.End
        };
        panel.AddView(edge, edgeParams);
        panel.AddView(corner, cornerParams);

        var lp = CreateLayoutParams(
            w, h,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal
                | WindowManagerFlags.LayoutInScreen | WindowManagerFlags.LayoutNoLimits);
        lp.X = x;
        lp.Y = y;

        _wm!.AddView(panel, lp);

        _panel = panel;
        _column = column;
        _bar = bar;
        _barLp = barLp;
        _contentLp = contentLp;
        _edgeGripLp = edgeParams;
        _cornerGripLp = cornerParams;
        _panelLp = lp;
        _panelW = w;
        _panelH = h;
        _mode = Mode.Panel;

        bar.Touch += OnBarTouch;
        edge.Touch += OnEdgeGripTouch;
        corner.Touch += OnCornerGripTouch;

        var wantBarBottom = y < (int)(_screenH * FlipLineRatio);
        _barAtBottom = !wantBarBottom;          // 取反，让 SetBarAtBottom 一定执行
        SetBarAtBottom(wantBarBottom);
        UpdateBarOverflow(w);
        SavePanelGeometry();
    }

    private LinearLayout BuildBar()
    {
        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var bg = new GradientDrawable();
        bg.SetColor(App.AppStateManager?.IsDarkTheme == true
            ? Color.Argb(240, 32, 32, 34)
            : Color.Argb(245, 250, 250, 250));
        bg.SetCornerRadius(Dp(12));
        bar.Background = bg;
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetPadding(Dp(4), 0, Dp(4), 0);

        _overflowOrder.Clear();
        var themeColor = App.AppStateManager?.IsDarkTheme == true
            ? Color.Argb(255, 230, 230, 230)
            : Color.Argb(255, 40, 40, 40);

        var back = BarButton(global::Terraria_Wiki.Resource.Drawable.ic_overlay_back, themeColor, () => GoBack());
        var home = BarButton(global::Terraria_Wiki.Resource.Drawable.ic_overlay_home, themeColor, () => GoHome());
        var zoomOut = BarButton(global::Terraria_Wiki.Resource.Drawable.ic_overlay_zoom_out, themeColor, () => ChangeZoom(-10));
        var zoomIn = BarButton(global::Terraria_Wiki.Resource.Drawable.ic_overlay_zoom_in, themeColor, () => ChangeZoom(10));
        bar.AddView(back);
        bar.AddView(home);
        bar.AddView(zoomOut);
        bar.AddView(zoomIn);

        var title = new TextView(this)
        {
            Ellipsize = global::Android.Text.TextUtils.TruncateAt.End,
            Gravity = GravityFlags.Center
        };
        title.SetMaxLines(1);
        title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        title.SetTextColor(themeColor);
        title.Text = CurrentPageTitle();
        bar.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _titleView = title;

        var wiki = BarButton(global::Terraria_Wiki.Resource.Drawable.ic_overlay_wiki, themeColor, SwitchWiki);
        var search = BarButton(global::Terraria_Wiki.Resource.Drawable.ic_overlay_search, themeColor, SearchInOverlay);
        var collapse = BarButton(global::Terraria_Wiki.Resource.Drawable.ic_overlay_collapse, themeColor, CollapseToBall);
        var close = BarButton(global::Terraria_Wiki.Resource.Drawable.ic_overlay_close, Color.Argb(255, 220, 80, 80), ExitOverlay);
        bar.AddView(wiki);
        bar.AddView(search);
        bar.AddView(collapse);
        bar.AddView(close);

        // 消失顺序＝添加顺序：+ → − → ⌂ → ‹ → wiki → 搜索 → 收起；✕ 不进来，永存。
        _overflowOrder.Add(zoomIn);
        _overflowOrder.Add(zoomOut);
        _overflowOrder.Add(home);
        _overflowOrder.Add(back);
        _overflowOrder.Add(wiki);
        _overflowOrder.Add(search);
        _overflowOrder.Add(collapse);

        return bar;
    }

    /// <summary>工具栏按钮：真图标（VectorDrawable），不是字符。尺寸与球一致。</summary>
    private ImageView BarButton(int iconRes, Color tint, Action onClick)
    {
        var button = new ImageView(this)
        {
            Clickable = true,
            Focusable = false
        };
        button.SetImageResource(iconRes);
        button.SetScaleType(ImageView.ScaleType.CenterInside);
        button.SetColorFilter(tint);
        button.Click += (_, _) => onClick();

        var lp = new LinearLayout.LayoutParams(Dp(BallSizeDp), ViewGroup.LayoutParams.MatchParent);
        button.LayoutParameters = lp;
        // 图标本身画 20dp，剩下的留给触摸热区
        button.SetPadding(Dp(10), Dp(10), Dp(10), Dp(10));
        return button;
    }

    /// <summary>
    /// 宽度不够时按优先级收起控件：标题 → 左侧(+ − ⌂ ‹) → 右侧(wiki 搜索 收起)，✕ 永不消失。
    /// 每次先全部恢复再收，缩回去放大时能自己长回来。
    /// </summary>
    private void UpdateBarOverflow(int widthPx)
    {
        if (_bar == null)
            return;

        var avail = Math.Max(0, widthPx - Dp(8));
        var unit = Dp(BallSizeDp);

        foreach (var v in _overflowOrder)
            v.Visibility = ViewStates.Visible;
        if (_titleView != null)
            _titleView.Visibility = ViewStates.Visible;

        var used = unit;                                   // ✕ 永远占一格
        foreach (var v in _overflowOrder)
            if (v.Visibility == ViewStates.Visible)
                used += unit;

        // 标题最先让路
        if (_titleView != null && used + unit > avail)
            _titleView.Visibility = ViewStates.Gone;

        foreach (var v in _overflowOrder)
        {
            if (used <= avail)
                break;
            if (v.Visibility != ViewStates.Visible)
                continue;
            v.Visibility = ViewStates.Gone;
            used -= unit;
        }
    }

    private View BuildContent()
    {
        var content = new FrameLayout(this);
        var bg = new GradientDrawable();
        bg.SetColor(App.AppStateManager?.IsDarkTheme == true
            ? Color.Argb(240, 24, 24, 26)
            : Color.Argb(245, 252, 252, 252));
        bg.SetCornerRadius(Dp(12));
        content.Background = bg;
        content.Clickable = true;
        content.Click += (_, _) => ReturnToApp();

        var hint = new TextView(this)
        {
            Text = CurrentPageTitle(),
            Gravity = GravityFlags.Center
        };
        hint.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
        hint.SetTextColor(App.AppStateManager?.IsDarkTheme == true
            ? Color.Argb(255, 200, 200, 200)
            : Color.Argb(255, 90, 90, 90));
        hint.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));
        content.AddView(hint, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Center
        });

        return content;
    }

    private string CurrentPageTitle()
    {
        var title = _appState?.CurrentWikiPage
                    ?? _appState?.ActiveWikiBook?.DefaultPageTitle
                    ?? _appState?.ActiveWikiBook?.Title;
        return string.IsNullOrWhiteSpace(title) ? "" : title;
    }

    private void RemovePanel()
    {
        if (_panel == null)
            return;

        if (_bar != null) _bar.Touch -= OnBarTouch;
        try { _wm?.RemoveView(_panel); } catch { /* 已被系统收走 */ }

        _panel = null;
        _column = null;
        _bar = null;
        _barLp = null;
        _contentLp = null;
        _edgeGripLp = null;
        _cornerGripLp = null;
        _titleView = null;
        _panelLp = null;
        if (_mode == Mode.Panel)
            _mode = Mode.None;
    }

    private void CollapseToBall()
    {
        if (_mode != Mode.Panel || _panelLp == null)
            return;

        SavePanelGeometry();

        // 球落在手指最后按下的位置，而不是窗口中心：收起这个动作发生在工具栏上，
        // 手指就在那里，球出现在手下才跟手。
        int centerX, centerY;
        if (_lastTouchX != 0 || _lastTouchY != 0)
        {
            centerX = _lastTouchX;
            centerY = _lastTouchY;
        }
        else
        {
            centerX = _panelLp.X + _panelLp.Width / 2;
            centerY = _panelLp.Y + _panelLp.Height / 2;
        }

        RemovePanel();
        ShowBallCore(centerX - _ballSizePx / 2, centerY - _ballSizePx / 2);
        SaveBallPosition();
    }

    private void ReturnToApp()
    {
        OpenApp();
        if (_mode == Mode.Panel)
            CollapseToBall();
    }

    private void ExitOverlay()
    {
        StopSelf();
        OpenApp();
    }

    private void OpenApp()
    {
        var intent = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        if (intent == null)
            return;

        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ReorderToFront);
        StartActivity(intent);
    }

    private void GoBack()
    {
        OpenApp();
        _ = AppService.WikiBackAsync();
    }

    private void GoHome()
    {
        OpenApp();
        _ = AppService.WikiBackHomeAsync();
    }

    /// <summary>切换 wiki：先回主界面打开数据管理；窗口内选书要等后台服务化(任务 D)。</summary>
    private void SwitchWiki()
    {
        OpenApp();
        AppService.NavigateTo("data");
    }

    /// <summary>搜索：原地搜索同样要等后台服务化(任务 D)，现在先回主界面，不做假交互。</summary>
    private void SearchInOverlay()
    {
        ReturnToApp();
    }

    private void ChangeZoom(int delta)
    {
        if (_appState == null)
            return;

        _appState.WikiZoom = _appState.WikiZoom + delta;
    }

    // ============================================================
    // 拖动 / 翻转 / 缩放
    // ============================================================

    private void OnBarTouch(object? sender, View.TouchEventArgs e)
    {
        var motion = e.Event;
        if (motion == null)
        {
            e.Handled = false;
            return;
        }

        switch (motion.ActionMasked)
        {
            case MotionEventActions.Down:
                _downRawX = motion.RawX;
                _downRawY = motion.RawY;
                _startX = _panelLp?.X ?? 0;
                _startY = _panelLp?.Y ?? 0;
                _lastTouchX = (int)motion.RawX;
                _lastTouchY = (int)motion.RawY;
                _dragging = false;
                _followingPointer = false;
                break;

            case MotionEventActions.Move:
                HandleBarMove(motion);
                break;

            case MotionEventActions.Up:
                if (!_dragging && !_followingPointer)
                {
                    DispatchBarClick(motion);
                }
                else if (_followingPointer)
                {
                    RemovePanel();
                    SaveBallPosition();
                }
                else if (_mode == Mode.Panel)
                {
                    SavePanelGeometry();
                }

                _dragging = false;
                _followingPointer = false;
                break;

            case MotionEventActions.Cancel:
                _dragging = false;
                _followingPointer = false;
                break;
        }

        e.Handled = true;
    }

    private void HandleBarMove(MotionEvent motion)
    {
        var dx = (int)(motion.RawX - _downRawX);
        var dy = (int)(motion.RawY - _downRawY);

        if (!_dragging && !_followingPointer
            && (Math.Abs(dx) > _touchSlop || Math.Abs(dy) > _touchSlop))
            _dragging = true;

        if (!_dragging && !_followingPointer)
            return;

        if (_mode == Mode.Panel && _panelLp != null && _panel != null)
        {
            var newX = _startX + dx;
            var newY = _startY + dy;

            // 顶边进入翻转线 → 工具栏换到窗口底部
            var wantBottom = newY < (int)(_screenH * FlipLineRatio);
            if (wantBottom != _barAtBottom)
                SetBarAtBottom(wantBottom);

            if (!BarVisibleAt(newY, _panelLp.Height))
            {
                // 翻转后工具栏仍不可见:收成球,球跟着手指
                FollowPointerAsBall((int)motion.RawX, (int)motion.RawY);
                return;
            }

            _panelLp.X = newX;
            _panelLp.Y = newY;
            _wm!.UpdateViewLayout(_panel, _panelLp);
        }
        else if (_followingPointer && _ballLp != null && _ballView != null)
        {
            // 球跟手
            _ballLp.X = Clamp((int)motion.RawX - _ballSizePx / 2, 0,
                Math.Max(0, _screenW - _ballSizePx));
            _ballLp.Y = Clamp((int)motion.RawY - _ballSizePx / 2, 0,
                Math.Max(0, _screenH - _ballSizePx));
            _wm!.UpdateViewLayout(_ballView, _ballLp);

            // 手指回到能让窗口放下的位置 → 自主展开
            if (CanPanelFitAt(_ballLp.X + _ballSizePx / 2, _ballLp.Y + _ballSizePx / 2))
            {
                var centerX = _ballLp.X + _ballSizePx / 2;
                var centerY = _ballLp.Y + _ballSizePx / 2;
                _followingPointer = false;
                RemoveBall();
                ExpandAtAnchor(centerX, centerY);
            }
        }
    }

    private void FollowPointerAsBall(int rawX, int rawY)
    {
        if (_panelLp != null)
        {
            _panelW = _panelLp.Width;
            _panelH = _panelLp.Height;
        }

        // 只隐藏、不移除:移除正在接收触摸的 View 会让系统发 CANCEL,后续 Move 就没了.
        if (_panel != null)
            _panel.Visibility = ViewStates.Invisible;

        ShowBallCore(rawX - _ballSizePx / 2, rawY - _ballSizePx / 2);
        _followingPointer = true;
    }

    private void ExpandAtAnchor(int centerX, int centerY)
    {
        var w = Clamp(_panelW > 0 ? _panelW : (int)(PanelWidthRatio * _screenW), _minW, _screenW);
        var h = Clamp(_panelH > 0 ? _panelH : (int)(PanelHeightRatio * _screenH), _minH, _screenH);
        var (x, y) = AnchorToBall(centerX, centerY, w, h);

        if (!BarVisibleAt(y, h))
            return;

        RemovePanel();
        ShowPanelCore(x, y, w, h);
    }

    private void OnEdgeGripTouch(object? sender, View.TouchEventArgs e)
    {
        HandleGripTouch(e, resizeWidth: false);
    }

    private void OnCornerGripTouch(object? sender, View.TouchEventArgs e)
    {
        HandleGripTouch(e, resizeWidth: true);
    }

    private void HandleGripTouch(View.TouchEventArgs e, bool resizeWidth)
    {
        var motion = e.Event;
        if (motion == null || _panelLp == null || _panel == null)
        {
            e.Handled = false;
            return;
        }

        switch (motion.ActionMasked)
        {
            case MotionEventActions.Down:
                _downRawX = motion.RawX;
                _downRawY = motion.RawY;
                _startW = _panelLp.Width;
                _startH = _panelLp.Height;
                break;

            case MotionEventActions.Move:
                var dw = resizeWidth ? (int)(motion.RawX - _downRawX) : 0;
                var dh = (int)(motion.RawY - _downRawY);

                _panelLp.Width = Clamp(_startW + dw, _minW, _screenW * 2);
                _panelLp.Height = Clamp(_startH + dh, _minH, _screenH * 2);
                _wm!.UpdateViewLayout(_panel, _panelLp);
                UpdateBarOverflow(_panelLp.Width);
                break;

            case MotionEventActions.Up:
                _panelW = _panelLp.Width;
                _panelH = _panelLp.Height;
                SavePanelGeometry();
                break;
        }

        e.Handled = true;
    }

    private void SetBarAtBottom(bool bottom)
    {
        if (_bar == null || _barLp == null || _column == null)
            return;

        // 首次调用(ShowPanelCore 末尾)也走这里，把布局参数一次性摆正
        _barAtBottom = bottom;

        _barLp.Gravity = bottom ? GravityFlags.Bottom : GravityFlags.Top;
        _column.UpdateViewLayout(_bar, _barLp);

        if (_contentLp != null && _column.ChildCount > 0)
        {
            _contentLp.TopMargin = bottom ? 0 : _barHeightPx;
            _contentLp.BottomMargin = bottom ? _barHeightPx : 0;
            _column.UpdateViewLayout(_column.GetChildAt(0), _contentLp);
        }

        // 手柄换到工具栏的反面：工具栏在上手柄在下，反之亦然
        if (_panel != null)
        {
            var gripGravity = bottom ? GravityFlags.Top : GravityFlags.Bottom;
            if (_edgeGripLp != null && _panel.ChildCount > 1)
            {
                _edgeGripLp.Gravity = gripGravity;
                _panel.UpdateViewLayout(_panel.GetChildAt(1), _edgeGripLp);
            }
            if (_cornerGripLp != null && _panel.ChildCount > 2)
            {
                _cornerGripLp.Gravity = gripGravity | GravityFlags.End;
                _panel.UpdateViewLayout(_panel.GetChildAt(2), _cornerGripLp);
            }
        }

        Preferences.Default.Set(PrefBarBottom, bottom);
    }

    /// <summary>工具栏(在顶部或底部)是否还能看见一点.</summary>
    private bool BarVisibleAt(int y, int h)
    {
        var barTop = _barAtBottom ? y + h - _barHeightPx : y;
        var barBottomEdge = barTop + _barHeightPx;
        return barBottomEdge > 0 && barTop < _screenH;
    }

    /// <summary>以球心为锚点算窗口位置:球在九宫格哪一格,窗口就用同一格的那个角/边去对球.</summary>
    private (int X, int Y) AnchorToBall(int centerX, int centerY, int w, int h)
    {
        var col = centerX < _screenW / 3 ? 0 : centerX < _screenW * 2 / 3 ? 1 : 2;
        var row = centerY < _screenH / 3 ? 0 : centerY < _screenH * 2 / 3 ? 1 : 2;

        var x = col switch
        {
            0 => centerX - _ballSizePx / 2,
            1 => centerX - w / 2,
            _ => centerX + _ballSizePx / 2 - w
        };

        var y = row switch
        {
            0 => centerY - _ballSizePx / 2,
            1 => centerY - h / 2,
            _ => centerY + _ballSizePx / 2 - h
        };

        return (Clamp(x, 0, Math.Max(0, _screenW - w)), Clamp(y, 0, Math.Max(0, _screenH - h)));
    }

    private bool CanPanelFitAt(int centerX, int centerY)
    {
        var w = _panelW > 0 ? _panelW : (int)(PanelWidthRatio * _screenW);
        var h = _panelH > 0 ? _panelH : (int)(PanelHeightRatio * _screenH);
        var (_, y) = AnchorToBall(centerX, centerY, w, h);
        return BarVisibleAt(y, h);
    }

    private void SavePanelGeometry()
    {
        if (_panelLp == null || _screenW <= 0 || _screenH <= 0)
            return;

        Preferences.Default.Set(PrefPanelX, (double)_panelLp.X / _screenW);
        Preferences.Default.Set(PrefPanelY, (double)_panelLp.Y / _screenH);
        Preferences.Default.Set(PrefPanelW, (double)_panelLp.Width / _screenW);
        Preferences.Default.Set(PrefPanelH, (double)_panelLp.Height / _screenH);
    }

    private void DispatchBarClick(MotionEvent motion)
    {
        if (_bar == null)
            return;

        var rect = new Rect();
        var px = (int)motion.GetX();
        var py = (int)motion.GetY();

        for (var i = 0; i < _bar.ChildCount; i++)
        {
            var child = _bar.GetChildAt(i);
            if (child == null || !child.Clickable)
                continue;

            child.GetHitRect(rect);
            if (!rect.Contains(px, py))
                continue;

            child.PerformClick();
            return;
        }
    }

    // ============================================================
    // 窗口参数与球图案
    // ============================================================

    private WindowManagerLayoutParams CreateLayoutParams(
        int w, int h, WindowManagerFlags flags)
    {
        var lp = new WindowManagerLayoutParams(
            w, h,
            WindowManagerTypes.ApplicationOverlay,
            flags,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start
        };

        if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
            lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.Always;

        return lp;
    }

    /// <summary>白底圆 + 描边 + 中间的应用图标,比例与 Windows 的 FloatingIconWindow 一致.</summary>
    private Bitmap? BuildBallBitmap(int size, float density)
    {
        var icon = Application?.ApplicationInfo?.LoadIcon(PackageManager);
        if (icon == null)
            return null;

        var bitmap = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!);
        if (bitmap == null)
            return null;

        using var canvas = new Canvas(bitmap);
        using var paint = new Paint(PaintFlags.AntiAlias);
        var radius = size / 2f;

        paint.SetStyle(Paint.Style.Fill);
        paint.Color = Color.White;
        canvas.DrawCircle(radius, radius, radius, paint);

        var inset = (int)(size * 0.16f);
        icon.SetBounds(inset, inset, size - inset, size - inset);
        icon.Draw(canvas);

        paint.SetStyle(Paint.Style.Stroke);
        paint.StrokeWidth = density;
        paint.Color = Color.Argb(30, 0, 0, 0);
        canvas.DrawCircle(radius, radius, radius - density / 2f, paint);

        return bitmap;
    }

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;
}
