using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace BigBlueFish.Windows;
public partial class MainWindow
{
    private CompanionPreferences preferences = null !;
    private CompanionBrain brain = null !;
    private DispatcherTimer companionTimer = null !;
    private CompanionBubble bubble = null !;
    private TextBlock bubbleText = null !;
    private Border loadingIndicator = null !;
    private CompanionLoadingRing loadingSpinner = null !;
    private CompanionSpeech speech = null!;
    private bool bubbleIsModelReply;
    private long speechRequestVersion;
    private Grid companionLayout = null !;
    private Canvas heartsLayer = null!;
    private Window? companionSettings;
    private ContextMenu companionContextMenu = null!;
    private CompanionMenuDismissal? companionMenuDismissal;
    private bool weatherRequestPending;
    private bool speechRequestPending;
    private DateTime nextSpeech, nextAction, bubbleUntil;
    private bool companionReady, companionClosed, dragged, dragging, hiddenByUser, fullscreenHidden;
    private Point dragStart, windowStart, anchor;
    private readonly Random companionRandom = new();
    private DateTime lastFrame = DateTime.UtcNow;
    private double affinityAccumulator;
    // 好感度陪伴/冷淡机制（三状态）：
    // - 用户在场（近 15 分钟有键鼠输入）且距上次点击互动 ≤ 60 分钟 → 「陪伴中」，每 600 秒 +1
    // - 用户在场但超过 60 分钟未互动 → 「冷淡期」，每 60 分钟 -5（首次粉色气泡提醒一次）
    // - 用户离场（无键鼠输入）→ 「冻结」，不涨不扣；回场瞬间视为久别重逢，重置宽限期
    private double affinityDecayTimer;
    private bool affinityDecayNotified;
    private bool wasUserPresent;
    private static readonly TimeSpan AffinityGraceSpan = TimeSpan.FromMinutes(60);   // 点击后宽限期：60 分钟内算「陪伴中」
    private static readonly TimeSpan AffinityCompanionStep = TimeSpan.FromSeconds(600); // 陪伴每 600 秒 +1
    private static readonly TimeSpan AffinityDecayInterval = TimeSpan.FromMinutes(60); // 冷淡期每 60 分钟 -5
    private const double PresenceIdleSeconds = 15 * 60; // 近 15 分钟有键鼠输入 = 用户在场
    // 好感度粉红气泡的正文颜色（配合 CompanionBubble 的粉红主题）。
    private static readonly Color AffinityPinkText = Color.FromRgb(226, 82, 133);
    private DateTime nextWarmup = DateTime.MinValue;
    private bool hookInstalled;
    private DateTime diagnosticAt;
    private readonly IntPtr startupForeground = CompanionNative.GetForegroundWindow();
    private IntPtr lastExternalForeground;
    private IntPtr? speechForegroundOverride;
    private string currentAction = "idle";
    private System.Threading.Mutex? singleInstance;
    private FileStream? instanceLock;

    // 邮课叮：桌宠自身的学业提醒轮询与菜单展示，不依赖壁纸前端。
    private CompanionStudyClient? studyClient;
    private DispatcherTimer? studyTimer;
    private CancellationTokenSource? studyCts;
    private bool studyRequestPending;
    private long studyAlertId;
    private string? settingsPageHint;
    private double SpriteSize => 280 * preferences.Scale;
    internal double CompanionSpriteSize => SpriteSize;
    internal Border CompanionBubble => bubble;
    internal TextBlock CompanionBubbleText => bubbleText;
    internal double CompanionBubbleScale => preferences.BubbleScale;
    internal string CompanionBubbleLayoutText => speech?.FullText ?? bubbleText.Text;

