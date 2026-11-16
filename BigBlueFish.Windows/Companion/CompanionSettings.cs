using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BigBlueFish.Windows;
public partial class MainWindow
{
    private Action? refreshCompanionSettings;
    private Action? disposeCompanionSettingsContent;

    /// <summary>打开设置窗口；pageTitle 指定初始页（例如从「邮课叮 → 账号设置」直接跳到学业账号页）。</summary>
    private void OpenCompanionSettings(string? pageTitle = null)
    {
        if (exitAnimation || companionClosed || exitReady) return;
        if (pageTitle != null) settingsPageHint = pageTitle;
        if (companionSettings != null)
        {
            ActivateCompanionSettings(companionSettings);
            if (pageTitle != null) BuildCompanionSettingsContent(companionSettings);
            return;
        }

        CancelMovement();
        var w = new Window
        {
            Title = "大肥鱼设置",
            Width = Math.Min(860, SystemParameters.WorkArea.Width - 32),
            Height = Math.Min(690, SystemParameters.WorkArea.Height - 32),
            MinWidth = Math.Min(680, SystemParameters.WorkArea.Width - 32),
            MinHeight = Math.Min(450, SystemParameters.WorkArea.Height - 32),
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 252)),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 14,
            ShowInTaskbar = true,
            ShowActivated = true,
            Topmost = false
        };
        w.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/BigBlueFish;component/Companion/CompanionSettingsStyles.xaml", UriKind.Relative) });
        companionSettings = w;
        BuildCompanionSettingsContent(w);
        w.Closed += (_, _) =>
        {
            disposeCompanionSettingsContent?.Invoke();
            disposeCompanionSettingsContent = refreshCompanionSettings = null;
            settingsPageHint = null;
            companionSettings = null;
            SaveCompanion();
        };
        ActivateCompanionSettings(w);
    }

    private void BuildCompanionSettingsContent(Window w, int selectedPage = 0, double[]? scrollOffsets = null)
    {
        disposeCompanionSettingsContent?.Invoke();
        var shell = new Grid { Background = w.Background };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        shell.ColumnDefinitions.Add(new ColumnDefinition());
        w.Content = shell;
        var sidebar = new StackPanel { Margin = new Thickness(18, 24, 18, 18) };
        var sidebarFrame = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(226, 233, 243)), BorderThickness = new Thickness(0, 0, 1, 0), Child = sidebar };
        shell.Children.Add(sidebarFrame);
        sidebar.Children.Add(new TextBlock { Text = "蓝色大肥鱼", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(53, 76, 129)), Margin = new Thickness(5, 0, 0, 8) });
        var version = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().First().InformationalVersion.Split('+')[0];
#if COMPANION_SAFE
        version += " · 无MCP版";
#endif
        // 无 MCP 版会在版本号后追加「· 无MCP版」，200px 侧栏放不下一整行，必须允许换行，否则尾部会被裁掉。
        sidebar.Children.Add(new TextBlock { Text = version + " · 陪伴设置", Foreground = Brushes.SlateGray, Margin = new Thickness(5, 0, 5, 28), TextWrapping = TextWrapping.Wrap });
#if COMPANION_SAFE
        var titles = new[] { "陪伴与外观", "说话与天气", "模型与供电", "学业与账号" };
#else
        var titles = new[] { "陪伴与外观", "说话与天气", "模型与供电", "学业与账号", "Agent 与 MCP" };
