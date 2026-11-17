using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BigBlueFish.Windows;

/// <summary>
/// 北交大肥鱼 · 空教室自习。
/// 数据来自教务系统「教室使用查询」：一周 7 天 × 每天 N 节的占用网格（节数从接口读，不写死）。
/// 界面按「星期 / 起始节次 / 连续节数 / 教学楼 / 教室名」筛选，列出能连续自习的教室。
/// </summary>
internal static class CompanionRoomWindow
{
    private static Window? instance;
    private static CompanionStudyClient.StudyRoomsView? view;

    private static ComboBox? dayBox;
    private static ComboBox? startBox;
    private static ComboBox? spanBox;
    private static ComboBox? buildingBox;
    private static TextBox? keywordBox;
    private static StackPanel? results;
    private static TextBlock? summaryText;
    private static TextBlock? statusText;
    private static TextBlock? nowText;
    private static Border? nowBar;
    private static Button? nowButton;
    private static Button? refreshButton;
    private static Action<string, string, bool>? onQuery;   // building, keyword, forceRefresh
    private static bool loading;

    /* 「此刻去哪」默认筛选：由打开窗口时的真实时间 + 课表节次时间算出，
     * 用户手动改过筛选后可用「回到此刻」一键还原。 */
    private static string defaultNote = "";
    private static int defaultDay = 1;
    private static int defaultStart = 1;

    private static readonly SolidColorBrush FreeInk = new(Color.FromRgb(30, 122, 79));
    private static readonly SolidColorBrush StreakBrush = new(Color.FromRgb(255, 244, 214));
    private static readonly SolidColorBrush DarkInk = new(Color.FromRgb(37, 44, 54));
    private static readonly SolidColorBrush LightInk = new(Colors.White);
    private static readonly SolidColorBrush RangeBorder = new(Color.FromRgb(21, 87, 60));
    private static readonly SolidColorBrush CellLine = new(Color.FromRgb(198, 204, 213));
    private static readonly SolidColorBrush NowBg = new(Color.FromRgb(233, 242, 255));
    private static readonly SolidColorBrush NowBorder = new(Color.FromRgb(197, 218, 247));
    private static readonly SolidColorBrush NowInk = new(Color.FromRgb(35, 66, 122));
    private static readonly SolidColorBrush GroupInk = new(Color.FromRgb(72, 86, 112));
    private static readonly SolidColorBrush GroupChip = new(Color.FromRgb(224, 233, 248));

    /* 格子颜色严格取自接口 legend（即教务系统网页图例原文）。
     * 绝不自行把绿色当成空闲：网页的 #77bf6d 是「实验占用」。 */
    private static readonly Dictionary<char, SolidColorBrush> StatusBrushes = new();
    private static readonly Dictionary<char, string> StatusLabels = new();

    private static readonly List<CompanionStudyClient.RoomStatusItem> FallbackLegend = new()
    {
        new() { Code = "0", Label = "空闲", Color = "#ffffff" },
        new() { Code = "1", Label = "排课占用", Color = "#e46868" },
        new() { Code = "2", Label = "调课占用", Color = "#9e6868" },
        new() { Code = "3", Label = "考试占用", Color = "#394ed6" },
        new() { Code = "4", Label = "实验占用", Color = "#77bf6d" },
        new() { Code = "5", Label = "其他安排", Color = "#d8cc56" },
        new() { Code = "9", Label = "其他占用", Color = "#8a8f98" }
    };

    private static List<CompanionStudyClient.RoomStatusItem> LegendItems()
        => view?.Legend != null && view.Legend.Count > 0 ? view.Legend : FallbackLegend;

