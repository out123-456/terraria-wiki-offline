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
    private const int MinPanelWidthDp = 240;
    private const int MinPanelHeightDp = 320;
    private const int BarButtonWidthDp = 30;
    private const int CornerGripDp = 28;
    private const int EdgeGripDp = 14;
    private const int BallMarginDp = 24;

    private const string PrefXRatio = "Overlay.XRatio";
    private const string PrefYRatio = "Overlay.YRatio";
    private const string PrefWRatio = "Overlay.WRatio";
    private const string PrefHRatio = "Overlay.HRatio";
    private const string PrefBarBottom = "Overlay.BarAtBottom";

    private enum Mode { None, Ball, Panel }

    private Mode _mode;
    private IViewManager? _wm;

    private ImageView? _ballView;
    private WindowManagerLayoutParams? _ballLp;

    private FrameLayout? _panel;
    private LinearLayout? _column;
    private LinearLayout? _bar;
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
        _barHeightPx = Math.Max(Dp(32), (int)(_screenH * BarHeightRatio));
        _ballSizePx = Dp(BallSizeDp);
        _minW = Dp(MinPanelWidthDp);
        _minH = Dp(MinPanelHeightDp);
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

        var x = (int)(Preferences.Default.Get(PrefXRatio, -1.0) * _screenW);
        var y = (int)(Preferences.Default.Get(PrefYRatio, -1.0) * _screenH);
        if (Preferences.Default.Get(PrefXRatio, -1.0) < 0)
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

        lp.X = Clamp(x, 0, Math.Max(0, _screenW - _ballSizePx));
        lp.Y = Clamp(y, 0, Math.Max(0, _screenH - _ballSizePx));

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
                    _ballLp.Y = Clamp(_startY + dy, 0, Math.Max(0, _screenH - _ballSizePx));
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

        Preferences.Default.Set(PrefXRatio, (double)_ballLp.X / _screenW);
        Preferences.Default.Set(PrefYRatio, (double)_ballLp.Y / _screenH);
    }

    // ============================================================
    // 面板
    // ============================================================

    private void ShowPanelAtStoredGeometry()
    {
        var wRatio = Preferences.Default.Get(PrefWRatio, (double)PanelWidthRatio);
        var hRatio = Preferences.Default.Get(PrefHRatio, (double)PanelHeightRatio);
        var xRatio = Preferences.Default.Get(PrefXRatio, (double)PanelLeftRatio);
        var yRatio = Preferences.Default.Get(PrefYRatio, (double)PanelTopRatio);

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

        var w = Clamp((int)(Preferences.Default.Get(PrefWRatio, (double)PanelWidthRatio) * _screenW),
            _minW, _screenW);
        var h = Clamp((int)(Preferences.Default.Get(PrefHRatio, (double)PanelHeightRatio) * _screenH),
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
        var column = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var bar = BuildBar();
        var content = BuildContent();

        column.AddView(bar, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, _barHeightPx));
        column.AddView(content, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var panel = new FrameLayout(this);
        panel.AddView(column, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // 下边缘 / 右下角 两个缩放手柄
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
        _panelLp = lp;
        _panelW = w;
        _panelH = h;
        _mode = Mode.Panel;

        bar.Touch += OnBarTouch;
        edge.Touch += OnEdgeGripTouch;
        corner.Touch += OnCornerGripTouch;

        var wantBarBottom = y < (int)(_screenH * FlipLineRatio);
        _barAtBottom = !wantBarBottom;          // 取反,让 SetBarAtBottom 一定执行
        SetBarAtBottom(wantBarBottom);
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

        bar.AddView(BarButton("‹", () => GoBack()));
        bar.AddView(BarButton("⌂", () => GoHome()));
        bar.AddView(BarButton("−", () => ChangeZoom(-10)));
        bar.AddView(BarButton("+", () => ChangeZoom(10)));

        var title = new TextView(this)
        {
            Ellipsize = global::Android.Text.TextUtils.TruncateAt.End,
            Gravity = GravityFlags.Center
        };
        title.SetMaxLines(1);
        title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        title.SetTextColor(App.AppStateManager?.IsDarkTheme == true
            ? Color.Argb(255, 230, 230, 230)
            : Color.Argb(255, 40, 40, 40));
        title.Text = CurrentPageTitle();
        bar.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _titleView = title;

        bar.AddView(BarButton("⤢", () => ReturnToApp()));
        bar.AddView(BarButton("—", () => CollapseToBall()));
        bar.AddView(BarButton("✕", () => ExitOverlay(), danger: true));

        return bar;
    }

    private TextView BarButton(string label, Action onClick, bool danger = false)
    {
        var button = new TextView(this)
        {
            Text = label,
            Gravity = GravityFlags.Center,
            Clickable = true,
            Focusable = false
        };
        button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        button.SetTextColor(danger
            ? Color.Argb(255, 220, 80, 80)
            : (App.AppStateManager?.IsDarkTheme == true
                ? Color.Argb(255, 230, 230, 230)
                : Color.Argb(255, 40, 40, 40)));

        button.Click += (_, _) => onClick();
        var lp = new LinearLayout.LayoutParams(Dp(BarButtonWidthDp), ViewGroup.LayoutParams.MatchParent);
        button.LayoutParameters = lp;
        return button;
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
        var centerX = _panelLp.X + _panelLp.Width / 2;
        var centerY = _panelLp.Y + _panelLp.Height / 2;

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
        if (_column == null || _bar == null || bottom == _barAtBottom)
            return;

        _barAtBottom = bottom;
        _column.RemoveView(_bar);

        if (!bottom)
            _column.AddView(_bar, 0);
        else
            _column.AddView(_bar);

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

        Preferences.Default.Set(PrefXRatio, (double)_panelLp.X / _screenW);
        Preferences.Default.Set(PrefYRatio, (double)_panelLp.Y / _screenH);
        Preferences.Default.Set(PrefWRatio, (double)_panelLp.Width / _screenW);
        Preferences.Default.Set(PrefHRatio, (double)_panelLp.Height / _screenH);
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