    private void InitializeCompanion()
    {
        singleInstance = new System.Threading.Mutex(true, "Local\\BlueWhaleCompanion", out var first);
        if (!first)
        {
            System.Windows.Application.Current.Shutdown();
            return;
        }

        instanceLock = File.Open("companion.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        preferences = CompanionPreferences.Read();
        if (CompanionNative.IsExternalForegroundCandidate(startupForeground))
            lastExternalForeground = startupForeground;
        brain = new CompanionBrain
        {
            UseGpu = preferences.GpuEnabled,
            StopOnBattery = preferences.StopModelOnBattery,
            WeatherRegionCode = preferences.WeatherRegionCode,
            Preferences = preferences
        };
        InitializeComponent();
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);
        AllowsTransparency = true;
        Background = null;
        WindowStyle = WindowStyle.None;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "蓝色大肥鱼";
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        companionLayout = new Grid
        {
            Background = Brushes.Transparent
        };
        bubbleText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 17,
            Foreground = new SolidColorBrush(global::BigBlueFish.Windows.CompanionBubble.HairBlue),
            FontFamily = new FontFamily(new Uri("pack://application:,,,/BigBlueFish;component/"),
                "./Res/Font/#乐米小奶泡体"),
            Focusable = false
        };
        var bubbleContent = new Grid();
        bubbleText.Margin = new Thickness(0, 0, 26, 0);
        bubbleContent.Children.Add(bubbleText);
        var dismiss = new Button
        {
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Focusable = false,
            IsTabStop = false,
            Cursor = Cursors.Hand,
            ToolTip = "关闭气泡",
            Content = new TextBlock
            {
                Text = "\uE711",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Foreground = new SolidColorBrush(global::BigBlueFish.Windows.CompanionBubble.HairBlue),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(dismiss, "关闭气泡");
        dismiss.Click += (_, e) =>
        {
            e.Handled = true;
            speech.Cancel();
            bubble.Visibility = Visibility.Collapsed;
        };
        bubbleContent.Children.Add(dismiss);
        bubble = new CompanionBubble
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 300,
            Visibility = Visibility.Collapsed,
            Child = bubbleContent
        };
        companionLayout.Children.Add(bubble);
        ApplyCompanionBubbleScale(false);
        loadingSpinner = new CompanionLoadingRing
        {
            Width = 24,
            Height = 24
        };
        loadingIndicator = new Border
        {
            Width = 34,
            Height = 34,
            Padding = new Thickness(4),
            Background = new SolidColorBrush(Color.FromArgb(248, 255, 251, 244)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(35, 80, 96, 152)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(17),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = global::BigBlueFish.Windows.CompanionBubble.HairBlue, BlurRadius = 7, ShadowDepth = 1, Opacity = .16 },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = loadingSpinner
        };
        Panel.SetZIndex(loadingIndicator, 20);
        companionLayout.Children.Add(loadingIndicator);
        heartsLayer = new Canvas
        {
            IsHitTestVisible = false,
            IsEnabled = false,
            Background = null,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Panel.SetZIndex(heartsLayer, 30);
        companionLayout.Children.Add(heartsLayer);
        speech = new CompanionSpeech(bubbleText, bubble, () => CompanionEdgeLayout.Refresh(this), ResetBubbleDeadline);
        Content = companionLayout;
        ApplyCompanionScale(false);
        var a = SystemParameters.WorkArea;
        Left = preferences.Left ?? (a.Right - Width - 12);
        Top = preferences.Top ?? (a.Bottom - Height + 15);
        ClampCompanion();
        anchor = new Point(Left, Top);
        SourceInitialized += (_, _) => InstallCompanionHook();
        Loaded += async (_, _) =>
        {
            InstallCompanionHook();
            await LoadCompanionAnimations();
            if (preferences.CheckUpdatesOnStartup && !App.Args.Contains("--test-mode"))
            {
                await Task.Delay(3000);
                await CheckForUpdates(false);
            }
        };
        BuildCompanionContextMenu();
        PreviewMouseRightButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (exitAnimation) return;
            lastExternalForeground = CompanionNative.ResolveSpeechForeground(CompanionNative.GetForegroundWindow(), lastExternalForeground);
            PublishMcpInteraction("click", new { button = "right", Left, Top });
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!companionClosed && !exitAnimation && companionContextMenu != null)
                    companionContextMenu.IsOpen = true;
            }));
        };
        PreviewMouseLeftButtonDown += CompanionDown;
        PreviewMouseMove += CompanionMove;
        PreviewMouseLeftButtonUp += CompanionUp;
        LostMouseCapture += (_, _) =>
        {
            if (dragging) { dragging = false; if (dragged) PublishMcpInteraction("drag_end", new { Left, Top, cancelled = true }); FinishDragAnimation(); }
        };
        Closing += CompanionClosing;
        Closed += (_, _) => ShutdownCompanion();
        BuildCompanionTray();
        SystemEvents.PowerModeChanged += CompanionPowerChanged;
        SystemEvents.DisplaySettingsChanged += CompanionDisplayChanged;
        companionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        companionTimer.Tick += CompanionTick;
        nextSpeech = DateTime.UtcNow.AddSeconds(45);
        nextAction = DateTime.UtcNow.AddSeconds(12);
        companionTimer.Start();
        InitializeStudyNotifications();
        InitializeMcp();
    }

    private Task LoadCompanionAnimations()
    {
        try { StartVideoAnimations(); }
        catch (Exception e)
        {
            File.WriteAllText("companion-errors.log", e.ToString());
            ShowCompanionNotice("动画未能加载，请检查文件是否完整。");
        }
        return Task.CompletedTask;
    }

    private void InstallCompanionHook()
    {
        if (hookInstalled)
            return;
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero)
            return;
        hookInstalled = true;
        CompanionNative.SetStyle(h, -20, new IntPtr(CompanionNative.GetStyle(h, -20).ToInt64() | 0x08000000L | 0x80L));
        HwndSource.FromHwnd(h)?.AddHook(CompanionWndProc);
    }

    private IntPtr CompanionWndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x21)
        {
            handled = true;
            return new IntPtr(3);
        } // MA_NOACTIVATE, still deliver clicks.

        if (msg == 0x11) exitReady = true; // System session end need not wait for an animation.
        if (msg == 0x218)
            ApplyCompanionPowerPolicy();
        return IntPtr.Zero;
    }

    private void CompanionPowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (companionClosed)
                return;
            if (e.Mode == PowerModes.Suspend)
                brain.Stop();
            ApplyCompanionPowerPolicy();
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom, brain.MoodSpeechFactor));
        });
    }

    private void ApplyCompanionPowerPolicy()
    {
        if (brain == null || preferences == null) return;
        brain.StopOnBattery = preferences.StopModelOnBattery;
        if (!brain.CanRunOnCurrentPower)
        {
            brain.Stop();
            ClearCompanionModelReply();
        }
        nextWarmup = DateTime.MinValue;
    }

    private void CompanionDisplayChanged(object? s, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        ClampCompanion();
        anchor = new Point(Left, Top);
    });
    private void ApplyCompanionBubbleScale(bool save = true)
    {
        bubble.LayoutTransform = new ScaleTransform(preferences.BubbleScale, preferences.BubbleScale);
        CompanionEdgeLayout.Refresh(this);
        if (save) SaveCompanion();
    }

    private void ApplyCompanionScale(bool save = true)
    {
        Width = SpriteSize * 640 / 360 + 20;
        Height = SpriteSize + 35;
        if (videoImage != null) { videoImage.Width = SpriteSize * 640 / 360; videoImage.Height = SpriteSize; }
        PositionLoadingIndicator();

        if (save)
        {
            ClampCompanion();
            anchor = new Point(Left, Top);
            SaveCompanion();
        }
    }

    private void PositionLoadingIndicator()
    {
        if (loadingIndicator == null || preferences == null)
            return;
        // The idle sprite's visible head is around the upper middle of the
        // transparent sprite. Keep the indicator just above its right side.
        loadingIndicator.Margin = new Thickness(
            Math.Max(0, (Width - SpriteSize * 640 / 360) / 2 + SpriteSize * 412 / 360),
            Math.Max(0, Height - SpriteSize + SpriteSize * 50 / 360),
            0,
            0);
    }

    private void ClampCompanion() => CompanionEdgeLayout.Clamp(this);
    private void SaveCompanion()
    {
        if (preferences == null)
            return;
        preferences.Left = Left;
        preferences.Top = Top;
        try
        {
            preferences.Save();
        }
        catch
        {
        }
    }

    // 好感度系统：每次点击互动，亲密度 +1 并立即落盘；跨档瞬间给晋级庆祝反馈。
    private void AffinityClick()
    {
        if (preferences == null)
            return;
        double before = preferences.AffinityPoints;
        preferences.AffinityPoints += 1;
        // 点击即视为一次互动：刷新互动时间戳，退出冷淡期并重置衰减计时与提醒标记。
        preferences.LastAffinityInteraction = DateTime.UtcNow;
        affinityDecayTimer = 0;
        affinityDecayNotified = false;
        bool promoted = CompanionBrain.AffinityTier(before) != CompanionBrain.AffinityTier(preferences.AffinityPoints);
        SaveCompanion();
        ShowAffinityFeedback(promoted);
    }

    // 好感度系统：点击反馈。普通点击 → 鱼鱼身上冒出一串粉红爱心泡泡（视觉特效，不带文字小反应气泡）；
    // 晋级时额外保留一句庆祝文字，并冒更多爱心庆祝。
    private void ShowAffinityFeedback(bool promoted)
    {
        if (PuppetActive || companionClosed || exitAnimation || !companionReady)
            return;
        if (promoted)
        {
            string text = "好感度 +1 ♡ 晋级「" + CompanionBrain.AffinityTier(preferences.AffinityPoints) + "」啦！";
            ShowCompanionNotice(text, pink: true);
            bubbleUntil = DateTime.UtcNow.AddSeconds(3.5);
            BurstAffinityHearts(11);
        }
        else
        {
            BurstAffinityHearts(6);
        }
    }

    // 好感度系统：粉红爱心泡泡特效——在鱼身附近随机生成若干颗 ♥/♡，向上飘起并淡出，动画结束后移除。
    private static readonly Color[] HeartPalette =
    [
        Color.FromRgb(255, 111, 145),   // 珊瑚粉
        Color.FromRgb(255, 143, 171),   // 樱花粉
        Color.FromRgb(250, 160, 194),   // 粉红
        Color.FromRgb(255, 176, 209),   // 浅粉
        Color.FromRgb(247, 130, 165)    // 玫粉
    ];

    private void BurstAffinityHearts(int count)
    {
        if (heartsLayer == null || companionClosed || exitAnimation)
            return;
        double w = companionLayout.ActualWidth;
        double h = companionLayout.ActualHeight;
        if (w < 20 || h < 20)
            return;
        // 爱心从鱼身中上部冒出（鱼身约占窗口下部）。
        double baseY = Math.Max(70, h * 0.60);
        double centerX = w / 2;
        for (int i = 0; i < count; i++)
        {
            var heart = new TextBlock
            {
                Text = companionRandom.Next(3) == 0 ? "♡" : "♥",
                FontSize = 18 + companionRandom.Next(16),
                FontFamily = new FontFamily("Segoe UI Symbol"),
                Foreground = new SolidColorBrush(HeartPalette[companionRandom.Next(HeartPalette.Length)]),
                Focusable = false,
                IsHitTestVisible = false
            };
            double startX = centerX + (companionRandom.NextDouble() * 2 - 1) * w * 0.26;
            double startY = baseY + companionRandom.NextDouble() * 28;
            Canvas.SetLeft(heart, startX);
            Canvas.SetTop(heart, startY);
            var rotate = new RotateTransform(companionRandom.Next(-26, 27));
            heart.RenderTransformOrigin = new Point(0.5, 0.5);
            heart.RenderTransform = rotate;
            heartsLayer.Children.Add(heart);

            double rise = 95 + companionRandom.NextDouble() * 130;
            double duration = 1.1 + companionRandom.NextDouble() * 0.8;
            var riseAnim = new DoubleAnimation
            {
                From = startY,
                To = startY - rise,
                Duration = TimeSpan.FromSeconds(duration),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fadeAnim = new DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = TimeSpan.FromSeconds(Math.Max(0.4, duration * 0.55)),
                BeginTime = TimeSpan.FromSeconds(duration * 0.35),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            var rockAnim = new DoubleAnimation
            {
                From = rotate.Angle,
                To = rotate.Angle + companionRandom.Next(-22, 23),
                Duration = TimeSpan.FromSeconds(duration),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            riseAnim.Completed += (_, _) => heartsLayer.Children.Remove(heart);
            heart.BeginAnimation(Canvas.TopProperty, riseAnim);
            heart.BeginAnimation(OpacityProperty, fadeAnim);
            rotate.BeginAnimation(RotateTransform.AngleProperty, rockAnim);
        }
    }

    // 好感度系统：托盘「好感度」项点击 → 气泡展示档位、进度条与距下一档分数。
    private void ShowAffinityDetail()
    {
        if (companionClosed || exitAnimation || PuppetActive)
            return;
        if (hiddenByUser) RestoreCompanion();
        double points = preferences.AffinityPoints;
        string tier = CompanionBrain.AffinityTier(points);
        double progress = CompanionBrain.AffinityProgress(points);
        var (next, remaining) = CompanionBrain.AffinityNext(points);
        int filled = (int)Math.Round(progress * 10);
        string bar = new string('♥', filled) + new string('♡', 10 - filled);
        string text = next == null
            ? $"好感度 {points:0} 分 · 「{tier}」· 已是满级啦 {bar}"
            : $"好感度 {points:0} 分 · 「{tier}」· 再 {remaining:0} 分升「{next}」 {bar}";
        ShowCompanionNotice(text, pink: true);
        bubbleUntil = DateTime.UtcNow.AddSeconds(8);
    }

    private void CompanionDown(object sender, MouseButtonEventArgs e)
    {
        if (exitAnimation || e.ChangedButton != MouseButton.Left || bubble.IsMouseOver)
            return;
        e.Handled = true;
        if (e.ClickCount == 2 && TryOpenMcpInput()) {
            PublishMcpInteraction("click", new { button = "left", count = 2, Left, Top });
            return;
        }
        dragging = true;
        dragged = false;
        CancelMovement();
        CompanionNative.GetCursorPos(out var p);
        dragStart = new Point(p.X, p.Y);
        windowStart = new Point(Left, Top);
        CaptureMouse();
    }

    private void CompanionMove(object sender, MouseEventArgs e)
    {
        if (!dragging || e.LeftButton != MouseButtonState.Pressed)
            return;
        CompanionNative.GetCursorPos(out var p);
        var dpi = VisualTreeHelper.GetDpi(this);
        var dx = (p.X - dragStart.X) / dpi.DpiScaleX;
        var dy = (p.Y - dragStart.Y) / dpi.DpiScaleY;
        if (Math.Abs(dx) + Math.Abs(dy) > 4 && !dragged)
        {
            dragged = true;
            PublishMcpInteraction("drag_start", new { Left, Top });
            if (!PuppetActive) PlayCompanion("drag");
        }

        Left = windowStart.X + dx;
        Top = windowStart.Y + dy;
        e.Handled = true;
    }

    private void CompanionUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging)
            return;
        e.Handled = true;
        dragging = false;
        ReleaseMouseCapture();
        ClampCompanion();
        anchor = new Point(Left, Top);
        SaveCompanion();
        if (dragged) { PublishMcpInteraction("drag_end", new { Left, Top, cancelled = false }); FinishDragAnimation(); }
        else { PublishMcpInteraction("click", new { button = "left", Left, Top }); AffinityClick(); ClickAnimation(); }
    }

    private void PlayCompanion(string name)
    {
        if (!companionReady || exitAnimation) return;
        if (name == "drag") { CancelMovement(); PlayAnimation(animationPolicy.Find("被鼠标拖拽悬空反馈"), "Drag"); return; }
        var id = name switch { "idle" => "待机呼吸休闲", "wave" => "点击回应-元气挥手", "angry" => "点击回应-傲娇生气", "sleep" => "原地小憩沉眠", _ => name };
        CancelMovement();
        var rule = animationPolicy.Rules.FirstOrDefault(r => r.Id == id);
        if (rule != null) PlayAnimation(rule, "Preview");
    }

    private void ResetBubbleDeadline()
    {
        var seconds = CompanionPreferences.BubbleDurations[preferences.BubbleDurationIndex];
        bubbleUntil = seconds == 0 ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(seconds);
    }

    private void ShowCompanionBubble(string text)
    {
        if (PuppetActive) return;
        externalBubble = false;
        bubbleIsModelReply = true;
        bubbleUntil = DateTime.MaxValue;
        ResetAffinityPalette();
        speech.Show(text, true);
    }

    private void ShowCompanionPartial(string text, long? expectedVersion = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowCompanionPartial(text, expectedVersion));
            return;
        }
        if (companionClosed || exitAnimation || PuppetActive || text.Length == 0 || expectedVersion.HasValue && expectedVersion != speechRequestVersion)
            return;
        externalBubble = false;
        bubbleIsModelReply = true;
        bubbleUntil = DateTime.MaxValue;
        ResetAffinityPalette();
        speech.ShowPartial(text);
    }

    private void FinishCompanionPartial(string text)
    {
        ShowCompanionPartial(text);
        ResetBubbleDeadline();
    }

    private void BeginModelSpeech()
    {
        if (loadingIndicator == null || loadingSpinner == null)
            return;
        ClearCompanionModelReply();
        loadingIndicator.Visibility = Visibility.Visible;
        loadingSpinner.Start();
        CompanionEdgeLayout.Refresh(this);
    }

    private void EndModelSpeech()
    {
        if (loadingIndicator == null || loadingSpinner == null)
            return;
        loadingSpinner.Stop();
        loadingIndicator.Visibility = Visibility.Collapsed;
        CompanionEdgeLayout.Refresh(this);
    }

    private void ShowCompanionNotice(string text, bool animate = false, bool pink = false)
    {
        if (PuppetActive) return;
        externalBubble = false;
        bubbleIsModelReply = false;
        bubbleUntil = DateTime.MaxValue;
        bubble.SetAffinityPalette(pink);
        bubbleText.Foreground = new SolidColorBrush(pink ? AffinityPinkText : global::BigBlueFish.Windows.CompanionBubble.HairBlue);
        speech.Show(text, animate);
    }

    // 好感度以外的普通气泡显示前，把气泡恢复成奶白蓝边主题。
    private void ResetAffinityPalette()
    {
        bubble.SetAffinityPalette(false);
        bubbleText.Foreground = new SolidColorBrush(global::BigBlueFish.Windows.CompanionBubble.HairBlue);
    }

    private void ClearCompanionModelReply()
    {
        if (!bubbleIsModelReply) return;
        speech.Cancel();
        bubble.Visibility = Visibility.Collapsed;
    }

    private async void CompanionTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var foreground = CompanionNative.GetForegroundWindow();
        if (CompanionNative.IsExternalForegroundCandidate(foreground))
            lastExternalForeground = foreground;
        var dt = Math.Min(.15, (now - lastFrame).TotalSeconds);
        lastFrame = now;
        if (companionClosed || exitAnimation)
            return;
        // ── 好感度：陪伴增长 / 冷淡衰减 / 离场冻结 ──
        // 在场判定：近 15 分钟有键鼠输入 = 用户在电脑前。
        bool userPresent = CompanionNative.IdleSeconds <= PresenceIdleSeconds;
        if (userPresent && !wasUserPresent)
        {
            // 久别重逢：回场瞬间重置互动时间戳，视为重新开始，给满宽限期。
            preferences.LastAffinityInteraction = now;
            affinityDecayTimer = 0;
            affinityDecayNotified = false;
        }
        wasUserPresent = userPresent;
        if (userPresent)
        {
            // 距上次互动 ≤ 60 分钟 → 「陪伴中」：每 600 秒悄悄 +1。
            bool recentlyActive = (now - preferences.LastAffinityInteraction) <= AffinityGraceSpan;
            if (recentlyActive)
            {
                affinityAccumulator += dt;
                affinityDecayTimer = 0;
                if (affinityAccumulator >= AffinityCompanionStep.TotalSeconds)
                {
                    affinityAccumulator = 0;
                    preferences.AffinityPoints += 1;
                }
            }
            else
            {
                // 在场却不互动 → 「冷淡期」：每 60 分钟 -5（到 0 为止），首次用粉色气泡提醒一次。
                affinityAccumulator = 0;
                affinityDecayTimer += dt;
                if (affinityDecayTimer >= AffinityDecayInterval.TotalSeconds)
                {
                    affinityDecayTimer = 0;
                    if (preferences.AffinityPoints > 0)
                    {
                        preferences.AffinityPoints = Math.Max(0, preferences.AffinityPoints - 5);
                        SaveCompanion();
                        if (!affinityDecayNotified)
                        {
                            affinityDecayNotified = true;
                            ShowCompanionNotice("呜…主人坐在电脑前却好久没理鱼鱼，好感度悄悄掉了…", pink: true);
                        }
                    }
                }
            }
        }
        // 用户离场（无键鼠输入）：冻结，不涨不扣。回场由上方「久别重逢」逻辑接住。
        if (preferences.PublicInfo) brain.RefreshWeather();
        if (App.Args.Contains("--test-mode") && now > diagnosticAt)
        {
            diagnosticAt = now.AddSeconds(1);
            var hwnd = new WindowInteropHelper(this).Handle;
            File.WriteAllText("test-status.json", System.Text.Json.JsonSerializer.Serialize(new { ready = companionReady, visible = IsVisible, hiddenByUser, fullscreenHidden, fullscreen = CompanionNative.IsFullscreen(), onAC = CompanionNative.OnAC, runner = brain.RunnerPid, busy = brain.Busy, status = brain.Status, hwnd = hwnd.ToInt64(), foreground = CompanionNative.GetForegroundWindow().ToInt64(), startupForeground = startupForeground.ToInt64(), style = CompanionNative.GetStyle(hwnd, -20).ToInt64(), Left, Top, Width, Height, bubble = bubbleText.Text, bubbleVisible = bubble.Visibility == Visibility.Visible, loadingVisible = loadingIndicator.Visibility == Visibility.Visible, bubbleDurationIndex = preferences.BubbleDurationIndex, bubbleRemainingSeconds = bubbleUntil == DateTime.MaxValue ? -1 : Math.Max(0, (bubbleUntil - now).TotalSeconds), action = currentAction, animationPhase, animationFrames = videoPlayer?.Frames, animationSeconds = videoPlayer?.PositionSeconds, animationInterval = preferences.AnimationIntervalSeconds, workRecognitionEnabled = false, affinity = preferences.AffinityPoints, affinityTier = CompanionBrain.AffinityTier(preferences.AffinityPoints) }));
            if (File.Exists("test-command.txt"))
            {
                var command = File.ReadAllText("test-command.txt").Trim();
                File.Delete("test-command.txt");
                switch (command)
                {
                    case "settings":
                        OpenCompanionSettings();
                        break;
                    case "settings-study":
                        OpenCompanionSettings("学业与账号");
                        break;
                    case "settings-shot":
                        OpenCompanionSettings("学业与账号");
                        ShotCompanionSettings("settings-shot.png");
                        break;
                    case "weather":
                        _ = SpeakCompanionWeather();
                        break;
                    case "memory":
                        _ = SpeakCompanionMemory();
                        break;
                    case "affinity":
                        ShowCompanionNotice($"好感度：{preferences.AffinityPoints:0} 分，鱼鱼和你现在是「{CompanionBrain.AffinityTier(preferences.AffinityPoints)}」阶段～");
                        break;
                    case "feedback":
                        ShowAffinityFeedback(promoted: false);
                        break;
                    case "affinity-detail":
                        ShowAffinityDetail();
                        break;
                    case "speech-test":
                        _ = SpeakCompanionHotTrend();
                        break;
                    case "study-homework":
                        _ = ShowStudyHomeworkAsync();
                        break;
                    case "study-mail":
                        _ = ShowStudyMailAsync();
                        break;
                    case "study-schedule":
                        _ = ShowStudyScheduleAsync();
                        break;
                    case "schedule-shot":
                        _ = ScheduleShotAsync();
                        break;
                    case "study-rooms":
                        _ = ShowStudyRoomsAsync();
                        break;
                    case "rooms-shot":
                        _ = RoomsShotAsync();
                        break;
                    case "rooms-status-shot":
                        _ = RoomsStatusShotAsync();
                        break;
                    case "study-hub":
                        _ = OpenStudyHubAsync(0);
                        break;
                    case "homework-shot":
                        _ = HomeworkShotAsync();
                        break;
                    case "courseware-shot":
                        _ = CoursewareShotAsync();
                        break;
                    case "experiment-shot":
                        _ = ExperimentShotAsync();
                        break;
                    case "schedule-hub-shot":
                        _ = ScheduleHubShotAsync();
                        break;
                    case "schedule-link-shot":
                        _ = ScheduleLinkShotAsync();
                        break;
                    case "study-refresh":
                        _ = RefreshStudyAsync();
                        break;
                    case "wizard-fortune":
                        _ = SpeakCompanionWizardFortune();
                        break;
                    case "wizard-tarot":
                        _ = SpeakCompanionWizardTarot();
                        break;
                    case "close-settings":
                        companionSettings?.Close();
                        break;
                    case "bubble":
                        ShowCompanionBubble("主人，小女仆今天也元气满满地来报到啦～");
                        break;
                    case "bubble-next":
                        ShowCompanionBubble("主人，下一句已经送到啦，小尾巴也跟着摇起来～");
                        break;
                    case "battery":
                        CompanionNative.TestPowerOverride = false;
                        ApplyCompanionPowerPolicy();
                        break;
                    case "ac":
                        CompanionNative.TestPowerOverride = true;
                        nextWarmup = DateTime.MinValue;
                        break;
                    case "real-power":
                        CompanionNative.TestPowerOverride = null;
                        break;
                    case "hide":
                        HideCompanion();
                        break;
                    case "restore":
                        RestoreCompanion();
                        break;
                    case "exit":
                        base.Close();
                        return;
                    case "generate":
                        nextSpeech = now;
                        break;
                    case "inspectable":
                        ShowInTaskbar = true;
                        CompanionNative.SetStyle(hwnd, -20, new IntPtr(CompanionNative.GetStyle(hwnd, -20).ToInt64() & ~0x80L));
                        break;
                    case "normal-style":
                        ShowInTaskbar = false;
                        CompanionNative.SetStyle(hwnd, -20, new IntPtr(CompanionNative.GetStyle(hwnd, -20).ToInt64() | 0x08000000L | 0x80L));
                        break;
                    case "model-off":
                        preferences.ModelEnabled = false;
                        brain.Stop();
                        break;
                    case "model-on":
                        preferences.ModelEnabled = true;
                        nextWarmup = DateTime.MinValue;
                        break;
                    case "render":
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(companionLayout);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using (var stream = File.Create("test-render.png"))
                            encoder.Save(stream);
                        break;
                    case "render-settings":
                        if (companionSettings != null)
                        {
                            var settingsBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)companionSettings.ActualWidth, (int)companionSettings.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                            settingsBitmap.Render((Visual)companionSettings.Content);
                            var settingsEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            settingsEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(settingsBitmap));
                            using var settingsStream = File.Create("test-settings.png");
                            settingsEncoder.Save(settingsStream);
                        }
                        break;
                    default:
                        if (command.StartsWith("event:"))
                            StartAnimationEvent(animationPolicy.Find(command[6..]));
                        else if (command.StartsWith("action:"))
                            PlayCompanion(command[7..]);
                        break;
                }
            }
        }

        if (!brain.CanRunOnCurrentPower)
        {
            if (brain.Busy || brain.RunnerPid != null)
                brain.Stop();
            ClearCompanionModelReply();
        }
        else if (!PuppetActive && modelInstaller == null && preferences.ModelEnabled && brain.ModelAvailable && !speechRequestPending && !brain.Busy && brain.RunnerPid == null && now >= nextWarmup)
        {
            nextWarmup = now.AddMinutes(2);
            _ = brain.WarmUp();
        }

        bool full = preferences.HideOnFullscreen && CompanionNative.IsFullscreen();
        if (full && !fullscreenHidden && !hiddenByUser)
        {
            fullscreenHidden = true;
            Hide();
            brain.CancelGeneration();
        }
        else if (!full && fullscreenHidden)
        {
            fullscreenHidden = false;
            if (!hiddenByUser)
            {
                Show();
                ClampCompanion();
            }
        }

        if (videoPlayer != null) videoPlayer.Paused = !exitAnimation && (hiddenByUser || fullscreenHidden);
        if (exitAnimation || hiddenByUser || fullscreenHidden || !companionReady || dragging)
            return;
        if (bubble.Visibility == Visibility.Visible && now > bubbleUntil)
            bubble.Visibility = Visibility.Collapsed;
        if (PuppetActive || now < nextSpeech || companionSettings != null || modelInstaller != null
            || !HasScheduledMcpSpeech && (speechRequestPending || brain.Busy || weatherRequestPending || !preferences.ModelEnabled || !brain.ModelAvailable || !brain.CanRunOnCurrentPower))
            return;
        if (CompanionNative.IdleSeconds < 8 || CompanionNative.IdleSeconds > 600)
        {
            nextSpeech = now.AddSeconds(30);
            return;
        }

        if (!ConsumeMcpSpeech()) await SpeechCountdownZeroAsync();
    }

    private async Task SpeechCountdownZeroAsync()
    {
        var foregroundHandle = speechForegroundOverride
            ?? CompanionNative.ResolveSpeechForeground(CompanionNative.GetForegroundWindow(), lastExternalForeground);
        speechForegroundOverride = null;
        if (PuppetActive || speechRequestPending || companionClosed || exitAnimation || modelInstaller != null || !preferences.ModelEnabled || !brain.ModelAvailable || !brain.CanRunOnCurrentPower)
            return;

        speechRequestPending = true;
        long version = speechRequestVersion;
        BeginModelSpeech();
        bool streamed = false;
        string text = "";
        try
        {
            // Both countdown and test use this gate. A warm-up or weather task
            // delays the request instead of silently discarding the test click.
            var deadline = DateTime.UtcNow.AddSeconds(100);
            while ((brain.Busy || weatherRequestPending) && DateTime.UtcNow < deadline
                && !companionClosed && !exitAnimation && !hiddenByUser && !fullscreenHidden
                && preferences.ModelEnabled && brain.CanRunOnCurrentPower)
                await Task.Delay(100);
            if (brain.Busy || weatherRequestPending || companionClosed || exitAnimation || hiddenByUser || fullscreenHidden
                || !preferences.ModelEnabled || !brain.CanRunOnCurrentPower)
                return;
            version = speechRequestVersion;
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom, brain.MoodSpeechFactor));
            BeginModelSpeech(); // A preceding weather request may have ended its own loading indicator.
            text = await brain.Generate(preferences.PublicInfo, preferences.ForegroundEnabled, foregroundHandle, partial =>
            {
                streamed = true;
                ShowCompanionPartial(partial, version);
            });
        }
        finally
        {
            speechRequestPending = false;
            EndModelSpeech();
        }
        if (version == speechRequestVersion && !companionClosed && !hiddenByUser && !fullscreenHidden && preferences.ModelEnabled && brain.CanRunOnCurrentPower && text.Length > 0)
        {
            if (streamed)
                FinishCompanionPartial(text);
            else
                ShowCompanionBubble(text);
        }
        else if (streamed)
        {
            ClearCompanionModelReply();
        }
        // Empty/failed generation is silent for automatic speech and tests alike.
    }

    private void BuildCompanionTray()
    {
        var menu = new CompanionTrayMenu();
        menu.Opening += (_, _) => {
            lastExternalForeground = CompanionNative.ResolveSpeechForeground(CompanionNative.GetForegroundWindow(), lastExternalForeground);
            // 天气 / 邮课叮 / 今日打工 / 今日热榜 / 出鲸仙（索引 3/4/5/6/7）。
            foreach (int index in new[] { 3, 4, 5, 6, 7 }) if (menu.Items.Count > index) menu.Items[index].Enabled = !PuppetActive;
            // 好感度进度条实时刷新当前分数与档位。
            if (menu.Items.Count > 1 && menu.Items[1] is Forms.ToolStripControlHost host && host.Control is AffinityProgressControl affinity)
                affinity.SetAffinityPoints(preferences.AffinityPoints);
        };
        menu.Items.Add("显示桌宠", null, (_, _) => Dispatcher.Invoke(RestoreCompanion));
        // 好感度可见化：自绘进度条项（爱心 + 档位 + 进度条 + 距下一档），点击展示详情气泡。
        var affinityControl = new AffinityProgressControl(preferences.AffinityPoints);
        affinityControl.AffinityClicked += (_, _) => { menu.Close(); Dispatcher.Invoke(ShowAffinityDetail); };
        menu.Items.Add(new Forms.ToolStripControlHost(affinityControl));
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(OpenCompanionSettings));
        menu.Items.Add("天气", null, async (_, _) => await Dispatcher.InvokeAsync(SpeakCompanionWeather).Task.Unwrap());
        menu.Items.Add(BuildStudyTrayMenu());
        menu.Items.Add("今日打工", null, async (_, _) => await Dispatcher.InvokeAsync(SpeakCompanionMemory).Task.Unwrap());
        menu.Items.Add("今日热榜", null, async (_, _) => await Dispatcher.InvokeAsync(SpeakCompanionHotTrend).Task.Unwrap());
        var wizard = new Forms.ToolStripMenuItem("出鲸仙");
        wizard.DropDownItems.Add("今日运势", null, async (_, _) => await Dispatcher.InvokeAsync(SpeakCompanionWizardFortune).Task.Unwrap());
        wizard.DropDownItems.Add("塔罗抽牌", null, async (_, _) => await Dispatcher.InvokeAsync(SpeakCompanionWizardTarot).Task.Unwrap());
        wizard.DropDownItems.Add("事件命理", null, async (_, _) => await Dispatcher.InvokeAsync(SpeakCompanionWizardEvent).Task.Unwrap());
        wizard.DropDownItems.Add("命运罗盘", null, async (_, _) =>
        {
            try { await Dispatcher.InvokeAsync(() => CompanionFateWindow.Show()).Task; }
            catch (Exception ex) { await Dispatcher.InvokeAsync(() => ShowCompanionNotice("主人，罗盘转不动：" + ex.Message)).Task; }
        });
        menu.Items.Add(wizard);
        menu.Items.Add("关闭桌宠", null, (_, _) => Dispatcher.Invoke(() => base.Close()));
        var icons = new (int Index, string Glyph)[]
        {
            (0, "\uE7F4"), (2, "\uE713"), (3, "\uE706"), (4, "\uE8F1"), (5, "\uE8A5"), (6, "\uE8F2"), (7, "\uE8F2"), (8, "\uE8BB")
        };
        foreach (var (index, glyph) in icons) menu.Items[index].Tag = glyph;
        using var sourceIcon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "BigBlueFish.ico"));
        notifyIcon = new CompanionTrayIcon
        {
            Icon = new System.Drawing.Icon(sourceIcon, new System.Drawing.Size(32, 32)),
            Text = "蓝色大肥鱼",
            ContextMenuStrip = menu
        };
        notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreCompanion);
        notifyIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(OpenUpdateWindow);
        notifyIcon.Visible = true;
    }

    private void RestoreCompanion()
    {
        if (companionClosed || exitAnimation || exitReady) return;
        hiddenByUser = false;
        Show();
        ClampCompanion();
        nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom, brain.MoodSpeechFactor));
    }

    private void HideCompanion()
    {
        if (companionClosed || exitAnimation || exitReady) return;
        hiddenByUser = true;
        speech.Cancel();
        if (brain.CanRunOnCurrentPower)
            brain.CancelGeneration();
        else
            brain.Stop();
        bubble.Visibility = Visibility.Collapsed;
        Hide();
        companionSettings?.Close();
    }

    private const string StartupName = "BlueWhaleCompanion";
    private static bool StartupEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return k?.GetValue(StartupName) != null;
    }

    private void SetCompanionStartup(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
            k.SetValue(StartupName, "\"" + Environment.ProcessPath + "\"");
        else
            k.DeleteValue(StartupName, false);
    }

    private void ShutdownCompanion()
    {
        if (companionClosed)
            return;
        companionClosed = true;
        modelInstaller?.RequestClose();
        updateWindow?.RequestClose();
        StopMcp();
        StopStudyNotifications();
        companionContextMenu.IsOpen = false;
        companionMenuDismissal?.Dispose();
        companionTimer?.Stop();
        videoPlayer?.Dispose();
        loadingSpinner?.Stop();
        speech?.Dispose();
        brain?.Dispose();
        SaveCompanion();
        companionSettings?.Close();
        SystemEvents.PowerModeChanged -= CompanionPowerChanged;
        SystemEvents.DisplaySettingsChanged -= CompanionDisplayChanged;
        if (notifyIcon != null) { notifyIcon.Visible = false; notifyIcon.Dispose(); }
        instanceLock?.Dispose();
        singleInstance?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
