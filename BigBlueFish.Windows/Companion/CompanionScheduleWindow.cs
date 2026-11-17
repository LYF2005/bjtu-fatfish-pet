using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BigBlueFish.Windows;

/// <summary>北交大肥鱼 · 课表周视图。只读展示当前学期课表，并高亮今天。</summary>
internal static class CompanionScheduleWindow
{
    private static Window? instance;

    internal static void Show(CompanionStudyClient.StudySummary summary, Action? refresh = null)
    {
        if (instance != null)
        {
            if (instance.WindowState == WindowState.Minimized) instance.WindowState = WindowState.Normal;
            instance.Activate();
            return;
        }

        var schedule = summary.Schedule;
        if (schedule == null || !schedule.Ok)
            throw new InvalidOperationException(schedule?.Error ?? "课表暂不可用");

        var window = new Window
        {
            Title = "北交大肥鱼 · 课表",
            Width = 1200,
            Height = 720,
            MinWidth = 1100,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false,
            Topmost = true,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Background = CompanionFateWindow.Paper
        };
        instance = window;
        window.Closed += (_, _) => { instance = null; };

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "北交大肥鱼 · 我的课表",
            FontSize = 23,
            FontWeight = FontWeights.Bold,
            Foreground = CompanionFateWindow.Ink,
            Margin = new Thickness(4, 0, 0, 4)
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var term = new TextBlock
        {
            Text = $"{summary.TermName}    第 {schedule.Week} 周    今天是{DayName(schedule.Today)}",
            FontSize = 13,
            Foreground = CompanionFateWindow.Muted,
            Margin = new Thickness(5, 0, 0, 12)
        };
        Grid.SetRow(term, 1);
        root.Children.Add(term);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var table = new Grid { MinWidth = 900 };

        /* 节次和时间都以教务系统课表实际返回为准（schedule.periods），
         * 不写死 7 节 / 固定时间——不同学校、不同学期节次表都可能不一样。
         * 万一接口没给，就退回到「条目里出现过的节次」，再没有才用内置默认值。 */
        var periods = (schedule.Periods ?? new List<CompanionStudyClient.SchedulePeriod>())
            .Where(x => x.Period > 0)
            .GroupBy(x => x.Period)
            .Select(g => g.First())
            .OrderBy(x => x.Period)
            .ToList();
        if (periods.Count == 0)
        {
            periods = (schedule.Entries ?? new List<CompanionStudyClient.ScheduleEntry>())
                .Where(x => x.Period > 0)
                .GroupBy(x => x.Period)
                .Select(g => new CompanionStudyClient.SchedulePeriod { Period = g.Key, Time = g.First().Time })
                .OrderBy(x => x.Period)
                .ToList();
        }
        if (periods.Count == 0)
        {
            var fallback = new[] { "08:00-09:50", "10:10-12:00", "12:10-14:00", "14:10-16:00", "16:20-18:10", "19:00-20:50", "21:00-21:50" };
            for (int i = 0; i < fallback.Length; i++)
                periods.Add(new CompanionStudyClient.SchedulePeriod { Period = i + 1, Time = fallback[i] });
        }

        // 节次列固定 84，7 个「天」列各固定 150：整周 84 + 7×150 = 1134px，
        // 稳定落在 1200 的窗口内，避免长课程名把列宽撑大、把周末列挤出视口（出现横向滚动）。
        // 列内固定宽度下，课程名/周次会自动在列内换行，不会截断。
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        for (int i = 0; i < 7; i++) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        foreach (var _ in periods) table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(84) });

        AddCell(table, "节次 / 时间", 0, 0, HeaderBrush(), true);
        var dayNames = new[] { "星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日" };
        for (int d = 1; d <= 7; d++)
        {
            var brush = d == schedule.Today ? CompanionFateWindow.Accent : HeaderBrush();
            AddCell(table, dayNames[d - 1] + (d == schedule.Today ? "  · 今天" : ""), d, 0, brush, true, d == schedule.Today ? Brushes.White : CompanionFateWindow.Ink);
        }

        for (int row = 0; row < periods.Count; row++)
        {
            var p = periods[row].Period;
            var time = periods[row].Time ?? "";
            AddCell(table, string.IsNullOrWhiteSpace(time) ? $"第{p}节" : $"第{p}节\n{time}", 0, row + 1, new SolidColorBrush(Color.FromRgb(249, 246, 238)), false);
            for (int d = 1; d <= 7; d++)
            {
                // 注意：这里不再用 ActiveThisWeek 过滤。课表应当展示整学期全部课程，
                // 与教务系统「学生个人课表」一致；当前周之外的课程用灰色弱化并标注周次，
                // 而不是整门隐去——否则第 N 周查看时，已结束/未开始的课程会凭空「消失」，
                // 给人「课表不全」的错觉。
                var entries = (schedule.Entries ?? new List<CompanionStudyClient.ScheduleEntry>())
                    .Where(x => x.Day == d && x.Period == p).ToList();
                var bg = d == schedule.Today ? new SolidColorBrush(Color.FromRgb(255, 248, 225)) : CompanionFateWindow.Card;
                AddCell(table, BuildEntryPanel(entries, schedule.Week), d, row + 1, bg, false);
            }
        }
        scroll.Content = table;
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = new DockPanel { Margin = new Thickness(4, 12, 4, 0) };
        var hint = new TextBlock
        {
            Text = "展示本学期全部课程（灰色为当前周之外）；点击“刷新”重新登录并获取最新课表。",
            Foreground = CompanionFateWindow.Muted,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(hint, Dock.Left);
        footer.Children.Add(hint);
        var refreshButton = CompanionFateWindow.PrimaryButton("刷新课表", 100, 34);
        refreshButton.HorizontalAlignment = HorizontalAlignment.Right;
        refreshButton.Click += (_, _) => { refresh?.Invoke(); window.Close(); };
        footer.Children.Add(refreshButton);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        window.Content = root;
        window.Show();
    }

    private static SolidColorBrush HeaderBrush() => new(Color.FromRgb(235, 231, 244));

    private static string DayName(int day) => day is >= 1 and <= 7 ? new[] { "星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日" }[day - 1] : "未知";

    private static FrameworkElement BuildEntryPanel(IReadOnlyList<CompanionStudyClient.ScheduleEntry> entries, int week)
    {
        if (entries.Count == 0)
            return new TextBlock { Text = "", FontSize = 12 };

        var panel = new StackPanel { Margin = new Thickness(6, 5, 6, 4) };
        foreach (var e in entries)
        {
            bool active = week <= 0 || e.ActiveThisWeek;   // 当前周之外 = 灰色弱化，但依然显示
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(238, 243, 253)),
                BorderBrush = CompanionFateWindow.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 4, 7, 4),
                Margin = new Thickness(0, 0, 0, 3),
                Opacity = active ? 1 : 0.42,
                Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Opacity = .12 }
            };
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = e.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = CompanionFateWindow.Ink, TextWrapping = TextWrapping.Wrap });
            var detail = string.Join(" · ", new[] { e.ShortPlace, e.Teacher }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (detail.Length > 0) content.Children.Add(new TextBlock { Text = detail, FontSize = 10.5, Foreground = CompanionFateWindow.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            if (!string.IsNullOrWhiteSpace(e.Weeks))
                content.Children.Add(new TextBlock
                {
                    Text = (active ? "本周 · " : "") + "第" + e.Weeks + "周",
                    FontSize = 10,
                    Foreground = active ? CompanionFateWindow.Accent : CompanionFateWindow.Muted,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            card.Child = content;
            panel.Children.Add(card);
        }
        return panel;
    }

    private static void AddCell(Grid grid, object content, int col, int row, Brush background, bool bold, Brush? foreground = null)
    {
        var border = new Border
        {
            Background = background,
            BorderBrush = CompanionFateWindow.Line,
            BorderThickness = new Thickness(0.6),
            Padding = new Thickness(5),
            Child = content is FrameworkElement element ? element : new TextBlock { Text = content.ToString() }
        };
        if (border.Child is TextBlock text)
        {
            text.Foreground = foreground ?? CompanionFateWindow.Ink;
            text.FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal;
            text.FontSize = bold ? 12 : 11;
            text.TextWrapping = TextWrapping.Wrap;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.HorizontalAlignment = bold ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }
        Grid.SetColumn(border, col);
        Grid.SetRow(border, row);
        grid.Children.Add(border);
    }

    /// <summary>
    /// 测试模式专用：把课表窗口用 WPF 自己渲染成 PNG。
    /// 直接抓屏会被前台全屏窗口遮挡，PrintWindow 对硬件加速的 WPF 窗口又会返回白板，
    /// 用 RenderTargetBitmap 能拿到真实排版（含完整 11 门课，不被当前周过滤掉）。
    /// </summary>
    internal static void RenderToPng(string fileName, int delayMs = 1500)
    {
        if (instance == null) return;
        instance.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(async () =>
        {
            await Task.Delay(delayMs);   // 等窗口布局稳定
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
                try { File.AppendAllText("companion-errors.log", DateTime.Now.ToString("s") + " schedule-shot 失败：" + ex.Message + Environment.NewLine); } catch { }
            }
        }));
    }
}
