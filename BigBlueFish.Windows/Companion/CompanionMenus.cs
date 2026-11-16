using System;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace BigBlueFish.Windows;

public partial class MainWindow
{
    private const string BatteryPauseNotice = "主人，当前设置为笔记本离电暂停模型，可以在设置里调节哦。";
    private bool memoryRequestPending;
    private bool trendRequestPending;
    private bool wizardRequestPending;

    private void BuildCompanionContextMenu()
    {
        companionContextMenu = new ContextMenu
        {
            PlacementTarget = this,
            Placement = PlacementMode.MousePoint,
            StaysOpen = true
        };
        var menuStyles = new ResourceDictionary
        { Source = new Uri("/BigBlueFish;component/Companion/CompanionMenuStyles.xaml", UriKind.Relative) };
        Application.Current.Resources.MergedDictionaries.Add(menuStyles);
        companionContextMenu.Style = (Style)companionContextMenu.FindResource(typeof(ContextMenu));
        companionMenuDismissal = new CompanionMenuDismissal(companionContextMenu);
        TextBlock Icon(string glyph) => new()
        {
            Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(80, 96, 152)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var settings = new MenuItem { Header = "设置" };
        settings.Icon = Icon("\uE713");
        settings.Click += (_, _) => { companionContextMenu.IsOpen = false; OpenCompanionSettings(); };
        var affinity = new MenuItem { Header = AffinityMenuHeader() };
        affinity.Icon = Icon("\uEB51");
        affinity.Click += (_, _) => { companionContextMenu.IsOpen = false; ShowAffinityDetail(); };
        var weather = new MenuItem { Header = "天气" };
        weather.Icon = Icon("\uE706");
        weather.Click += async (_, _) => { companionContextMenu.IsOpen = false; await SpeakCompanionWeather(); };
        companionContextMenu.Items.Add(settings);
        companionContextMenu.Items.Add(affinity);
        companionContextMenu.Items.Add(weather);
        var study = BuildStudySubmenu();
        study.Icon = Icon("\uE8F1");
        companionContextMenu.Items.Add(study);
        var memory = new MenuItem { Header = "今日打工" };
        memory.Icon = Icon("\uE8A5");
        memory.Click += async (_, _) => { companionContextMenu.IsOpen = false; await SpeakCompanionMemory(); };
        companionContextMenu.Items.Add(memory);
        var test = new MenuItem { Header = "今日热榜" };
        test.Icon = Icon("\uE8EC");
        test.Click += async (_, _) => { companionContextMenu.IsOpen = false; await SpeakCompanionHotTrend(); };
        companionContextMenu.Items.Add(test);
        var wizard = BuildWizardSubmenu();
        wizard.Icon = Icon("\uE8F2");
        companionContextMenu.Items.Add(wizard);
        var hide = new MenuItem { Header = "隐藏桌宠" };
        hide.Icon = Icon("\uE8A7");
        hide.Click += (_, _) => { companionContextMenu.IsOpen = false; HideCompanion(); };
        companionContextMenu.Items.Add(hide);
        companionContextMenu.Opened += (_, _) =>
        {
            weather.IsEnabled = !PuppetActive; study.IsEnabled = !PuppetActive; memory.IsEnabled = !PuppetActive; test.IsEnabled = !PuppetActive;
            wizard.IsEnabled = !PuppetActive;
            affinity.Header = AffinityMenuHeader();
        };
    }

    // 出鲸仙子菜单：今日运势 / 塔罗抽牌 / 事件命理（右键与托盘共用结构）。
    private MenuItem BuildWizardSubmenu()
    {
        var wizard = new MenuItem { Header = "出鲸仙" };
        var fortune = new MenuItem { Header = "今日运势" };
        fortune.Click += async (_, _) => { CloseCompanionMenu(); await SpeakCompanionWizardFortune(); };
        var tarot = new MenuItem { Header = "塔罗抽牌" };
        tarot.Click += (_, _) => { CloseCompanionMenu(); RunAfterWizardMenuClosed(SpeakCompanionWizardTarot); };
        var mingle = new MenuItem { Header = "事件命理" };
        mingle.Click += (_, _) => { CloseCompanionMenu(); RunAfterWizardMenuClosed(SpeakCompanionWizardEvent); };
        var fate = new MenuItem { Header = "命运罗盘" };
        fate.Click += (_, _) =>
        {
            CloseCompanionMenu();
            RunAfterWizardMenuClosed(() =>
            {
                try { CompanionFateWindow.Show(); }
                catch (Exception ex) { ShowCompanionNotice("主人，罗盘转不动：" + ex.Message); }
                return Task.CompletedTask;
            });
        };
        wizard.Items.Add(fortune);
        wizard.Items.Add(tarot);
        wizard.Items.Add(mingle);
        wizard.Items.Add(fate);
        return wizard;
    }

    private void CloseCompanionMenu()
    {
        if (companionContextMenu != null) companionContextMenu.IsOpen = false;
    }

    // 嵌套子菜单（出鲸仙）点击后，外层 ContextMenu 与子菜单 Popup 正处于关闭过程；
    // 若立即同步弹模态窗口 ShowDialog，会与 Popup 关闭竞争，导致模态窗口被吞掉（输入框弹不出）。
    // 延迟到 Dispatcher 空闲后再弹，给 Popup 关闭留出时间；异常仍走 DispatcherUnhandledException 记录。
    private void RunAfterWizardMenuClosed(Func<Task> action)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () => await action());
    }

    // 右键菜单里的好感度摘要：当前分 · 档位（距下一档还差多少 / 已满级）。
    private string AffinityMenuHeader()
    {
        double points = preferences.AffinityPoints;
        string tier = CompanionBrain.AffinityTier(points);
        var (next, remaining) = CompanionBrain.AffinityNext(points);
        string suffix = next == null ? "已满级" : $"距「{next}」还差 {remaining:0} 分";
        return $"♥ 好感度 {points:0} 分 · {tier}（{suffix}）";
    }

    private async Task SpeakCompanionMemory()
    {
        if (PuppetActive || memoryRequestPending || companionClosed || exitAnimation) return;
        if (hiddenByUser) RestoreCompanion();
        memoryRequestPending = true;
        long version = ++speechRequestVersion;
        try
        {
            ShowCompanionNotice("主人，让我看看你今天留下了什么痕迹～");
            // Let an in-flight warm-up or utterance finish; automatic speech yields to this request.
            var deadline = DateTime.UtcNow.AddSeconds(100);
            while (brain.Busy && DateTime.UtcNow < deadline && !companionClosed && brain.CanRunOnCurrentPower)
                await Task.Delay(100);
            if (version != speechRequestVersion || companionClosed) return;
            string summary = await brain.MemorySummaryAsync();
            if (summary.Length == 0)
            {
                ShowCompanionNotice("主人，今天还没连上你的 Agent 桌面呢，痕迹都在睡大觉，晚点再来找我吧～");
                return;
            }
            if (!preferences.ModelEnabled || !brain.ModelAvailable || !brain.CanRunOnCurrentPower)
            {
                ShowCompanionNotice("主人，" + summary + "（模型没开，鱼鱼就先照本宣科啦～）");
                return;
            }
            if (brain.Busy) { ShowCompanionNotice("主人，模型还在忙，待会儿再让我念叨～"); return; }
            BeginModelSpeech();
            string text;
            try
            {
                text = await brain.GenerateMemory(partial => ShowCompanionPartial(partial, version));
            }
            finally
            {
                EndModelSpeech();
            }
            if (version == speechRequestVersion && !companionClosed && !hiddenByUser && !fullscreenHidden && preferences.ModelEnabled
                && brain.CanRunOnCurrentPower)
            {
                if (text.Length > 0) { FinishCompanionPartial(text); }
                else ShowCompanionNotice("主人，" + brain.Status + "。");
            }
        }
        finally
        {
            memoryRequestPending = false;
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
        }
    }

    private async Task SpeakCompanionWeather()
    {
        if (PuppetActive || weatherRequestPending || companionClosed || exitAnimation) return;
        if (hiddenByUser) RestoreCompanion();
        // 无本地模型也能看天气：直接念模板句，不用下载模型。
        if (!preferences.ModelEnabled || !brain.ModelAvailable)
        {
            weatherRequestPending = true;
            try
            {
                ShowCompanionNotice("主人，正在查看" + CompanionRegions.FullName(CompanionRegions.District(preferences.WeatherRegionCode)) + "的天气～");
                string line = await brain.WeatherLineFallback(preferences.WeatherRegionCode);
                if (line.Length > 0) ShowCompanionNotice(line);
                else ShowCompanionNotice("主人，天气资料暂时没取到，过一会儿再问问鱼鱼吧～");
            }
            finally
            {
                weatherRequestPending = false;
                nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
            }
            return;
        }
        if (!brain.CanRunOnCurrentPower)
        {
            ShowCompanionNotice(BatteryPauseNotice, true);
            return;
        }
        weatherRequestPending = true;
        long version = ++speechRequestVersion;
        string regionCode = preferences.WeatherRegionCode;
        try
        {
            ShowCompanionNotice("主人，正在查看" + CompanionRegions.FullName(CompanionRegions.District(regionCode)) + "的天气～");
            // Let an in-flight warm-up or utterance finish; automatic speech yields to this request.
            var deadline = DateTime.UtcNow.AddSeconds(100);
            while (brain.Busy && DateTime.UtcNow < deadline && !companionClosed && brain.CanRunOnCurrentPower)
                await Task.Delay(100);
            if (version != speechRequestVersion || companionClosed || !preferences.ModelEnabled || !brain.CanRunOnCurrentPower || regionCode != preferences.WeatherRegionCode)
                return;
            if (brain.Busy) { ShowCompanionNotice("主人，模型暂时还在忙，请稍后再试一下～"); return; }
            BeginModelSpeech();
            string text;
            try
            {
                text = await brain.GenerateWeather(partial => ShowCompanionPartial(partial, version));
            }
            finally
            {
                EndModelSpeech();
            }
            if (version == speechRequestVersion && !companionClosed && !hiddenByUser && !fullscreenHidden && preferences.ModelEnabled
                && brain.CanRunOnCurrentPower && regionCode == preferences.WeatherRegionCode)
            {
                if (text.Length > 0) { FinishCompanionPartial(text); }
                else ShowCompanionNotice("主人，" + brain.Status + "。");
            }
        }
        finally
        {
            weatherRequestPending = false;
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
        }
    }

    private async Task SpeakCompanionHotTrend()
    {
        if (PuppetActive || trendRequestPending || companionClosed || exitAnimation) return;
        if (hiddenByUser) RestoreCompanion();
        // 无本地模型也能看热榜：直接念模板句，不用下载模型。
        if (!preferences.ModelEnabled || !brain.ModelAvailable)
        {
            trendRequestPending = true;
            try
            {
                ShowCompanionNotice("主人，正在看看大家都在聊什么～");
                string line = await brain.HotTrendLineFallback();
                if (line.Length > 0) ShowCompanionNotice(line);
                else ShowCompanionNotice("主人，热榜暂时没取到，过一会儿再问问鱼鱼吧～");
            }
            finally
            {
                trendRequestPending = false;
                nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
            }
            return;
        }
        if (!brain.CanRunOnCurrentPower)
        {
            ShowCompanionNotice(BatteryPauseNotice, true);
            return;
        }
        trendRequestPending = true;
        long version = ++speechRequestVersion;
        try
        {
            ShowCompanionNotice("主人，正在看看大家都在聊什么～");
            // Let an in-flight warm-up or utterance finish; automatic speech yields to this request.
            var deadline = DateTime.UtcNow.AddSeconds(100);
            while (brain.Busy && DateTime.UtcNow < deadline && !companionClosed && brain.CanRunOnCurrentPower)
                await Task.Delay(100);
            if (version != speechRequestVersion || companionClosed || !preferences.ModelEnabled || !brain.CanRunOnCurrentPower)
                return;
            if (brain.Busy) { ShowCompanionNotice("主人，模型暂时还在忙，请稍后再试一下～"); return; }
            BeginModelSpeech();
            string text;
            try
            {
                text = await brain.GenerateHotTrend(partial => ShowCompanionPartial(partial, version));
            }
            finally
            {
                EndModelSpeech();
            }
            if (version == speechRequestVersion && !companionClosed && !hiddenByUser && !fullscreenHidden && preferences.ModelEnabled
                && brain.CanRunOnCurrentPower)
            {
                if (text.Length > 0) { FinishCompanionPartial(text); }
                else ShowCompanionNotice("主人，" + brain.Status + "。");
            }
        }
        finally
        {
            trendRequestPending = false;
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
        }
    }

    // ── 出鲸仙：今日运势 / 塔罗抽牌 / 事件命理 ──
    // 与其它播报相同：无本地模型时念模板句兜底；有模型时用「出鲸仙」人设生成 JSON 解读。
    private async Task SpeakCompanionWizardFortune()
    {
        if (PuppetActive || wizardRequestPending || companionClosed || exitAnimation) return;
        if (hiddenByUser) RestoreCompanion();
        if (!preferences.ModelEnabled || !brain.ModelAvailable)
        {
            wizardRequestPending = true;
            try
            {
                ShowCompanionNotice("主人，本鲸仙正在掐指推算～");
                string line = CompanionWizardData.FortuneLine();
                if (line.Length > 0) ShowCompanionNotice(line);
            }
            finally
            {
                wizardRequestPending = false;
                nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
            }
            return;
        }
        if (!brain.CanRunOnCurrentPower)
        {
            ShowCompanionNotice(BatteryPauseNotice, true);
            return;
        }
        wizardRequestPending = true;
        long version = ++speechRequestVersion;
        try
        {
            ShowCompanionNotice("主人，本鲸仙掐指一算——很快便有消息～");
            var deadline = DateTime.UtcNow.AddSeconds(100);
            while (brain.Busy && DateTime.UtcNow < deadline && !companionClosed && brain.CanRunOnCurrentPower)
                await Task.Delay(100);
            if (version != speechRequestVersion || companionClosed || !preferences.ModelEnabled || !brain.CanRunOnCurrentPower)
                return;
            if (brain.Busy) { ShowCompanionNotice("主人，模型暂时还在忙，请稍后再试一下～"); return; }
            BeginModelSpeech();
            string raw;
            try
            {
                raw = await brain.GenerateWizardFortune();
            }
            finally
            {
                EndModelSpeech();
            }
            if (version == speechRequestVersion && !companionClosed && !hiddenByUser && !fullscreenHidden && preferences.ModelEnabled
                && brain.CanRunOnCurrentPower)
            {
                ShowWizardReply(raw);
            }
        }
        finally
        {
            wizardRequestPending = false;
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
        }
    }

    private async Task SpeakCompanionWizardTarot()
    {
        if (PuppetActive || wizardRequestPending || companionClosed || exitAnimation) return;
        if (hiddenByUser) RestoreCompanion();
        wizardRequestPending = true;
        try
        {
            ShowCompanionNotice("主人，本鲸仙正在为你洗牌～");
            var picks = CompanionWizardData.DrawTarotOptions(3);
            if (picks.Count == 0)
            {
                ShowCompanionNotice("主人，本鲸仙今天找不到塔罗牌…明天再来问我嘛🦊");
                return;
            }
            var chosen = AskWizardTarotPick(picks);
            if (chosen == null || companionClosed)
            {
                ShowCompanionNotice("主人，牌都摆好了，怎么又走神啦？想抽的时候再唤本鲸仙🦊");
                return;
            }
            string orientation = chosen.Reversed ? "逆位" : "正位";
            // 无本地模型也能抽牌：如实报出抽中的牌面，再附一句神秘提示。
            if (!preferences.ModelEnabled || !brain.ModelAvailable || !brain.CanRunOnCurrentPower)
            {
                ShowCompanionNotice("主人，你抽到的是「" + chosen.Name + "」（" + orientation + "）");
                ShowCompanionNotice("牌面说：" + chosen.Meaning + "。别急着把答案说死，给直觉留一点位置吧🔮");
                return;
            }
            long version = ++speechRequestVersion;
            var deadline = DateTime.UtcNow.AddSeconds(100);
            while (brain.Busy && DateTime.UtcNow < deadline && !companionClosed && brain.CanRunOnCurrentPower)
                await Task.Delay(100);
            if (version != speechRequestVersion || companionClosed || !preferences.ModelEnabled || !brain.CanRunOnCurrentPower)
                return;
            if (brain.Busy) { ShowCompanionNotice("主人，模型暂时还在忙，请稍后再试一下～"); return; }
            ShowCompanionNotice("主人，本鲸仙这就为你解牌～");
            BeginModelSpeech();
            string raw;
            try
            {
                raw = await brain.GenerateWizardTarot(chosen.Text);
            }
            finally
            {
                EndModelSpeech();
            }
            if (version == speechRequestVersion && !companionClosed && !hiddenByUser && !fullscreenHidden && preferences.ModelEnabled
                && brain.CanRunOnCurrentPower)
            {
                ShowWizardReply(raw);
            }
        }
        finally
        {
            wizardRequestPending = false;
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
        }
    }

    private async Task SpeakCompanionWizardEvent()
    {
        if (PuppetActive || wizardRequestPending || companionClosed || exitAnimation) return;
        if (hiddenByUser) RestoreCompanion();
        string? question = AskWizardEventText();
        if (string.IsNullOrWhiteSpace(question)) return;
        if (!preferences.ModelEnabled || !brain.ModelAvailable)
        {
            wizardRequestPending = true;
            try
            {
                ShowCompanionNotice("主人，本鲸仙先把这话记在心里～");
                string line = CompanionWizardData.EventLine(question.Trim());
                if (line.Length > 0) ShowCompanionNotice(line);
            }
            finally
            {
                wizardRequestPending = false;
                nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
            }
            return;
        }
        if (!brain.CanRunOnCurrentPower)
        {
            ShowCompanionNotice(BatteryPauseNotice, true);
            return;
        }
        wizardRequestPending = true;
        long version = ++speechRequestVersion;
        try
        {
            ShowCompanionNotice("主人，本鲸仙听了，正在为你参详～");
            var deadline = DateTime.UtcNow.AddSeconds(100);
            while (brain.Busy && DateTime.UtcNow < deadline && !companionClosed && brain.CanRunOnCurrentPower)
                await Task.Delay(100);
            if (version != speechRequestVersion || companionClosed || !preferences.ModelEnabled || !brain.CanRunOnCurrentPower)
                return;
            if (brain.Busy) { ShowCompanionNotice("主人，模型暂时还在忙，请稍后再试一下～"); return; }
            BeginModelSpeech();
            string raw;
            try
            {
                raw = await brain.GenerateWizardEvent(question.Trim());
            }
            finally
            {
                EndModelSpeech();
            }
            if (version == speechRequestVersion && !companionClosed && !hiddenByUser && !fullscreenHidden && preferences.ModelEnabled
                && brain.CanRunOnCurrentPower)
            {
                ShowWizardReply(raw);
            }
        }
        finally
        {
            wizardRequestPending = false;
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
        }
    }

    // 解析鲸仙的 JSON 解读 {text, mood, action}：text 上气泡（mood=comfort 用粉色安慰气泡），action 播放对应动画。
    // 解析失败时回退到正则抓取 text，再失败则直接显示原文。
    private void ShowWizardReply(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            ShowCompanionNotice("主人，本鲸仙今天掐不动指头…明天再来问我嘛🦊");
            return;
        }
        string text = raw;
        string mood = "neutral";
        string action = "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("text", out var textElement)) text = textElement.GetString() ?? raw;
            if (doc.RootElement.TryGetProperty("mood", out var moodElement)) mood = moodElement.GetString() ?? "neutral";
            if (doc.RootElement.TryGetProperty("action", out var actionElement)) action = actionElement.GetString() ?? "";
        }
        catch (JsonException)
        {
            var match = Regex.Match(raw, "\"text\"\\s*:\\s*\"([^\"]*)\"");
            if (match.Success) text = match.Groups[1].Value;
        }
        text = text.Trim().Trim('"', '“', '”');
        if (text.Length == 0) text = "主人，本鲸仙没算清楚，再唤我一次吧🔮";
        if (string.Equals(mood, "comfort", StringComparison.OrdinalIgnoreCase))
            ShowCompanionNotice(text, animate: true, pink: true);
        else
            ShowCompanionBubble(text);
        if (action.Length > 0) PlayCompanion(action);
    }

    // 描边次按钮：米白底 + 细描边，悬停纸面泛金；与罗盘主按钮同源圆角模板。
    private static Button SecondaryButton(string text, double width, double height)
    {
        var button = new Button
        {
            Content = text, Width = width, Height = height, FontSize = 13, Foreground = CompanionFateWindow.Ink,
            Background = CompanionFateWindow.Card, BorderBrush = CompanionFateWindow.Line,
            BorderThickness = new Thickness(1), Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand
        };
        button.Template = CompanionFateWindow.RoundTemplate(9, true);
        button.MouseEnter += (_, _) => button.Background = CompanionFateWindow.Hover;
        button.MouseLeave += (_, _) => button.Background = CompanionFateWindow.Card;
        return button;
    }

    // 事件命理的模态输入框：主人写下想问的事，确定后带回文本。
    private string? AskWizardEventText()
    {
        if (companionClosed || exitAnimation) return null;
        string? result = null;
        var text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 200,
            Height = 72, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 14, Padding = new Thickness(10, 8, 10, 8), Background = CompanionFateWindow.Field,
            BorderBrush = CompanionFateWindow.Line, BorderThickness = new Thickness(1) };
        System.Windows.Automation.AutomationProperties.SetName(text, "主人想问的事");
        var ok = CompanionFateWindow.PrimaryButton("让本鲸仙算算", 128, 34);
        ok.IsDefault = true;
        var cancel = SecondaryButton("先不了", 78, 34);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(8, 0, 0, 0);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(ok);
        actions.Children.Add(cancel);

        var content = new StackPanel { Margin = new Thickness(4) };
        var titleBar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = CompanionFateWindow.Gold, Margin = new Thickness(0, 2, 10, 2) };
        var title = new TextBlock { Text = "事件命理", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = CompanionFateWindow.Ink };
        var titleRow = new DockPanel { Margin = new Thickness(0, 2, 0, 8) };
        titleRow.Children.Add(titleBar);
        titleRow.Children.Add(title);
        content.Children.Add(titleRow);
        content.Children.Add(new TextBlock { Text = "把想问的事写下来，本鲸仙以六爻替你卜上一卦。", TextWrapping = TextWrapping.Wrap,
            Foreground = CompanionFateWindow.Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) });
        content.Children.Add(text);
        content.Children.Add(actions);

        var window = new Window
        {
            Title = "出鲸仙 · 事件命理", Width = 376, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.SingleBorderWindow, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = content, FontFamily = new FontFamily("Microsoft YaHei UI"),
            Background = CompanionFateWindow.Paper
        };
        ok.Click += (_, _) => { result = text.Text; window.DialogResult = true; };
        cancel.Click += (_, _) => window.DialogResult = false;
        window.Loaded += (_, _) => { text.Focus(); };
        bool? dialogResult = window.ShowDialog();
        return dialogResult == true && !string.IsNullOrWhiteSpace(result) ? result.Trim() : null;
    }

    // 塔罗抽牌的模态选牌窗口：三张牌背反扣，点一张翻牌，确认后带回牌面文本。
    private CompanionWizardData.TarotPick? AskWizardTarotPick(System.Collections.Generic.IReadOnlyList<CompanionWizardData.TarotPick> picks)
    {
        if (companionClosed || exitAnimation || picks.Count == 0) return null;
        CompanionWizardData.TarotPick? result = null;
        var hint = new TextBlock { Text = "三张牌已洗好，凭第一感觉选一张吧", TextWrapping = TextWrapping.Wrap,
            Foreground = CompanionFateWindow.Ink, FontSize = 13, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 14) };
        var pickPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var revealTitle = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 18, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Foreground = CompanionFateWindow.Ink };
        var revealTag = new TextBlock { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var revealMeaning = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(84, 92, 108)), FontSize = 13,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(6, 10, 6, 0) };
        var confirm = CompanionFateWindow.PrimaryButton("就这张，替我解一下", 156, 34);
        confirm.IsDefault = true;
        confirm.Margin = new Thickness(0, 14, 0, 0);
        var repick = SecondaryButton("换一张", 78, 34);
        repick.Margin = new Thickness(10, 14, 0, 0);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        actions.Children.Add(confirm);
        actions.Children.Add(repick);
        var revealPanel = new StackPanel { Margin = new Thickness(10, 4, 10, 0), Visibility = Visibility.Collapsed };
        revealPanel.Children.Add(revealTitle);
        revealPanel.Children.Add(revealTag);
        revealPanel.Children.Add(revealMeaning);
        revealPanel.Children.Add(actions);
        for (int i = 0; i < picks.Count; i++)
        {
            var pick = picks[i];
            var card = new Button
            {
                Width = 96, Height = 132, Margin = new Thickness(i == 0 ? 0 : 12, 0, 0, 0),
                Background = new LinearGradientBrush(
                    Color.FromRgb(124, 92, 255), Color.FromRgb(179, 136, 255), 90),
                BorderThickness = new Thickness(1.5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)),
                Cursor = System.Windows.Input.Cursors.Hand,
                Effect = CompanionFateWindow.SoftShadow(),
                Content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
                {
                    new TextBlock { Text = "✦", FontSize = 26, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "第 " + (i + 1) + " 张", FontSize = 13, Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) }
                } }
            };
            card.Template = CompanionFateWindow.RoundTemplate(12, true);
            card.Click += (_, _) =>
            {
                result = pick;
                revealTitle.Text = pick.Name;
                revealTag.Text = pick.Reversed ? "逆位" : "正位";
                revealTag.Foreground = new SolidColorBrush(pick.Reversed ? Color.FromRgb(214, 120, 84) : Color.FromRgb(88, 148, 108));
                revealMeaning.Text = pick.Meaning;
                pickPanel.Visibility = Visibility.Collapsed;
                revealPanel.Visibility = Visibility.Visible;
            };
            pickPanel.Children.Add(card);
        }
        repick.Click += (_, _) =>
        {
            result = null;
            revealPanel.Visibility = Visibility.Collapsed;
            pickPanel.Visibility = Visibility.Visible;
        };
        var window = new Window
        {
            Title = "出鲸仙 · 塔罗抽牌", Width = 420, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.SingleBorderWindow, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Background = CompanionFateWindow.Paper
        };
        confirm.Click += (_, _) => { if (result != null) window.DialogResult = true; };
        var content = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        content.Children.Add(hint);
        content.Children.Add(pickPanel);
        content.Children.Add(revealPanel);
        window.Content = content;
        bool? dialogResult = window.ShowDialog();
        return dialogResult == true && result != null ? result : null;
    }

    private void AddCompanionRegionSelectors(Panel panel)
    {
        panel.Children.Add(new TextBlock { Text = "天气地点", Margin = new Thickness(0, 10, 0, 4) });
        var grid = new Grid();
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        ComboBox Selector(string name, string caption, int column)
        {
            var columnPanel = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 6, 0, 0, 0) };
            columnPanel.Children.Add(new TextBlock { Text = caption, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
            var combo = new ComboBox { Name = name, DisplayMemberPath = "Name", SelectedValuePath = "Code", MinHeight = 30 };
            System.Windows.Automation.AutomationProperties.SetName(combo, caption);
            columnPanel.Children.Add(combo);
            Grid.SetColumn(columnPanel, column);
            grid.Children.Add(columnPanel);
            return combo;
        }
        var province = Selector("WeatherProvince", "省 / 自治区 / 直辖市", 0);
        var city = Selector("WeatherCity", "市 / 州", 1);
        var district = Selector("WeatherDistrict", "县 / 区", 2);
        var selected = CompanionRegions.District(preferences.WeatherRegionCode);
        var selectedCity = CompanionRegions.Parent(selected);
        province.ItemsSource = CompanionRegions.Children("0");
        province.SelectedValue = selectedCity.ParentCode;
        city.ItemsSource = CompanionRegions.Children(selectedCity.ParentCode);
        city.SelectedValue = selectedCity.Code;
        district.ItemsSource = CompanionRegions.Children(selectedCity.Code);
        district.SelectedValue = selected.Code;
        bool changing = false;
        void SaveRegion()
        {
            if (district.SelectedItem is not CompanionRegion region) return;
            if (preferences.WeatherRegionCode != region.Code) brain.CancelGeneration();
            preferences.WeatherRegionCode = region.Code;
            brain.WeatherRegionCode = region.Code;
            SaveCompanion();
        }
        province.SelectionChanged += (_, _) =>
        {
            if (changing || province.SelectedItem is not CompanionRegion region) return;
            changing = true;
            city.ItemsSource = CompanionRegions.Children(region.Code);
            city.SelectedIndex = 0;
            district.ItemsSource = city.SelectedItem is CompanionRegion c ? CompanionRegions.Children(c.Code) : Array.Empty<CompanionRegion>();
            district.SelectedIndex = 0;
            changing = false;
            SaveRegion();
        };
        city.SelectionChanged += (_, _) =>
        {
            if (changing || city.SelectedItem is not CompanionRegion region) return;
            changing = true;
            district.ItemsSource = CompanionRegions.Children(region.Code);
            district.SelectedIndex = 0;
            changing = false;
            SaveRegion();
        };
        district.SelectionChanged += (_, _) => { if (!changing) SaveRegion(); };
        panel.Children.Add(grid);
        panel.Children.Add(new TextBlock { Text = "天气来自 Open-Meteo，按所选区县中心附近估算。", FontSize = 11,
            Foreground = System.Windows.Media.Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 4) });
    }
}