#endif
        var pages = new StackPanel[titles.Length];
        var views = new ScrollViewer[titles.Length];
        var navigation = new Button[titles.Length];
        if (!string.IsNullOrEmpty(settingsPageHint))
        {
            var hintIndex = Array.IndexOf(titles, settingsPageHint);
            if (hintIndex >= 0) selectedPage = hintIndex;
        }
        settingsPageHint = null;
        for (int i = 0; i < pages.Length; i++)
        {
            pages[i] = new StackPanel { Margin = new Thickness(26, 24, 26, 24) };
            pages[i].Children.Add(new TextBlock { Text = titles[i], FontSize = 25, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(42, 56, 83)), Margin = new Thickness(0, 0, 0, 20) });
            views[i] = new ScrollViewer { Content = pages[i], VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = i == selectedPage ? Visibility.Visible : Visibility.Collapsed };
            Grid.SetColumn(views[i], 1); shell.Children.Add(views[i]);
            int selected = i;
            var button = navigation[i] = new Button { Content = titles[i], HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(0), Background = i == selectedPage ? new SolidColorBrush(Color.FromRgb(220, 234, 255)) : Brushes.Transparent };
            button.Click += (_, _) =>
            {
                selectedPage = selected;
                for (int n = 0; n < views.Length; n++)
                { views[n].Visibility = n == selected ? Visibility.Visible : Visibility.Collapsed; navigation[n].Background = n == selected ? new SolidColorBrush(Color.FromRgb(220, 234, 255)) : Brushes.Transparent; }
            };
            sidebar.Children.Add(button);
        }
        void Card(StackPanel panel)
        {
            var children = panel.Children.Cast<UIElement>().Skip(1).ToArray();
            foreach (var child in children) panel.Children.Remove(child);
            var inner = new StackPanel { Margin = new Thickness(20) };
            foreach (var child in children) inner.Children.Add(child);
            panel.Children.Add(new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(226, 233, 243)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Child = inner });
        }
        var p = pages[2];
        void Toggle(string label, bool value, Action<bool> change)
        {
            var c = new CheckBox
            {
                Content = label,
                IsChecked = value,
                Margin = new Thickness(0, 7, 0, 7)
            };
            c.Click += (_, _) =>
            {
                change(c.IsChecked == true);
                SaveCompanion();
            };
            p.Children.Add(c);
        }

        Toggle("启用本地大模型", preferences.ModelEnabled, v =>
        {
            preferences.ModelEnabled = v;
            if (!v)
                brain.Stop();
            else
            {
                nextWarmup = DateTime.MinValue;
                nextSpeech = DateTime.UtcNow.AddSeconds(20);
            }
        });
        Toggle("拔电时暂停并卸载模型", preferences.StopModelOnBattery, v =>
        {
            preferences.StopModelOnBattery = v;
            ApplyCompanionPowerPolicy();
        });
        Toggle("使用显卡加速（关闭则使用 CPU）", preferences.GpuEnabled, v =>
        {
            preferences.GpuEnabled = v;
            brain.Stop();
            brain.UseGpu = v;
            nextWarmup = DateTime.MinValue;
        });
        p = pages[0];
        Toggle("全屏时自动隐藏桌宠", preferences.HideOnFullscreen, v =>
        {
            preferences.HideOnFullscreen = v;
            if (!v && fullscreenHidden)
            {
                fullscreenHidden = false;
                if (!hiddenByUser) { Show(); ClampCompanion(); }
            }
        });
        Toggle("开机自动启动", StartupEnabled(), SetCompanionStartup);
        Toggle("启动时检查更新", preferences.CheckUpdatesOnStartup, v => preferences.CheckUpdatesOnStartup = v);
        p = pages[1];
        Toggle("自动获取天气和科技新闻", preferences.PublicInfo, v => preferences.PublicInfo = v);
        Toggle("感知当前应用（仅在本机处理）", preferences.ForegroundEnabled, v => preferences.ForegroundEnabled = v);
        AddCompanionRegionSelectors(p);
        p = pages[0];
        var label = new TextBlock
        {
            Text = $"缩放：{preferences.Scale:P0}",
            Margin = new Thickness(0, 10, 0, 4)
        };
        p.Children.Add(label);
        var scale = new Slider
        {
            Minimum = .5,
            Maximum = 2.0,
            Value = preferences.Scale,
            TickFrequency = .05,
            IsSnapToTickEnabled = true
        };
        scale.ValueChanged += (_, _) =>
        {
            preferences.Scale = scale.Value;
            label.Text = $"缩放：{scale.Value:P0}";
            ApplyCompanionScale();
        };
        p.Children.Add(scale);
        p = pages[1];
        var bubbleScaleLabel = new TextBlock
        {
            Text = $"文字气泡大小：{preferences.BubbleScale:P0}",
            Margin = new Thickness(0, 18, 0, 4)
        };
        p.Children.Add(bubbleScaleLabel);
        var bubbleScale = new Slider
        {
            Minimum = .5, Maximum = 2.0, Value = preferences.BubbleScale,
            TickFrequency = .05, IsSnapToTickEnabled = true
        };
        System.Windows.Automation.AutomationProperties.SetName(bubbleScale, "文字气泡大小");
        bubbleScale.ValueChanged += (_, _) =>
        {
            preferences.BubbleScale = bubbleScale.Value;
            bubbleScaleLabel.Text = $"文字气泡大小：{bubbleScale.Value:P0}";
            ApplyCompanionBubbleScale();
        };
        p.Children.Add(bubbleScale);
        p = pages[0];
        var intervalLabel = new TextBlock { Text = $"动作触发间隔：{preferences.AnimationIntervalSeconds} 秒", Margin = new Thickness(0, 18, 0, 4) };
        p.Children.Add(intervalLabel);
        var interval = new Slider { Minimum = 30, Maximum = 300, Value = preferences.AnimationIntervalSeconds, TickFrequency = 10, IsSnapToTickEnabled = true };
        System.Windows.Automation.AutomationProperties.SetName(interval, "动作触发间隔");
        interval.ValueChanged += (_, _) =>
        {
            preferences.AnimationIntervalSeconds = (int)Math.Round(interval.Value);
            intervalLabel.Text = $"动作触发间隔：{preferences.AnimationIntervalSeconds} 秒";
            nextAction = DateTime.UtcNow.AddSeconds(preferences.AnimationIntervalSeconds);
            SaveCompanion();
        };
        p.Children.Add(interval);
        var meals = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 12) };
        void MealTime(string labelText, int initialHour, Action<int> change)
        {
            var column = new StackPanel { Margin = new Thickness(0, 0, 14, 0), Width = 110 };
            column.Children.Add(new TextBlock { Text = labelText, Margin = new Thickness(0, 0, 0, 6) });
            var selector = new ComboBox();
            for (int hour = 0; hour < 24; hour++) selector.Items.Add($"{hour:00}:00");
            selector.SelectedIndex = initialHour;
            selector.SelectionChanged += (_, _) => { if (selector.SelectedIndex >= 0) { change(selector.SelectedIndex); SaveCompanion(); } };
            column.Children.Add(selector); meals.Children.Add(column);
        }
        MealTime("早餐时间", preferences.BreakfastHour, h => preferences.BreakfastHour = h);
        MealTime("午餐时间", preferences.LunchHour, h => preferences.LunchHour = h);
        MealTime("晚餐时间", preferences.DinnerHour, h => preferences.DinnerHour = h);
        p.Children.Add(meals);
        p = pages[1];
        var freq = new ComboBox
        {
            Margin = new Thickness(0, 8, 0, 8)
        };
        foreach (var range in CompanionPreferences.SpeechRanges)
            freq.Items.Add($"说话间隔：{range.Min}–{range.Max} 分钟");
        freq.SelectedIndex = preferences.SpeechRangeIndex;
        freq.SelectionChanged += (_, _) =>
        {
            if (freq.SelectedIndex < 0) return;
            preferences.SpeechMinMinutes = CompanionPreferences.SpeechRanges[freq.SelectedIndex].Min;
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
            SaveCompanion();
        };
        p.Children.Add(freq);
        var dwellLabel = new TextBlock
        {
            Text = "气泡驻留时间：" + CompanionPreferences.BubbleDurationLabels[preferences.BubbleDurationIndex],
            Margin = new Thickness(0, 8, 0, 4)
        };
        p.Children.Add(dwellLabel);
        var dwell = new Slider
        {
            Minimum = 0,
            Maximum = 7,
            Value = preferences.BubbleDurationIndex,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            SmallChange = 1,
            LargeChange = 1,
            TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
            ToolTip = dwellLabel.Text
        };
        System.Windows.Automation.AutomationProperties.SetName(dwell, "气泡驻留时间");
        dwell.ValueChanged += (_, _) =>
        {
            preferences.BubbleDurationIndex = (int)Math.Round(dwell.Value);
            dwellLabel.Text = "气泡驻留时间：" + CompanionPreferences.BubbleDurationLabels[preferences.BubbleDurationIndex];
            dwell.ToolTip = dwellLabel.Text;
            if (bubble.Visibility == Visibility.Visible)
                ResetBubbleDeadline();
            SaveCompanion();
        };
        p.Children.Add(dwell);
        p = pages[2];
        var installModel = new Button { Content = "安装模型", Margin = new Thickness(0, 8, 0, 8) };
        installModel.Click += (_, _) => OpenModelInstaller();
        p.Children.Add(installModel);
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.SlateGray,
            Margin = new Thickness(0, 8, 0, 8)
        };
        p.Children.Add(status);
        p.Children.Add(new TextBlock
        {
            Text = "远程 API（OpenAI 兼容）",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 20, 0, 2)
        });
        p.Children.Add(new TextBlock
        {
            Text = "填写后优先使用远程接口，三项留空则回到本地模型。保存后立即生效。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.SlateGray,
            Margin = new Thickness(0, 0, 0, 6)
        });
        TextBox ApiField(string caption, string value, string automationName)
        {
            p.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(0, 6, 0, 2) });
            var field = new TextBox { Text = value };
            System.Windows.Automation.AutomationProperties.SetName(field, automationName);
            p.Children.Add(field);
            return field;
        }
        var apiValues = brain.ApiConfigValues();
        var apiBaseUrl = ApiField("接口地址（例如 https://api.example.com/v1）", apiValues.BaseUrl, "API 接口地址");
        var apiKey = ApiField("API 密钥", apiValues.ApiKey, "API 密钥");
        var apiModel = ApiField("模型名称", apiValues.Model, "API 模型名称");
        var apiStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.SlateGray,
            Margin = new Thickness(0, 4, 0, 2)
        };
        string ApiModeText() => brain.ApiEnabled
            ? "当前生效：远程 API · " + brain.ApiConfigValues().Model
            : "当前生效：本地模型（未配置 API）";
        apiStatus.Text = ApiModeText();
        p.Children.Add(apiStatus);
        var apiButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var saveApi = new Button { Content = "保存 API 设置", Margin = new Thickness(0, 0, 10, 0) };
        var clearApi = new Button { Content = "清空 API 设置" };
        saveApi.Click += (_, _) =>
        {
            var baseUrl = apiBaseUrl.Text.Trim();
            var key = apiKey.Text.Trim();
            var model = apiModel.Text.Trim();
            try
            {
                if (baseUrl.Length == 0 || key.Length == 0 || model.Length == 0)
                {
                    apiStatus.Text = "三项都要填写，已保持本地模型。";
                    return;
                }
                brain.SaveApiConfig(baseUrl, key, model);
                apiStatus.Text = "已保存。" + ApiModeText();
            }
            catch (Exception error)
            {
                apiStatus.Text = "保存失败：" + error.Message;
            }
        };
        clearApi.Click += (_, _) =>
        {
            try
            {
                brain.ClearApiConfig();
                apiBaseUrl.Text = apiKey.Text = apiModel.Text = "";
                apiStatus.Text = "已清空，当前生效：本地模型。";
            }
            catch (Exception error)
            {
                apiStatus.Text = "清空失败：" + error.Message;
            }
        };
        apiButtons.Children.Add(saveApi);
        apiButtons.Children.Add(clearApi);
        p.Children.Add(apiButtons);
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        void UpdateStatus()
        {
            bool available = brain.ModelAvailable;
            installModel.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            status.Text = (CompanionNative.OnAC ? "已插电" :
                preferences.StopModelOnBattery ? "电池供电 · 已按设置暂停模型" : "电池供电 · 允许模型运行")
                + "\n" + (available ? brain.Status : "尚未安装模型");
        }
        timer.Tick += (_, _) => UpdateStatus();
        UpdateStatus();
        timer.Start();
        void Button(string text, Action action)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 4, 0, 4)
            };
            b.Click += (_, _) => action();
            p.Children.Add(b);
        }

        p = pages[0];
        Button("检查更新", async () => await CheckForUpdates(true));
        var updates = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray };
        p.Children.Add(updates);
        timer.Tick += (_, _) => updates.Text = updateStatus;
        Button("隐藏到任务栏通知区", HideCompanion);
        Button("回到右下角", () =>
        {
            var a = SystemParameters.WorkArea;
            Left = a.Right - Width - 8;
            Top = a.Bottom - Height + 12;
            ClampCompanion();
            anchor = new Point(Left, Top);
            SaveCompanion();
        });
        Button("关闭桌宠", () =>
        {
            w.Close();
            base.Close();
        });
        Action? disposeMcpSettings = pages.Length > 4 ? AddMcpSettings(pages[pages.Length - 1], w) : null;
        Action? disposeStudySettings = pages.Length > 3 ? BuildStudyAccountPage(pages[3], w) : null;
        foreach (var panel in pages) Card(panel);
        disposeCompanionSettingsContent = () => { timer.Stop(); disposeStudySettings?.Invoke(); disposeMcpSettings?.Invoke(); };
        refreshCompanionSettings = () => BuildCompanionSettingsContent(w, selectedPage, views.Select(v => v.VerticalOffset).ToArray());
        if (scrollOffsets != null) w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
        {
            if (companionSettings != w || w.Content != shell) return;
            for (int i = 0; i < views.Length; i++) views[i].ScrollToVerticalOffset(scrollOffsets[i]);
        }));
    }

    /// <summary>
    /// 测试模式专用：把当前设置窗口用 WPF 自己渲染成 PNG。
    /// 直接抓屏会被前台全屏窗口遮挡，PrintWindow 对硬件加速的 WPF 窗口又会返回白板，
    /// 用 RenderTargetBitmap 能拿到真实的排版结果。
    /// </summary>
    private void ShotCompanionSettings(string fileName, int delayMs = 2600)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(async () =>
        {
            var w = companionSettings;
            if (w == null) return;
            await Task.Delay(delayMs);   // 等页面里的异步状态文案先回来
            Capture(w, fileName);
            // 再滚到底拍一张，确认下半页（自动提醒 / 汇总）排版
            var scroller = FindVisibleScroller(w.Content as DependencyObject);
            if (scroller != null && scroller.ScrollableHeight > 0)
            {
                scroller.ScrollToEnd();
                await Task.Delay(700);
                w.UpdateLayout();
                Capture(w, System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(fileName) ?? ".",
                    System.IO.Path.GetFileNameWithoutExtension(fileName) + "-bottom.png"));
            }
        }));
    }

    private static ScrollViewer? FindVisibleScroller(DependencyObject? node)
    {
        if (node == null) return null;
        if (node is ScrollViewer sv && sv.Visibility == Visibility.Visible) return sv;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var found = FindVisibleScroller(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            if (found != null) return found;
        }
        return null;
    }

    private static void Capture(Window w, string fileName)
    {
        try
        {
            w.UpdateLayout();
            var root = (FrameworkElement)w.Content;
            var width = (int)Math.Ceiling(root.ActualWidth);
            var height = (int)Math.Ceiling(root.ActualHeight);
            if (width <= 0 || height <= 0) return;
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
            rtb.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(fileName);
            encoder.Save(fs);
        }
        catch (Exception ex)
        {
            try { File.AppendAllText("companion-errors.log", DateTime.Now.ToString("s") + " settings-shot 失败：" + ex.Message + Environment.NewLine); } catch { }
        }
    }

    private void ActivateCompanionSettings(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Topmost = false;
        window.Show();
        window.Activate();
        // Pet/tray menus finish restoring their previous foreground window after
        // the click handler. Activate once that input/menu work has completed.
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(() =>
        {
            if (companionSettings == window && window.IsVisible && !companionClosed && !exitAnimation)
            {
                // This is only reached by an explicit user request. Keep the
                // ordinary window band even if a native topmost flag was left over.
                window.Topmost = false;
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                CompanionNative.SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013);
                window.Activate();
            }
        }));
    }

}