    private static SolidColorBrush HexBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(Colors.White); }
    }

    private static void RefreshLegend()
    {
        StatusBrushes.Clear();
        StatusLabels.Clear();
        foreach (var it in LegendItems())
        {
            if (string.IsNullOrEmpty(it.Code)) continue;
            char code = it.Code[0];
            StatusLabels[code] = string.IsNullOrWhiteSpace(it.Label) ? "占用" : it.Label;
            StatusBrushes[code] = HexBrush(it.Color);
        }
    }

    private static SolidColorBrush BrushFor(char code)
        => StatusBrushes.TryGetValue(code, out var b) ? b : new SolidColorBrush(Colors.White);

    private static string LabelFor(char code)
        => StatusLabels.TryGetValue(code, out var s) ? s : "占用";

    /// <summary>亮底用深字、暗底用白字，保证节次编号在任何网页原色上都看得清。</summary>
    private static SolidColorBrush InkFor(char code)
    {
        if (!StatusBrushes.TryGetValue(code, out var b)) return DarkInk;
        var c = b.Color;
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return lum > 0.62 ? DarkInk : LightInk;
    }

    internal static void Show(CompanionStudyClient.StudyRoomsView data, Action<string, string, bool> query)
    {
        if (instance != null)
        {
            Update(data);
            if (instance.WindowState == WindowState.Minimized) instance.WindowState = WindowState.Normal;
            instance.Activate();
            return;
        }

        view = data;
        onQuery = query;
        RefreshLegend();

        var window = new Window
        {
            Title = "北交大肥鱼 · 空教室自习",
            Width = 1120,
            Height = 780,
            MinWidth = 900,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false,
            Topmost = true,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Background = CompanionFateWindow.Paper
        };
        instance = window;
        window.Closed += (_, _) => { instance = null; view = null; };

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 0 标题
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 1 副标题
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 2 筛选栏
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 3 统计
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 4 列表
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 5 底部

        var title = new TextBlock
        {
            Text = "北交大肥鱼 · 空教室自习",
            FontSize = 23,
            FontWeight = FontWeights.Bold,
            Foreground = CompanionFateWindow.Ink,
            Margin = new Thickness(4, 0, 0, 4)
        };
        Grid.SetRow(title, 0); root.Children.Add(title);

        /* 头部：学期信息 + 「此刻」状态条。状态条把「为什么默认看的是这一天这一节」讲清楚，
         * 避免深夜打开还推荐今天第 1 节，也避免用户以为白色格子是没加载出来。 */
        var head = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        summaryText = new TextBlock
        {
            FontSize = 13,
            Foreground = CompanionFateWindow.Muted,
            Margin = new Thickness(1, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap
        };
        head.Children.Add(summaryText);

        nowBar = new Border
        {
            Background = NowBg,
            BorderBrush = NowBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(11, 8, 11, 8),
            Margin = new Thickness(0, 0, 0, 11)
        };
        var nowDock = new DockPanel { LastChildFill = true };
        nowButton = CompanionFateWindow.PrimaryButton("回到此刻", 92, 28);
        nowButton.Margin = new Thickness(10, 0, 0, 0);
        nowButton.Visibility = Visibility.Collapsed;
        nowButton.ToolTip = "恢复按当前时间推荐的星期与节次";
        nowButton.Click += (_, _) => ResetToNow();
        DockPanel.SetDock(nowButton, Dock.Right);
        nowDock.Children.Add(nowButton);
        nowText = new TextBlock
        {
            FontSize = 12.5,
            Foreground = NowInk,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 19
        };
        nowDock.Children.Add(nowText);
        nowBar.Child = nowDock;
        head.Children.Add(nowBar);
        Grid.SetRow(head, 1); root.Children.Add(head);

        /* ---- 筛选栏 ---- */
        var filters = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 2, 10) };

        // 星期项形如「星期五 11月06日」，太窄会把文字挤成省略号
        dayBox = MakeCombo(150);
        filters.Children.Add(Labeled("星期", dayBox));
        startBox = MakeCombo(165);
        filters.Children.Add(Labeled("从第", startBox));
        spanBox = MakeCombo(88);
        for (int i = 1; i <= 8; i++) spanBox.Items.Add(new ComboItem(i.ToString(), i + " 节"));
        filters.Children.Add(Labeled("连续", spanBox));
        buildingBox = MakeCombo(170);
        filters.Children.Add(Labeled("教学楼", buildingBox));

        keywordBox = new TextBox { Width = 130, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "输入教室名关键字，例如 SY、九教" };
        System.Windows.Automation.AutomationProperties.SetName(keywordBox, "教室名关键字");
        filters.Children.Add(Labeled("教室", keywordBox));

        refreshButton = CompanionFateWindow.PrimaryButton("查询 / 刷新", 110, 30);
        refreshButton.Margin = new Thickness(10, 14, 0, 0);
        refreshButton.Click += (_, _) => TriggerQuery(true);
        filters.Children.Add(refreshButton);

        Grid.SetRow(filters, 2); root.Children.Add(filters);

        statusText = new TextBlock
        {
            FontSize = 12,
            Foreground = CompanionFateWindow.Muted,
            Margin = new Thickness(5, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(statusText, 3); root.Children.Add(statusText);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        results = new StackPanel { Margin = new Thickness(2, 0, 4, 0) };
        scroll.Content = results;
        Grid.SetRow(scroll, 4); root.Children.Add(scroll);

        /* ---- 底部 ---- */
        var footer = new DockPanel { Margin = new Thickness(4, 12, 4, 0) };
        var footerLeft = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var legendRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        foreach (var it in LegendItems())
        {
            legendRow.Children.Add(new Border
            {
                Width = 13,
                Height = 13,
                Background = HexBrush(it.Color),
                BorderBrush = CellLine,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center
            });
            legendRow.Children.Add(new TextBlock
            {
                Text = it.Label,
                FontSize = 11,
                Foreground = CompanionFateWindow.Muted,
                Margin = new Thickness(4, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        footerLeft.Children.Add(legendRow);
        footerLeft.Children.Add(new TextBlock
        {
            Text = "格子颜色与教务系统网页逐格一致（白色=空闲）；粗边框 = 满足筛选的连续空闲区间；每行下方「整周」是该教室 7 天的原色缩略图。列表按「有空档 / 全天无课」分组，普通教室优先。",
            Foreground = CompanionFateWindow.Muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        DockPanel.SetDock(footerLeft, Dock.Left);
        footer.Children.Add(footerLeft);

        var webButton = CompanionFateWindow.PrimaryButton("打开网页版", 106, 32);
        webButton.Margin = new Thickness(0, 0, 8, 0);
        webButton.ToolTip = "在浏览器打开教务系统原页面（需要浏览器里已登录）";
        webButton.Click += (_, _) =>
        {
            var url = view?.PageUrl;
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { if (statusText != null) statusText.Text = "打不开网页：" + ex.Message; }
        };
        DockPanel.SetDock(webButton, Dock.Right);
        footer.Children.Add(webButton);
        Grid.SetRow(footer, 5); root.Children.Add(footer);

        // 先填充下拉（只做一次，避免 Update 时重置用户选择），再渲染
        FillCombos(data, reset: true);
        HookComboEvents();
        Rebuild();
        window.Content = root;
        window.Show();
    }

    /// <summary>刷新数据但保留窗口与当前筛选。</summary>
    internal static void Update(CompanionStudyClient.StudyRoomsView data)
    {
        if (instance == null) return;
        loading = false;
        view = data;
        RefreshLegend();
        FillCombos(data, reset: false);
        if (refreshButton != null) refreshButton.IsEnabled = true;
        Rebuild();
    }

    internal static void SetError(string message)
    {
        loading = false;
        if (refreshButton != null) refreshButton.IsEnabled = true;
        if (statusText != null) statusText.Text = message;
    }

    private static void TriggerQuery(bool force)
    {
        if (loading || onQuery == null) return;
        loading = true;
        if (refreshButton != null) refreshButton.IsEnabled = false;
        if (statusText != null) statusText.Text = force ? "正在重新登录学校系统查询空教室…（约 5-10 秒）" : "正在查询…";
        var b = (buildingBox?.SelectedItem as ComboItem)?.Value ?? "";
        var k = keywordBox?.Text?.Trim() ?? "";
        onQuery(b, k, force);
    }

    private static ComboBox MakeCombo(double width) => new()
    {
        Width = width,
        Margin = new Thickness(0, 0, 0, 0),
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private static StackPanel Labeled(string text, System.Windows.Controls.Control control)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 12, 0) };
        p.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = CompanionFateWindow.Muted, Margin = new Thickness(2, 0, 0, 2) });
        p.Children.Add(control);
        return p;
    }

    private sealed class ComboItem
    {
        public string Value { get; }
        public string Text { get; }
        public ComboItem(string value, string text) { Value = value; Text = text; }
        public override string ToString() => Text;
    }

    private static void FillCombos(CompanionStudyClient.StudyRoomsView data, bool reset)
    {
        if (dayBox == null || startBox == null || spanBox == null || buildingBox == null) return;

        /* 每次刷新都重算「此刻」推荐，但只有首次打开才把选择强行设成它，
         * 之后用户手动选的星期 / 节次不能被后台刷新覆盖。 */
        var pick = PickDefault(data);
        defaultDay = Math.Max(1, pick.Day);
        defaultStart = Math.Max(1, pick.Start);
        defaultNote = pick.Note;

        if (reset || dayBox.Items.Count == 0)
        {
            dayBox.Items.Clear();
            foreach (var d in data.Days)
                dayBox.Items.Add(new ComboItem(d.Day.ToString(), d.Label + (string.IsNullOrEmpty(d.Date) ? "" : " " + d.Date)));
            dayBox.SelectedIndex = Math.Max(0, Math.Min(dayBox.Items.Count - 1, defaultDay - 1));
        }
        if (reset || startBox.Items.Count == 0)
        {
            startBox.Items.Clear();
            var times = data.PeriodTimes ?? new List<CompanionStudyClient.SchedulePeriod>();
            for (int i = 0; i < data.Periods.Count; i++)
            {
                var label = data.Periods[i];
                var t = times.FirstOrDefault(x => x.Period == i + 1)?.Time;
                startBox.Items.Add(new ComboItem((i + 1).ToString(),
                    "第" + label + "节" + (string.IsNullOrWhiteSpace(t) ? "" : "  " + t)));
            }
            if (startBox.Items.Count == 0) startBox.Items.Add(new ComboItem("1", "第1节"));
            startBox.SelectedIndex = Math.Max(0, Math.Min(startBox.Items.Count - 1, defaultStart - 1));
        }
        if (reset && spanBox.SelectedIndex < 0) spanBox.SelectedIndex = 1;   // 默认连续 2 节

        if (reset || buildingBox.Items.Count == 0)
        {
            buildingBox.Items.Clear();
            buildingBox.Items.Add(new ComboItem("", "全部教学楼"));
            foreach (var b in data.Buildings.Where(x => !string.IsNullOrWhiteSpace(x.Id)))
                buildingBox.Items.Add(new ComboItem(b.Id, b.Name));
            buildingBox.SelectedIndex = 0;
        }
    }

    private static void HookComboEvents()
    {
        if (dayBox != null) dayBox.SelectionChanged += (_, _) => Rebuild();
        if (startBox != null) startBox.SelectionChanged += (_, _) => Rebuild();
        if (spanBox != null) spanBox.SelectionChanged += (_, _) => Rebuild();
        // 教学楼 / 教室名走服务端筛选（返回的是该楼/该关键字的教室），需要重新查询
        if (buildingBox != null) buildingBox.SelectionChanged += (_, _) => { if (!loading) TriggerQuery(false); };
        if (keywordBox != null) keywordBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) TriggerQuery(false); };
    }

    /// <summary>测试模式专用：切换到指定筛选条件，用来验证多种占用状态的格子配色。</summary>
    internal static void ApplyFilter(int day, int start, int span)
    {
        if (instance == null) return;
        if (dayBox != null && day >= 1 && day <= dayBox.Items.Count) dayBox.SelectedIndex = day - 1;
        if (startBox != null && start >= 1 && start <= startBox.Items.Count) startBox.SelectedIndex = start - 1;
        if (spanBox != null && span >= 1 && span <= spanBox.Items.Count) spanBox.SelectedIndex = span - 1;
        Rebuild();   // 下拉变化本身也会触发，这里再兜一次，确保截图前已重绘
    }

    private sealed class NowPick
    {
        public int Day = 1;
        public int Start = 1;
        public string Note = "";
    }

    /// <summary>
    /// 默认视图要回答「我现在能去哪自习」，而不是「今天第 1 节起有哪些教室」。
    /// 用课表的真实节次时间判断：上课中 → 当前节；课间/清晨 → 下一节；
    /// 当天所有节次都结束 → 顺延到下一天第 1 节，并把这件事明确写在界面上。
    /// </summary>
    private static NowPick PickDefault(CompanionStudyClient.StudyRoomsView data)
    {
        var pick = new NowPick();
        int days = Math.Max(1, data.Days.Count);
        int today = data.Today > 0 ? data.Today : (((int)DateTime.Now.DayOfWeek + 6) % 7 + 1);
        var now = DateTime.Now;
        int minutes = now.Hour * 60 + now.Minute;
        string hhmm = now.ToString("HH:mm");

        var times = (data.PeriodTimes ?? new List<CompanionStudyClient.SchedulePeriod>())
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Time))
            .OrderBy(x => x.Period).ToList();
        int firstStart = -1, lastEnd = -1, running = 0, upcoming = 0, lastPeriod = 0;
        foreach (var p in times)
        {
            var parts = p.Time.Split('-');
            if (parts.Length < 2) continue;
            if (!TryMinutes(parts[0], out var a) || !TryMinutes(parts[1], out var b)) continue;
            if (firstStart < 0 || a < firstStart) firstStart = a;
            if (b > lastEnd) { lastEnd = b; lastPeriod = p.Period; }
            if (running == 0 && minutes >= a && minutes <= b) running = p.Period;
            if (upcoming == 0 && minutes < a) upcoming = p.Period;
        }

        string DayText(int d)
        {
            var x = data.Days.FirstOrDefault(y => y.Day == d);
            return x == null ? "第" + d + "天" : x.Label + (string.IsNullOrEmpty(x.Date) ? "" : " " + x.Date);
        }
        string TimeOf(int period) => times.FirstOrDefault(x => x.Period == period)?.Time ?? "";

        if (today < 1 || today > days)
        {
            pick.Day = 1; pick.Start = 1;
            pick.Note = $"现在是 {hhmm}。当前查看的是第 {data.Week} 周，不包含今天，因此默认展示该周第一天；可手动切换星期。";
            return pick;
        }
        if (times.Count == 0 || lastEnd < 0)
        {
            pick.Day = today; pick.Start = 1;
            pick.Note = $"现在是 {hhmm} · {DayText(today)}。学校没返回节次时间，暂按第 1 节推荐。";
            return pick;
        }
        if (minutes > lastEnd)
        {
            int next = today + 1;
            string tail;
            if (next > days)
            {
                next = 1;
                tail = "本周数据已到末尾，先展示本周第一天；要看下一周请在网页版切换周次。";
            }
            else
            {
                tail = $"已自动切到 {DayText(next)} 第 1 节（{TimeOf(1)}）。";
            }
            pick.Day = next; pick.Start = 1;
            pick.Note = $"现在是 {hhmm}，今天（{DayText(today)}）的课已全部结束（末节 {TimeOf(lastPeriod)}）→ {tail}";
            return pick;
        }

        int current = running > 0 ? running : (upcoming > 0 ? upcoming : 1);
        pick.Day = today;
        pick.Start = Math.Max(1, Math.Min(Math.Max(1, data.Periods.Count), current));
        string phase = running > 0 ? "正在进行" : (minutes < firstStart ? "今天还没开始上课" : "课间休息");
        pick.Note = $"现在是 {hhmm} · {DayText(today)}，第 {pick.Start} 节（{TimeOf(pick.Start)}）{phase}。下面列出从这一刻起能连续自习的教室。";
        return pick;
    }

    private static void ResetToNow()
    {
        if (dayBox == null || startBox == null || spanBox == null) return;
        int d = Math.Max(0, Math.Min(dayBox.Items.Count - 1, defaultDay - 1));
        int s = Math.Max(0, Math.Min(startBox.Items.Count - 1, defaultStart - 1));
        if (dayBox.SelectedIndex != d) dayBox.SelectedIndex = d;
        if (startBox.SelectedIndex != s) startBox.SelectedIndex = s;
        if (spanBox.SelectedIndex != 1) spanBox.SelectedIndex = 1;
        Rebuild();
    }

    private static bool TryMinutes(string s, out int value)
    {
        value = 0;
        var parts = s.Trim().Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
        value = h * 60 + m;
        return true;
    }

    /// <summary>
    /// 教务系统把体育场馆、会堂、食堂、实验室等「场地」也一并算作教室资源，
    /// 直接按座位数降序会让它们霸占列表顶部（例如「不要教室」「台球馆」500 座）。
    /// 这里只在展示层做启发式降级，不删数据：普通自习教室优先，场地排在后面。
    /// </summary>
    private static bool LooksLikeVenue(CompanionStudyClient.StudyRoom room)
    {
        // 本数据源普通教室最大 253 座，300 座以上基本都是场馆 / 会堂 / 楼层场地
        if (room.Seats >= 300) return true;
        string name = room.Name ?? "";
        string[] hints = { "馆", "厅", "场", "食堂", "后勤", "实验室", "基地", "不要教室",
                           "排练", "游泳", "篮球", "网球", "羽毛球", "乒乓", "健身", "台球",
                           "室外", "报告", "会堂", "体育" };
        foreach (var hint in hints)
            if (name.Contains(hint, StringComparison.Ordinal)) return true;
        return false;
    }

    private static void Rebuild()
    {
        if (results == null || view == null) return;
        results.Children.Clear();

        int dayCount = Math.Max(1, view.Days.Count);
        int periodCount = Math.Max(1, view.Periods.Count);
        int day = (dayBox?.SelectedItem as ComboItem)?.Value is string ds && int.TryParse(ds, out var dv) ? dv : 1;
        int start = (startBox?.SelectedItem as ComboItem)?.Value is string ss && int.TryParse(ss, out var sv) ? sv : 1;
        int span = (spanBox?.SelectedItem as ComboItem)?.Value is string ps && int.TryParse(ps, out var pv) ? pv : 2;
        day = Math.Max(1, Math.Min(dayCount, day));
        start = Math.Max(1, Math.Min(periodCount, start));
        span = Math.Max(1, span);

        var dayInfo = view.Days.FirstOrDefault(x => x.Day == day);
        string startLabel = view.Periods.ElementAtOrDefault(start - 1) ?? start.ToString();
        if (summaryText != null)
        {
            var when = $"第 {view.Week} 周 · {dayInfo?.Label ?? "第" + day + "天"} {dayInfo?.Date ?? ""}";
            summaryText.Text = $"{view.Term}    {when}    共 {view.Count} 间教室" +
                (view.Cached ? "    （缓存数据）" : "") +
                (string.IsNullOrWhiteSpace(view.Warning) ? "" : "    ⚠ " + view.Warning);
        }

        bool atNow = day == defaultDay && start == defaultStart;
        if (nowText != null)
        {
            nowText.Text = atNow
                ? defaultNote
                : $"当前查看的是第 {view.Week} 周 {dayInfo?.Label ?? ""} {dayInfo?.Date ?? ""} 第 {startLabel} 节，不是此刻推荐。点右侧「回到此刻」可恢复按当前时间的筛选。";
        }
        if (nowButton != null) nowButton.Visibility = atNow ? Visibility.Collapsed : Visibility.Visible;
        if (nowBar != null) nowBar.Background = atNow ? NowBg : new SolidColorBrush(Color.FromRgb(255, 246, 228));

        /* 把「当天有课、只是空出一段」和「当天完全没安排」分开。
         * 后者整行都是白格，若按连空节数降序会霸占整个首屏，看起来像没加载出数据。 */
        var withGaps = new List<(CompanionStudyClient.StudyRoom Room, string Grid, string StatusLine, int Streak)>();
        var allClear = new List<(CompanionStudyClient.StudyRoom Room, string Grid, string StatusLine, int Streak)>();
        var freeByPeriod = new int[periodCount];
        foreach (var room in view.Rooms)
        {
            var grid = room.Free != null && room.Free.Count >= day ? room.Free[day - 1] : "";
            if (grid.Length < periodCount) grid = grid.PadRight(periodCount, '0');
            for (int p = 0; p < periodCount; p++) if (grid[p] == '1') freeByPeriod[p]++;
            int streak = 0;
            for (int p = start - 1; p < periodCount && grid[p] == '1'; p++) streak++;
            if (streak < span) continue;
            var statusLine = StatusLineFor(room, grid, day, periodCount);
            var item = (room, grid, statusLine, streak);
            bool clearAllDay = !statusLine.Take(periodCount).Any(c => c != '0');
            (clearAllDay ? allClear : withGaps).Add(item);
        }
        withGaps = withGaps.OrderByDescending(x => x.Streak)
                           .ThenBy(x => LooksLikeVenue(x.Room))
                           .ThenByDescending(x => x.Room.Seats)
                           .ThenBy(x => x.Room.Name).ToList();
        allClear = allClear.OrderBy(x => LooksLikeVenue(x.Room))
                           .ThenByDescending(x => x.Room.Seats)
                           .ThenBy(x => x.Room.Name).ToList();

        int total = withGaps.Count + allClear.Count;
        if (statusText != null)
        {
            var counts = string.Join("   ", freeByPeriod.Select((n, i) => $"第{i + 1}节 {n} 间"));
            statusText.Text = $"从 {dayInfo?.Label ?? ""} 第 {startLabel} 节起连续空闲 ≥{span} 节：{total} 间可用" +
                $"（{withGaps.Count} 间是从课表空档里挤出来的 / {allClear.Count} 间当天完全没安排）" +
                (loading ? "    · 正在重新查询…" : "") +
                "\n当天各节次空闲教室数：" + counts;
        }

        if (total == 0)
        {
            results.Children.Add(new TextBlock
            {
                Text = "这个时间段没有符合条件的空教室。可以试试换个星期、把「连续」改成 1 节，或清空教学楼 / 教室筛选。",
                FontSize = 13,
                Foreground = CompanionFateWindow.Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 20, 6, 0)
            });
            return;
        }

        RenderGroup(results,
            "现在能去 · 当天有课，空出了一段",
            "这些教室当天本来有排课 / 考试 / 其他安排，从所选节次起恰好能连着用；彩色格子可直接与教务系统网页逐格对照。",
            withGaps, 18, day, periodCount, start, atNow);
        RenderGroup(results,
            "当天完全没课 · 随时可去",
            "这一天没有任何安排，所以整行白色是正常结果，不是没加载出来。下方「整周」色带能看到它们其余六天什么时候有课。",
            allClear, 8, day, periodCount, start, atNow);
    }

    private static void RenderGroup(StackPanel host, string title, string note,
        List<(CompanionStudyClient.StudyRoom Room, string Grid, string StatusLine, int Streak)> rows,
        int limit, int day, int periodCount, int start, bool atNow)
    {
        if (rows.Count == 0) return;
        host.Children.Add(GroupHeader(title, note, rows.Count));
        foreach (var item in rows.Take(limit))
            host.Children.Add(BuildRoomCard(item.Room, item.Grid, item.StatusLine, item.Streak, periodCount, day, start, atNow));
        if (rows.Count > limit)
            host.Children.Add(new TextBlock
            {
                Text = $"这一组还有 {rows.Count - limit} 间，用「教学楼」或「教室」关键字缩小范围即可看到。",
                FontSize = 11,
                Foreground = CompanionFateWindow.Muted,
                Margin = new Thickness(6, 4, 6, 8)
            });
    }

    private static UIElement GroupHeader(string title, string note, int count)
    {
        var box = new StackPanel { Margin = new Thickness(4, 10, 4, 7) };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new Border
        {
            Width = 4, Height = 15, Background = CompanionFateWindow.Accent,
            CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0)
        });
        line.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = GroupInk, VerticalAlignment = VerticalAlignment.Center
        });
        line.Children.Add(new Border
        {
            Background = GroupChip, CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = count + " 间", FontSize = 11, Foreground = GroupInk }
        });
        box.Children.Add(line);
        box.Children.Add(new TextBlock
        {
            Text = note, FontSize = 11, Foreground = CompanionFateWindow.Muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 3, 0, 0)
        });
        return box;
    }

    /// <summary>取当天每格的网页状态码；旧接口没有 status 时按 free 位回退（占用按最常见的排课占用显示）。</summary>
    private static string StatusLineFor(CompanionStudyClient.StudyRoom room, string grid, int day, int periodCount)
    {
        var s = room.Status != null && room.Status.Count >= day ? room.Status[day - 1] : "";
        if (string.IsNullOrEmpty(s))
            s = new string((grid ?? "").Select(c => c == '1' ? '0' : '1').ToArray());
        if (s.Length < periodCount) s = s.PadRight(periodCount, '1');
        return s;
    }

    private static Border BuildRoomCard(CompanionStudyClient.StudyRoom room, string grid, string statusLine,
        int streak, int periodCount, int day, int start, bool atNow)
    {
        var card = new Border
        {
            Background = CompanionFateWindow.Card,
            BorderBrush = CompanionFateWindow.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(2, 0, 2, 6),
            Padding = new Thickness(10, 7, 10, 7)
        };
        var row = new DockPanel();

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = room.Name,
            Width = 150,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = CompanionFateWindow.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        left.Children.Add(new TextBlock
        {
            Text = room.Seats > 0 ? room.Seats + " 座" : "—",
            Width = 62,
            FontSize = 11,
            Foreground = CompanionFateWindow.Muted,
            VerticalAlignment = VerticalAlignment.Center
        });
        DockPanel.SetDock(left, Dock.Left);
        row.Children.Add(left);

        var badge = new Border
        {
            Background = StreakBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 3, 9, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = RangeLabel(start, streak, atNow), FontSize = 11, Foreground = FreeInk, FontWeight = FontWeights.SemiBold }
        };
        badge.ToolTip = $"可从第 {start} 节一直待到第 {start + streak - 1} 节，共 {streak} 节";
        DockPanel.SetDock(badge, Dock.Right);
        row.Children.Add(badge);

        var cells = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        for (int p = 0; p < periodCount; p++)
        {
            bool free = grid[p] == '1';
            char code = p < statusLine.Length ? statusLine[p] : (free ? '0' : '1');
            bool inRange = p >= start - 1 && p < start - 1 + streak;
            var cell = new Border
            {
                Width = 52,
                Height = 26,
                Margin = new Thickness(2, 0, 2, 0),
                CornerRadius = new CornerRadius(4),
                /* 底色 = 教务系统网页原色，5 种占用原因逐一区分，不再一律画灰。 */
                Background = BrushFor(code),
                BorderBrush = inRange && free ? RangeBorder : CellLine,
                BorderThickness = new Thickness(inRange && free ? 2 : 0.8),
                ToolTip = $"第 {p + 1} 节 · {LabelFor(code)}",
                Child = new TextBlock
                {
                    Text = (p + 1).ToString(),
                    FontSize = 11,
                    Foreground = InkFor(code),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            cells.Children.Add(cell);
        }
        row.Children.Add(cells);

        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(BuildWeekStrip(room, day, periodCount));
        card.Child = stack;
        return card;
    }

    /// <summary>把「能待多久」写成具体时间段，比「连空 2 节」更贴近「我现在过去能坐到几点」。</summary>
    private static string RangeLabel(int start, int streak, bool atNow)
    {
        var times = view?.PeriodTimes ?? new List<CompanionStudyClient.SchedulePeriod>();
        string first = times.FirstOrDefault(x => x.Period == start)?.Time ?? "";
        string last = times.FirstOrDefault(x => x.Period == start + streak - 1)?.Time ?? "";
        string a = first.Split('-')[0].Trim();
        string b = last.Contains('-') ? last.Split('-').Last().Trim() : "";
        string span = (a.Length > 0 && b.Length > 0) ? $"{a}–{b}" : $"{streak} 节";
        return (atNow ? "现在起  " : "") + span + $"  ·  {streak} 节";
    }

    /// <summary>
    /// 整周占用缩略带：7 天 × N 节，每格用网页原色画出来。
    /// 即使所选这一天整行白色，这一条也能立刻看出该教室其余几天什么时候有课，
    /// 界面不再是一片没有信息量的白，同时不改变任何格子的颜色语义。
    /// </summary>
    private static UIElement BuildWeekStrip(CompanionStudyClient.StudyRoom room, int selectedDay, int periodCount)
    {
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 7, 0, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        line.Children.Add(new TextBlock
        {
            Text = "整周", Width = 44, FontSize = 10,
            Foreground = CompanionFateWindow.Muted, VerticalAlignment = VerticalAlignment.Center
        });

        string[] marks = { "一", "二", "三", "四", "五", "六", "日" };
        int dayCount = Math.Max(1, view?.Days.Count ?? 7);
        for (int d = 1; d <= dayCount; d++)
        {
            line.Children.Add(new TextBlock
            {
                Text = d <= marks.Length ? marks[d - 1] : d.ToString(),
                Width = 12, FontSize = 9, Foreground = CompanionFateWindow.Muted,
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });

            string status = room.Status != null && room.Status.Count >= d ? room.Status[d - 1] : "";
            string free = room.Free != null && room.Free.Count >= d ? room.Free[d - 1] : "";
            var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
            for (int p = 0; p < periodCount; p++)
            {
                bool isFree = p < free.Length && free[p] == '1';
                char code = p < status.Length ? status[p] : (isFree ? '0' : '1');
                group.Children.Add(new Border
                {
                    Width = 9, Height = 11, Margin = new Thickness(0.5, 0, 0.5, 0),
                    Background = BrushFor(code), BorderBrush = CellLine,
                    BorderThickness = new Thickness(0.4), CornerRadius = new CornerRadius(1),
                    ToolTip = $"星期{marks[Math.Min(d, marks.Length) - 1]} 第 {p + 1} 节 · {LabelFor(code)}"
                });
            }
            if (d == selectedDay)
            {
                line.Children.Add(new Border
                {
                    BorderBrush = CompanionFateWindow.Accent, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2), Padding = new Thickness(1),
                    Child = group
                });
            }
            else line.Children.Add(group);
        }
        line.Children.Add(new TextBlock
        {
            Text = "每组 7 格 = 当天各节，颜色同网页", FontSize = 9.5,
            Foreground = CompanionFateWindow.Muted, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        });
        return line;
    }

    /// <summary>测试模式专用：把窗口用 WPF 自己渲染成 PNG（抓屏会被全屏壁纸遮挡）。</summary>
    internal static void RenderToPng(string fileName, int delayMs = 1500)
    {
        if (instance == null) return;
        instance.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(async () =>
        {
            await Task.Delay(delayMs);
            try
            {
                instance.UpdateLayout();
                var root = (FrameworkElement)instance.Content;
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
                try { File.AppendAllText("companion-errors.log", DateTime.Now.ToString("s") + " rooms-shot 失败：" + ex.Message + Environment.NewLine); } catch { }
            }
        }));
    }
}
