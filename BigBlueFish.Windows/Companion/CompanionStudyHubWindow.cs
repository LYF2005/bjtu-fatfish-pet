using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BigBlueFish.Windows;

/// <summary>
/// 北交大肥鱼 · 学习中心（2026-11-14 视觉重构）。
///
/// 设计：浅色纸面 + 大留白 + 极淡分隔线；层级靠字号与间距，状态靠胶囊与细色条，
/// 不使用系统默认 TabControl / 密集表格 / 整行染色。配色统一取自 CompanionTheme，
/// 与浏览器版学业设置页使用同一套色值。
///
/// 三个子页，首次切到才拉数据（懒加载）：
///   ① 作业：统计 / 类型筛选（全部·作业·实验·报告）/ 实验栏覆盖核对 / 只看未交 / 双击深链
///   ② 课程资料：学期 → 课程 → 目录 → 勾选批量下载、打开本地目录
///   ③ 课表：星期为列、节次/时间为行的表格化周视图，今天列高亮；点课程直接跳到「课程资料」并自动选中同一门课
///
/// 课表 ↔ 课件的关联依据：课表条目的 code 与课件课程的 key 都是同一个课程代码
/// （例如 M319018B = 密码学）。两边的老师名未必一致（区块链课表是应文池、课件是刘今），
/// 所以按代码精确匹配优先，课程名只做兜底，绝不按老师名猜。
/// 数据来自本机桥接服务的 /study/summary、/study/homeworks、/study/courseware/*，桌宠不保存账号密码。
/// </summary>
internal static class CompanionStudyHubWindow
{
    private static Window? instance;
    private static CompanionStudyClient? client;
    private static int currentTab;

    /* 顶部导航 */
    private static Button? navHomework;
    private static Button? navCourseware;
    private static Button? navSchedule;
    private static Border? navLineHomework;
    private static Border? navLineCourseware;
    private static Border? navLineSchedule;
    private static Grid? pageHomework;
    private static Grid? pageCourseware;
    private static Grid? pageSchedule;
    private static TextBlock? pageSubtitle;

    /* 作业页 */
    private static CheckBox? onlyPendingBox;
    private static StackPanel? homeworkRows;
    private static StackPanel? statRow;
    private static StackPanel? typeChips;
    private static StackPanel? coverageRows;
    private static TextBlock? coverageSummary;
    private static TextBlock? filterCount;
    private static Button? homeworkRefresh;
    private static readonly List<CompanionStudyClient.StudyHomework> allHomework = new();
    private static CompanionStudyClient.StudyHomeworkCoverage coverage = new();
    private static string typeFilter = "all";
    private static bool homeworkLoaded;

    /* 课件资料页 */
    private static ComboBox? termBox;
    private static ComboBox? courseBox;
    private static ComboBox? folderBox;
    private static StackPanel? fileRows;
    private static StackPanel? localRows;
    private static TextBlock? courseStatus;
    private static TextBlock? dirText;
    private static TextBlock? localSummary;
    private static Button? downloadButton;
    private static Button? courseRefresh;
    private static CompanionStudyClient.CoursewareCoursesView? coursesView;
    private static readonly List<CompanionStudyClient.CoursewareFile> currentFiles = new();
    private static bool coursewareLoaded;
    private static bool suppressing;
    private static string downloadDir = "";

    /* 课表页：星期为列、节次/时间为行的表格化网格 */
    private static Grid? scheduleRows;            // 表格容器（Grid，可 Stretch 填满，内含一张课表 Grid）
    private static ScrollViewer? scheduleScroll;  // 保存引用以便重建后可靠回到顶部
    private static StackPanel? scheduleStats;
    private static CheckBox? onlyThisWeekBox;
    private static TextBlock? scheduleStatus;
    private static Button? scheduleRefresh;
    private static CompanionStudyClient.Schedule? scheduleData;
    private static bool scheduleLoaded;

    /* ---------- 入口 ---------- */

    internal static void Show(CompanionStudyClient studyClient, int tab = 0)
    {
        client = studyClient;
        if (instance != null)
        {
            SelectTab(tab);
            try { instance.Activate(); } catch { /* 窗口已关闭 */ }
            return;
        }

        var window = new Window
        {
            Title = "北交大肥鱼 · 学习中心",
            Width = 1180,
            Height = 848,
            MinWidth = 1000,
            MinHeight = 680,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = CompanionTheme.Surface,
            Content = BuildRoot()
        };
        window.Closed += (_, _) =>
        {
            instance = null;
            navHomework = null;
            navCourseware = null;
            navSchedule = null;
            pageHomework = null;
            pageCourseware = null;
            pageSchedule = null;
            homeworkLoaded = false;
            coursewareLoaded = false;
            scheduleLoaded = false;
            /* 关窗重开一律重新拉数据：换账号后不能沿用上一个人的课程列表 / 作业 / 课表。 */
            coursesView = null;
            scheduleData = null;
            allHomework.Clear();
            currentFiles.Clear();
        };
        instance = window;
        window.Show();
        SelectTab(tab);
    }

    /// <summary>测试模式用：切到指定页（会触发懒加载）。</summary>
    internal static void ApplyTab(int index) => SelectTab(index);

    /// <summary>测试模式用：切换「只看未交」并立即重绘。</summary>
    internal static void SetOnlyPending(bool value)
    {
        if (onlyPendingBox == null) return;
        onlyPendingBox.IsChecked = value;
        RenderHomeworks();
    }

    /// <summary>测试模式用：切到某个任务类型（all / homework / experiment / report）。</summary>
    internal static void SetTypeFilter(string value)
    {
        typeFilter = string.IsNullOrEmpty(value) ? "all" : value;
        RenderTypeChips();
        RenderHomeworks();
    }

    private static void EnsureLoaded(int index)
    {
        if (client == null) return;
        if (index == 0 && !homeworkLoaded)
        {
            homeworkLoaded = true;
            _ = LoadHomeworksAsync(false);
        }
        else if (index == 1 && !coursewareLoaded)
        {
            coursewareLoaded = true;
            _ = LoadCoursesAsync(false);
            _ = LoadLocalAsync();
        }
        else if (index == 2 && !scheduleLoaded)
        {
            scheduleLoaded = true;
            /* 串行加载：先课表，再课程列表。两个都走学校 SSO，
             * 并发发起会互相顶掉会话（项目里踩过这个坑）。 */
            _ = LoadScheduleTabAsync();
        }
    }

    private static void SelectTab(int index)
    {
        currentTab = Math.Max(0, Math.Min(2, index));
        if (pageHomework != null)
            pageHomework.Visibility = currentTab == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (pageCourseware != null)
            pageCourseware.Visibility = currentTab == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (pageSchedule != null)
            pageSchedule.Visibility = currentTab == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (navHomework != null) CompanionTheme.SetNavActive(navHomework, currentTab == 0);
        if (navCourseware != null) CompanionTheme.SetNavActive(navCourseware, currentTab == 1);
        if (navSchedule != null) CompanionTheme.SetNavActive(navSchedule, currentTab == 2);
        if (navLineHomework != null)
            navLineHomework.Visibility = currentTab == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (navLineCourseware != null)
            navLineCourseware.Visibility = currentTab == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (navLineSchedule != null)
            navLineSchedule.Visibility = currentTab == 2 ? Visibility.Visible : Visibility.Collapsed;
        /* 副标题按子页给说明。作业页随后会被「已检查 N 门课」覆盖，
         * 从课表跳转时 OpenCoursewareForAsync 会再写入关联证据，顺序不冲突。 */
        if (pageSubtitle != null)
        {
            pageSubtitle.Text = currentTab switch
            {
                0 => "作业、实验、课程报告 · 按类型筛选，逐门课核对实验栏",
                1 => "课程课件 · 按学期与课程浏览目录，勾选即可批量下载",
                _ => "作业 DDL · 课程课件 · 课表 · 点课表里的课程可直接跳到对应课件，凭据只存在本机"
            };
        }
        EnsureLoaded(currentTab);
    }

    /* ---------- 骨架 ---------- */

    private static UIElement BuildRoot()
    {
        var root = new Grid { Background = CompanionTheme.Surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        /* 页头：eyebrow + 大标题 + 一行说明。为课表腾出垂直空间，收紧各处留白。 */
        var title = new StackPanel { Margin = new Thickness(40, 20, 40, 0) };
        title.Children.Add(T("邮 课 叮  /  STUDY HUB", CompanionTheme.MicroSize, CompanionTheme.Brand, FontWeights.SemiBold));
        var h1 = T("学习中心", CompanionTheme.TitleSize - 4, CompanionTheme.Ink, FontWeights.Light);
        h1.Margin = new Thickness(0, 4, 0, 0);
        title.Children.Add(h1);
        pageSubtitle = T("作业 DDL · 课程课件 · 课表 · 点课表里的课程可直接跳到对应课件，凭据只存在本机",
            CompanionTheme.SmallSize, CompanionTheme.Subtle);
        pageSubtitle.Margin = new Thickness(0, 6, 0, 0);
        title.Children.Add(pageSubtitle);
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        /* 顶部导航：文字 + 细下划线，替换系统 TabControl。三个子页共用一个窗口。 */
        var nav = new Grid { Margin = new Thickness(40, 16, 40, 0) };
        nav.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        nav.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        nav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        navHomework = CompanionTheme.NavButton("作业 DDL");
        navHomework.Margin = new Thickness(0, 0, 30, 0);
        navHomework.Click += (_, _) => SelectTab(0);
        Grid.SetRow(navHomework, 0); Grid.SetColumn(navHomework, 0);
        nav.Children.Add(navHomework);

        navCourseware = CompanionTheme.NavButton("课程课件");
        navCourseware.Margin = new Thickness(0, 0, 30, 0);
        navCourseware.Click += (_, _) => SelectTab(1);
        Grid.SetRow(navCourseware, 0); Grid.SetColumn(navCourseware, 1);
        nav.Children.Add(navCourseware);

        navSchedule = CompanionTheme.NavButton("课表");
        navSchedule.Click += (_, _) => SelectTab(2);
        Grid.SetRow(navSchedule, 0); Grid.SetColumn(navSchedule, 2);
        nav.Children.Add(navSchedule);

        navLineHomework = new Border
        {
            Height = 2, Background = CompanionTheme.Brand,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 30, 0)
        };
        Grid.SetRow(navLineHomework, 1); Grid.SetColumn(navLineHomework, 0);
        navLineCourseware = new Border
        {
            Height = 2, Background = CompanionTheme.Brand,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 30, 0)
        };
        Grid.SetRow(navLineCourseware, 1); Grid.SetColumn(navLineCourseware, 1);
        navLineSchedule = new Border
        {
            Height = 2, Background = CompanionTheme.Brand,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetRow(navLineSchedule, 1); Grid.SetColumn(navLineSchedule, 2);
        var baseline = new Border
        {
            Height = 1, Background = CompanionTheme.Hairline,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetRow(baseline, 1); Grid.SetColumnSpan(baseline, 4);

        nav.Children.Add(baseline);
        nav.Children.Add(navLineHomework);
        nav.Children.Add(navLineCourseware);
        nav.Children.Add(navLineSchedule);
        Grid.SetRow(nav, 1);
        root.Children.Add(nav);

        var content = new Grid { Margin = new Thickness(40, 16, 40, 0) };
        pageHomework = BuildHomeworkTab();
        pageCourseware = BuildCoursewareTab();
        pageSchedule = BuildScheduleTab();
        content.Children.Add(pageHomework);
        content.Children.Add(pageCourseware);
        content.Children.Add(pageSchedule);
        Grid.SetRow(content, 2);
        root.Children.Add(content);

        var footer = T("双击作业行或本地课件用系统默认程序打开 · 单击课表课程可跳到对应课件 · 账号密码仅保存在本机桥接服务",
            CompanionTheme.MicroSize, CompanionTheme.Subtle);
        footer.Margin = new Thickness(40, 8, 40, 12);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        return root;
    }

    /* ---------- 作业页 ---------- */

    private static Grid BuildHomeworkTab()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 统计
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 筛选
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 实验栏核对
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var stats = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statRow = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(statRow, 0);
        stats.Children.Add(statRow);
        onlyPendingBox = new CheckBox
        {
            Content = "只看未交",
            IsChecked = true,
            FontSize = CompanionTheme.BodySize,
            Foreground = CompanionTheme.Ink,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        onlyPendingBox.Checked += (_, _) => RenderHomeworks();
        onlyPendingBox.Unchecked += (_, _) => RenderHomeworks();
        Grid.SetColumn(onlyPendingBox, 1);
        stats.Children.Add(onlyPendingBox);
        Grid.SetRow(stats, 0);
        grid.Children.Add(stats);

        var filter = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        typeChips = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(typeChips, 0);
        filter.Children.Add(typeChips);
        filterCount = T("—", CompanionTheme.SmallSize, CompanionTheme.Subtle);
        filterCount.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetColumn(filterCount, 1);
        filter.Children.Add(filterCount);
        homeworkRefresh = CompanionTheme.Button("重新同步");
        homeworkRefresh.Click += (_, _) => _ = LoadHomeworksAsync(true);
        Grid.SetColumn(homeworkRefresh, 2);
        filter.Children.Add(homeworkRefresh);
        Grid.SetRow(filter, 1);
        grid.Children.Add(filter);
        RenderTypeChips();

        var covCard = CompanionTheme.Panel(BuildCoverageInner(), new Thickness(20, 16, 20, 16));
        covCard.Margin = new Thickness(0, 0, 0, 20);
        Grid.SetRow(covCard, 2);
        grid.Children.Add(covCard);

        homeworkRows = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = homeworkRows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 6, 0)
        };
        Grid.SetRow(scroll, 3);
        grid.Children.Add(scroll);
        return grid;
    }

    private static UIElement BuildCoverageInner()
    {
        var stack = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(CompanionTheme.Section("实验栏核对"));
        var cap = CompanionTheme.Caption("平台把「实验 / 课程报告」也计入作业任务数 · 逐门课对账，避免漏掉实验里的作业");
        cap.Margin = new Thickness(0, 4, 0, 0);
        cap.TextWrapping = TextWrapping.Wrap;
        left.Children.Add(cap);
        Grid.SetColumn(left, 0);
        head.Children.Add(left);

        coverageSummary = T("正在核对…", CompanionTheme.SmallSize, CompanionTheme.Subtle, FontWeights.SemiBold);
        coverageSummary.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(coverageSummary, 1);
        head.Children.Add(coverageSummary);
        stack.Children.Add(head);

        coverageRows = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        stack.Children.Add(coverageRows);
        return stack;
    }

    private static void RenderTypeChips()
    {
        if (typeChips == null) return;
        typeChips.Children.Clear();
        var defs = new[] { ("全部", "all"), ("作业", "homework"), ("实验", "experiment"), ("报告", "report") };
        foreach (var (label, value) in defs)
            typeChips.Children.Add(MakeChip(label, value));
    }

    private static Border MakeChip(string text, string value)
    {
        bool active = typeFilter == value;
        var b = new Border
        {
            Background = active ? CompanionTheme.BrandSoft : CompanionTheme.Raised,
            BorderBrush = active ? CompanionTheme.BrandSoft : CompanionTheme.Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(15, 6, 15, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Child = T(text, CompanionTheme.SmallSize,
                active ? CompanionTheme.Brand : CompanionTheme.Subtle, FontWeights.SemiBold)
        };
        b.MouseLeftButtonDown += (_, _) =>
        {
            typeFilter = value;
            RenderTypeChips();
            RenderHomeworks();
        };
        return b;
    }

    private static async Task LoadHomeworksAsync(bool refresh)
    {
        if (client == null) return;
        if (homeworkRefresh != null) homeworkRefresh.IsEnabled = false;
        if (pageSubtitle != null)
            pageSubtitle.Text = refresh ? "正在重新登录课程平台同步作业…" : "正在读取作业、实验与课程报告…";
        try
        {
            var v = await client.GetHomeworksAsync(refresh, CancellationToken.None);
            if (pageSubtitle == null) return;
            if (!v.Reachable || !v.Ok)
            {
                pageSubtitle.Text = "作业读取失败：" + (v.TransportError.Length > 0 ? v.TransportError : v.Error);
                return;
            }
            allHomework.Clear();
            allHomework.AddRange(v.Items ?? new List<CompanionStudyClient.StudyHomework>());
            coverage = v.Coverage ?? new CompanionStudyClient.StudyHomeworkCoverage();
            pageSubtitle.Text = $"{v.XqName} · 已检查 {v.CourseCount} 门课 · " +
                (v.FromSnapshot ? "轮询快照" : "刚刚重新同步") + " · 账号凭据只存在本机";
            RenderStats();
            RenderCoverage();
            RenderHomeworks();
        }
        finally
        {
            if (homeworkRefresh != null) homeworkRefresh.IsEnabled = true;
        }
    }

    private static void RenderStats()
    {
        if (statRow == null) return;
        statRow.Children.Clear();
        int pending = allHomework.Count(h => !h.Submitted && !h.Overdue);
        int overdue = allHomework.Count(h => !h.Submitted && h.Overdue);
        statRow.Children.Add(CompanionTheme.Stat(pending.ToString(), "待交", CompanionTheme.Brand));
        statRow.Children.Add(CompanionTheme.Stat(overdue.ToString(), "已逾期", CompanionTheme.Danger));
        statRow.Children.Add(CompanionTheme.Stat(allHomework.Count.ToString(), "本学期任务", CompanionTheme.Ink));
        var extra = new StackPanel { Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        extra.Children.Add(T($"其中实验 {coverage.ExperimentTotal} 条 · 报告 {coverage.ReportTotal} 条",
            CompanionTheme.SmallSize, CompanionTheme.Subtle));
        statRow.Children.Add(extra);
    }

    private static void RenderCoverage()
    {
        if (coverageRows == null || coverageSummary == null) return;
        coverageRows.Children.Clear();

        int gap = coverage.GapTotal;
        coverageSummary.Text = gap > 0 ? $"{gap} 条待核对" : "全部对上";
        coverageSummary.Foreground = gap > 0 ? CompanionTheme.Warning : CompanionTheme.Done;

        var overview = new Border
        {
            Background = CompanionTheme.Soft,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 7, 12, 7),
            Child = T($"已检查 {coverage.CheckedCourses} 门课 · 平台自报 {coverage.ReportedHomeworkTotal} 个任务 · " +
                $"已抓 {coverage.CapturedTotal} 条（作业 {coverage.HomeworkTotal} / 实验 {coverage.ExperimentTotal} / 报告 {coverage.ReportTotal}）",
                CompanionTheme.SmallSize, CompanionTheme.Ink)
        };
        coverageRows.Children.Add(overview);

        var list = coverage.Courses ?? new List<CompanionStudyClient.StudyCoverageCourse>();
        if (list.Count == 0)
        {
            var empty = T("各门课的「实验 / 课程报告」栏都没有额外任务。",
                CompanionTheme.SmallSize, CompanionTheme.Subtle);
            empty.Margin = new Thickness(2, 10, 0, 0);
            coverageRows.Children.Add(empty);
            return;
        }

        /* 每门课只占一行，把「其中 N 条在实验栏」并进明细。
         * 卡片太高会挤掉作业列表的可视行数，可读性优先。 */
        foreach (var c in list.Take(6))
        {
            var row = new Grid { Margin = new Thickness(2, 9, 2, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = T(c.Course, CompanionTheme.BodySize, CompanionTheme.Ink, FontWeights.SemiBold);
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            var parts = new List<string> { $"作业栏 {c.Homework}" };
            if (c.Experiment > 0) parts.Add($"实验栏 {c.Experiment}");
            if (c.Report > 0) parts.Add($"报告栏 {c.Report}");
            var detail = T(string.Join(" + ", parts) + $" = {c.Captured}" +
                    (c.ReportedHomework.HasValue ? $" · 平台 {c.ReportedHomework.Value}" : "") +
                    (c.Experiment > 0 ? $" · 其中 {c.Experiment} 条在实验栏" : "") +
                    (c.Report > 0 ? $" · 其中 {c.Report} 条在报告栏" : ""),
                CompanionTheme.SmallSize, CompanionTheme.Subtle);
            detail.ToolTip = detail.Text;
            Grid.SetColumn(detail, 1);
            row.Children.Add(detail);

            bool ok = (c.Gap ?? 0) == 0;
            var pill = CompanionTheme.Pill(ok ? "已对上" : "待核对",
                ok ? CompanionTheme.Done : CompanionTheme.Warning,
                ok ? CompanionTheme.DoneSoft : CompanionTheme.WarningSoft);
            pill.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(pill, 2);
            row.Children.Add(pill);

            coverageRows.Children.Add(row);
        }
    }

    private static void RenderHomeworks()
    {
        if (homeworkRows == null) return;
        homeworkRows.Children.Clear();
        bool only = onlyPendingBox?.IsChecked ?? false;
        var list = allHomework.Where(h =>
            (typeFilter == "all" || h.Kind == typeFilter) &&
            (!only || (!h.Submitted && !h.Overdue))).ToList();
        if (filterCount != null) filterCount.Text = $"{list.Count} 条";

        foreach (var h in list.Take(160))
            homeworkRows.Children.Add(BuildHomeworkRow(h));

        if (list.Count == 0)
        {
            var t = T(typeFilter == "experiment"
                    ? "这个学期的「实验」栏里没有作业。切回「全部」可以看作息、报告和已交记录。"
                    : (only ? "太好了，没有未交的任务～取消「只看未交」或切换类型可以看到全部记录。"
                            : "这个学期还没有抓到任务，点「重新同步」再试一次。"),
                CompanionTheme.BodySize, CompanionTheme.Subtle);
            t.Margin = new Thickness(4, 22, 4, 8);
            homeworkRows.Children.Add(t);
        }
        else if (list.Count > 160)
        {
            var t = T($"仅显示前 160 条，共 {list.Count} 条。", CompanionTheme.SmallSize, CompanionTheme.Subtle);
            t.Margin = new Thickness(4, 10, 4, 4);
            homeworkRows.Children.Add(t);
        }
    }

    private static Border BuildHomeworkRow(CompanionStudyClient.StudyHomework h)
    {
        var kind = string.IsNullOrEmpty(h.Kind) ? "homework" : h.Kind;
        Brush kindInk = kind == "experiment" ? CompanionTheme.Experiment
            : kind == "report" ? CompanionTheme.Report : CompanionTheme.Subtle;
        Brush kindBg = kind == "experiment" ? CompanionTheme.ExperimentSoft
            : kind == "report" ? CompanionTheme.ReportSoft : CompanionTheme.Soft;
        string kindText = kind == "experiment" ? "实验" : kind == "report" ? "报告" : "作业";

        string state = h.Submitted ? "已交" : (h.Overdue ? "逾期" : (h.DueSoon ? "临近" : "未交"));
        Brush stateInk = h.Submitted ? CompanionTheme.Done
            : h.Overdue ? CompanionTheme.Danger
            : h.DueSoon ? CompanionTheme.Warning : CompanionTheme.Brand;
        Brush stateBg = h.Submitted ? CompanionTheme.DoneSoft
            : h.Overdue ? CompanionTheme.DangerSoft
            : h.DueSoon ? CompanionTheme.WarningSoft : CompanionTheme.BrandSoft;

        var g = new Grid { MinHeight = 50 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });   // 状态细色条
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });  // 类型
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) }); // 截止
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });  // 剩余
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });  // 状态

        var stripe = new Border
        {
            Background = h.Overdue ? CompanionTheme.Danger
                : (!h.Submitted && h.DueSoon ? CompanionTheme.Warning : CompanionTheme.Hairline),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 12, 10, 12),
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        Grid.SetColumn(stripe, 0);
        g.Children.Add(stripe);

        var typePill = CompanionTheme.Pill(kindText, kindInk, kindBg);
        Grid.SetColumn(typePill, 1);
        g.Children.Add(typePill);

        var main = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        // 已交不再把标题刷成浅灰：可读性优先，状态只由右侧胶囊与左侧细色条表达
        var title = T(h.Title, CompanionTheme.BodySize,
            h.Overdue ? CompanionTheme.Danger : CompanionTheme.Ink,
            h.Overdue ? FontWeights.SemiBold : FontWeights.Normal);
        title.ToolTip = h.Title;
        main.Children.Add(title);
        var course = T(h.DisplayCourse, CompanionTheme.MicroSize, CompanionTheme.Subtle);
        course.Margin = new Thickness(0, 3, 0, 0);
        course.ToolTip = h.DisplayCourse;
        main.Children.Add(course);
        Grid.SetColumn(main, 2);
        g.Children.Add(main);

        var due = T(string.IsNullOrEmpty(h.DisplayEndTime) ? "—" : h.DisplayEndTime,
            CompanionTheme.SmallSize, CompanionTheme.Subtle);
        Grid.SetColumn(due, 3);
        g.Children.Add(due);

        var remaining = T(h.Submitted ? "—" : (string.IsNullOrEmpty(h.RemainingText) ? "—" : h.RemainingText),
            CompanionTheme.SmallSize, h.Overdue ? CompanionTheme.Danger : CompanionTheme.Ink,
            h.Overdue ? FontWeights.SemiBold : FontWeights.Normal);
        Grid.SetColumn(remaining, 4);
        g.Children.Add(remaining);

        var statePill = CompanionTheme.Pill(state, stateInk, stateBg);
        Grid.SetColumn(statePill, 5);
        g.Children.Add(statePill);

        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = CompanionTheme.Hairline,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 5, 6, 5),
            Child = g,
            Cursor = Cursors.Hand,
            ToolTip = string.IsNullOrEmpty(h.Url) ? "这条作业暂时没有页面地址" : "双击打开课程平台作业页（需已登录学校账号）"
        };
        border.MouseEnter += (_, _) => border.Background = CompanionTheme.Soft;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            if (string.IsNullOrWhiteSpace(h.Url))
            {
                if (pageSubtitle != null) pageSubtitle.Text = "这条作业暂时没有页面地址，点「重新同步」再试一次。";
                return;
            }
            OpenWithShell(h.Url, "作业页面");
        };
        return border;
    }

    /* ---------- 课表页 ---------- */

    private static Grid BuildScheduleTab()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        scheduleStats = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(scheduleStats, 0);
        head.Children.Add(scheduleStats);

        onlyThisWeekBox = new CheckBox
        {
            Content = "只看本周",
            IsChecked = false,
            FontSize = CompanionTheme.SmallSize,
            Foreground = CompanionTheme.InkSoft,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 16, 0)
        };
        onlyThisWeekBox.Checked += (_, _) => RenderSchedule();
        onlyThisWeekBox.Unchecked += (_, _) => RenderSchedule();
        Grid.SetColumn(onlyThisWeekBox, 1);
        head.Children.Add(onlyThisWeekBox);

        scheduleRefresh = CompanionTheme.Button("重新同步");
        scheduleRefresh.Click += (_, _) => _ = LoadScheduleAsync(true);
        Grid.SetColumn(scheduleRefresh, 2);
        head.Children.Add(scheduleRefresh);
        Grid.SetRow(head, 0);
        grid.Children.Add(head);

        scheduleStatus = T("正在读取课表…", CompanionTheme.SmallSize, CompanionTheme.Subtle);
        scheduleStatus.Margin = new Thickness(2, 0, 2, 8);
        scheduleStatus.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(scheduleStatus, 1);
        grid.Children.Add(scheduleStatus);

        scheduleRows = new Grid();
        scheduleScroll = new ScrollViewer
        {
            Content = scheduleRows,
            // 整张课表在一屏内呈现：禁用纵向滚动，行高按可用高度 Star 均分；
            // 仅当窗口被拉得很窄时才用横向滚动兜底 7 列。
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 6, 0)
        };
        Grid.SetRow(scheduleScroll, 2);
        grid.Children.Add(scheduleScroll);
        return grid;
    }

    /// <summary>课表页懒加载：课表 → 课程列表串行执行，避免两次 SSO 并发互相顶号。</summary>
    private static async Task LoadScheduleTabAsync()
    {
        await LoadScheduleAsync(false);
        if (coursesView == null)
        {
            coursewareLoaded = true;
            await LoadCoursesAsync(false);
        }
        // 课程列表就绪后重绘一次，让「查看课件」胶囊反映真实匹配结果
        RenderSchedule();
        _ = LoadLocalAsync();
    }

    private static async Task LoadScheduleAsync(bool refresh)
    {
        if (client == null) return;
        if (scheduleRefresh != null) scheduleRefresh.IsEnabled = false;
        SetScheduleStatus(refresh ? "正在重新登录并拉取课表…（约 10-40 秒）" : "正在读取课表…");
        try
        {
            if (refresh) await client.RefreshAsync(CancellationToken.None);
            var v = await client.GetSummaryAsync(CancellationToken.None);
            if (!v.Reachable)
            {
                SetScheduleStatus("课表读取失败：桥接服务没启动。" + v.TransportError);
                return;
            }
            if (v.Schedule == null || !v.Schedule.Ok)
            {
                SetScheduleStatus("课表读取失败：" + (v.Schedule?.Error ?? "教务系统没有返回数据"));
                return;
            }
            scheduleData = v.Schedule;
            RenderScheduleStats();
            RenderSchedule();
        }
        catch (Exception ex)
        {
            SetScheduleStatus("课表读取失败：" + ex.Message);
        }
        finally
        {
            if (scheduleRefresh != null) scheduleRefresh.IsEnabled = true;
        }
    }

    private static void RenderScheduleStats()
    {
        if (scheduleStats == null || scheduleData == null) return;
        scheduleStats.Children.Clear();
        int todayCount = (scheduleData.TodayEntries ?? new List<CompanionStudyClient.ScheduleEntry>()).Count;
        int total = (scheduleData.Entries ?? new List<CompanionStudyClient.ScheduleEntry>()).Count;
        // 单行紧凑摘要，不放大数字统计块，为表格腾高度
        var summary = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        summary.Children.Add(T("本周教学", CompanionTheme.SmallSize, CompanionTheme.Subtle));
        var week = T(scheduleData.Week.ToString(), CompanionTheme.SectionSize, CompanionTheme.Brand, FontWeights.SemiBold);
        week.Margin = new Thickness(6, 0, 18, 0);
        summary.Children.Add(week);
        summary.Children.Add(T($"今天 {todayCount} 节", CompanionTheme.SmallSize, CompanionTheme.InkSoft));
        var sep = T("   ·   ", CompanionTheme.SmallSize, CompanionTheme.Hairline);
        summary.Children.Add(sep);
        summary.Children.Add(T($"本学期 {total} 条", CompanionTheme.SmallSize, CompanionTheme.InkSoft));
        var sep2 = T("   ·   ", CompanionTheme.SmallSize, CompanionTheme.Hairline);
        summary.Children.Add(sep2);
        summary.Children.Add(T("蓝色列为今天，非本周淡化", CompanionTheme.SmallSize, CompanionTheme.Subtle));
        scheduleStats.Children.Add(summary);
    }

    private static readonly string[] DayNames =
        { "星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日" };

    private static void RenderSchedule()
    {
        if (scheduleRows == null) return;
        scheduleRows.Children.Clear();
        // 重建后回到顶部，否则沿用上一页的滚动偏移会把表头/第一行裁掉。
        // 用 Dispatcher 延后到布局完成后再滚一次，否则 Grid 还没测量时 ScrollToTop 无效。
        scheduleScroll?.ScrollToTop();
        scheduleScroll?.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() => scheduleScroll?.ScrollToTop()));
        if (scheduleData == null)
        {
            var empty = T("课表还没加载好，点「重新同步」再试一次。", CompanionTheme.BodySize, CompanionTheme.Subtle);
            empty.Margin = new Thickness(4, 24, 4, 8);
            scheduleRows.Children.Add(empty);
            return;
        }

        bool onlyWeek = onlyThisWeekBox?.IsChecked ?? false;
        var all = scheduleData.Entries ?? new List<CompanionStudyClient.ScheduleEntry>();
        var shown = onlyWeek ? all.Where(x => x.ActiveThisWeek).ToList() : all.ToList();
        SetScheduleStatus(shown.Count == 0
            ? "这个筛选条件下没有课程，取消「只看本周」可以看到整学期。"
            : $"共 {shown.Count} 门课程安排 · 蓝色列为今天 · 点课程跳到对应课件");

        /* 表格化课表：第一行 = 星期一…星期日，第一列 = 节次 / 时间，
         * 交叉单元格放课程。节次与时间以教务系统返回为准，不写死。 */
        scheduleRows.Children.Add(BuildScheduleGrid(shown));
    }

    /// <summary>
    /// 构造「星期为列、节次为行」的课表网格。
    /// 列 0 = 节次/时间（固定宽），列 1..7 = 星期一…星期日（固定宽），
    /// 行 0 = 表头，行 1..N = 各节次。今天列整列淡蓝底 + 表头实心品牌色。
    /// </summary>
    private static FrameworkElement BuildScheduleGrid(List<CompanionStudyClient.ScheduleEntry> shown)
    {
        // 列宽按窗口内容区（1180 - 2×40 = 1100px）设计，让 7 天在默认窗口下完整显示，
        // 不出现横向滚动条；窗口缩窄时 ScrollViewer 才接管横向滚动。
        const double timeW = 92;
        const double dayW = 136;
        const double headerH = 40;

        var periods = BuildPeriods();
        var grid = new Grid
        {
            MinWidth = timeW + dayW * 7,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,   // 纵向铺满可用高度
            ClipToBounds = true
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(timeW) });
        for (int d = 1; d <= 7; d++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(dayW) });
        // 表头固定高；各节次行用 Star 均分剩余高度 → 整张课表恰好一屏呈现，不需要纵向滚动
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerH) });
        foreach (var _ in periods)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        int today = scheduleData?.Today ?? 0;

        /* 表头：左上角的「节次 / 时间」 + 七个星期。今天的表头用实心品牌色 + 白字。 */
        grid.Children.Add(GridCell(
            T("节次 / 时间", CompanionTheme.SmallSize, CompanionTheme.Subtle, FontWeights.SemiBold),
            0, 0, CompanionTheme.Soft));
        for (int d = 1; d <= 7; d++)
        {
            bool isToday = d == today;
            var headText = new TextBlock
            {
                Text = DayNames[d - 1],
                FontSize = CompanionTheme.SmallSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = isToday ? Brushes.White : CompanionTheme.InkSoft,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            if (isToday)
            {
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var head = new TextBlock
                {
                    Text = DayNames[d - 1],
                    FontSize = CompanionTheme.SmallSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var sub = new TextBlock
                {
                    Text = "今天",
                    FontSize = CompanionTheme.MicroSize - 1,
                    Foreground = Brushes.White,
                    Opacity = 0.9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                stack.Children.Add(head);
                stack.Children.Add(sub);
                grid.Children.Add(GridCell(stack, d, 0, CompanionTheme.Brand));
            }
            else
            {
                grid.Children.Add(GridCell(headText, d, 0, CompanionTheme.Soft));
            }
        }

        /* 数据行：第一列节次/时间，随后每天一个单元格。 */
        for (int r = 0; r < periods.Count; r++)
        {
            var p = periods[r];
            var timeStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
            timeStack.Children.Add(T($"第 {p.Period} 节", CompanionTheme.BodySize, CompanionTheme.Ink, FontWeights.SemiBold));
            if (!string.IsNullOrWhiteSpace(p.Time))
            {
                var tm = T(p.Time, CompanionTheme.MicroSize, CompanionTheme.Subtle);
                tm.Margin = new Thickness(0, 4, 0, 0);
                timeStack.Children.Add(tm);
            }
            grid.Children.Add(GridCell(timeStack, 0, r + 1, CompanionTheme.Raised));

            for (int d = 1; d <= 7; d++)
            {
                bool isToday = d == today;
                var bg = isToday ? CompanionTheme.BrandWash : CompanionTheme.Raised;
                var entries = shown.Where(x => x.Day == d && x.Period == p.Period)
                    .OrderBy(x => x.Name).ToList();
                grid.Children.Add(GridCell(BuildDayCell(entries), d, r + 1, bg));
            }
        }

        return grid;
    }

    /// <summary>节次与时间：优先用教务系统返回的 periods，退化到条目去重，再退化到内置默认值。</summary>
    private static List<CompanionStudyClient.SchedulePeriod> BuildPeriods()
    {
        var periods = (scheduleData?.Periods ?? new List<CompanionStudyClient.SchedulePeriod>())
            .Where(x => x.Period > 0).GroupBy(x => x.Period).Select(g => g.First())
            .OrderBy(x => x.Period).ToList();
        if (periods.Count == 0)
        {
            periods = (scheduleData?.Entries ?? new List<CompanionStudyClient.ScheduleEntry>())
                .Where(x => x.Period > 0).GroupBy(x => x.Period)
                .Select(g => new CompanionStudyClient.SchedulePeriod { Period = g.Key, Time = g.First().Time })
                .OrderBy(x => x.Period).ToList();
        }
        if (periods.Count == 0)
        {
            var fallback = new[] { "08:00-09:50", "10:10-12:00", "12:10-14:00", "14:10-16:00", "16:20-18:10", "19:00-20:50" };
            for (int i = 0; i < fallback.Length; i++)
                periods.Add(new CompanionStudyClient.SchedulePeriod { Period = i + 1, Time = fallback[i] });
        }
        return periods;
    }

    /// <summary>单个单元格：无课留白；有课则渲染成可点卡片，突出课程名（重）与地点（次），周次作淡标签。</summary>
    private static FrameworkElement BuildDayCell(List<CompanionStudyClient.ScheduleEntry> entries)
    {
        if (entries.Count == 0)
            return new Border { Background = Brushes.Transparent };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 1, 2, 1) };
        foreach (var e in entries)
        {
            bool active = scheduleData == null || scheduleData.Week <= 0 || e.ActiveThisWeek;
            bool coursesReady = coursesView != null && coursesView.Courses != null && coursesView.Courses.Count > 0;
            var matched = coursesReady ? FindCoursewareCourse(e.Code, e.Name) : null;

            /* 重点信息层级：课程名（粗、深）> 地点/老师（小、次）> 周次（淡）。
             * 本周课程左侧一条品牌色细竖条；非本周整卡淡化，仍可点。
             * 单元格高度固定，副信息一律单行省略，避免换行把下方内容裁掉。 */
            var card = new StackPanel();
            var nameRow = new Grid();
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var stripe = new Border
            {
                Width = 3, CornerRadius = new CornerRadius(2),
                Background = active ? CompanionTheme.Brand : CompanionTheme.Hairline,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 1, 8, 1)
            };
            Grid.SetColumn(stripe, 0); nameRow.Children.Add(stripe);
            var name = T(e.Name, CompanionTheme.BodySize,
                active ? CompanionTheme.Ink : CompanionTheme.Subtle,
                FontWeights.SemiBold);
            name.TextWrapping = TextWrapping.Wrap;
            name.MaxHeight = CompanionTheme.BodySize * 2.4;   // 课程名最多两行
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.ToolTip = e.Name;
            Grid.SetColumn(name, 1); nameRow.Children.Add(name);
            card.Children.Add(nameRow);

            var detailParts = new[] { e.ShortPlace, e.Teacher }.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (detailParts.Count > 0)
            {
                var detail = T(string.Join(" · ", detailParts), CompanionTheme.MicroSize - 0.5, CompanionTheme.InkSoft);
                detail.Margin = new Thickness(11, 3, 0, 0);
                detail.ToolTip = detail.Text;
                card.Children.Add(detail);
            }
            if (!string.IsNullOrWhiteSpace(e.Weeks))
            {
                var wk = T("第 " + e.Weeks + " 周" + (active ? " · 本周" : ""), CompanionTheme.MicroSize - 0.5,
                    active ? CompanionTheme.Brand : CompanionTheme.Subtle);
                wk.Margin = new Thickness(11, 2, 0, 0);
                card.Children.Add(wk);
            }

            var cell = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 2),
                Child = card,
                Cursor = Cursors.Hand,
                Opacity = active ? 1 : 0.55,
                ToolTip = matched != null
                    ? $"跳到课程课件：{matched.Name}（代码 {matched.Key}）"
                    : "点一下查这门课有没有课件"
            };
            cell.MouseEnter += (_, _) => cell.Background = CompanionTheme.BrandSoft;
            cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
            var captured = e;
            cell.MouseLeftButtonDown += (_, _) => _ = OpenCoursewareForAsync(captured.Code, captured.Name);
            stack.Children.Add(cell);
        }
        return stack;
    }

    /// <summary>
    /// 把内容放进网格的一个单元格：设置行列、底色与 1px 细描边（即表格线）。
    /// 用极淡 Hairline 只描右/下边，避免双线叠加变粗。
    /// </summary>
    private static Border GridCell(UIElement content, int col, int row, Brush background)
    {
        var cell = new Border
        {
            Background = background,
            BorderBrush = CompanionTheme.Hairline,
            BorderThickness = new Thickness(0, 0, 1, 1),
            ClipToBounds = true,   // Star 行变矮时，超出的副信息裁剪掉而不撑破表格
            Child = new Border { Child = content, Padding = new Thickness(5, 3, 5, 3) }
        };
        Grid.SetColumn(cell, col);
        Grid.SetRow(cell, row);
        return cell;
    }

    /// <summary>
    /// 课表 → 课件的主关联逻辑：课程代码（code ↔ key）精确匹配优先，
    /// 课程名去括号/标点后做兜底。老师名在课表与课程平台可能不同，不参与匹配。
    /// </summary>
    private static CompanionStudyClient.CoursewareCourse? FindCoursewareCourse(string code, string name)
    {
        var list = coursesView?.Courses;
        if (list == null || list.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(code))
        {
            var byCode = list.FirstOrDefault(c =>
                c.Key.Equals(code, StringComparison.OrdinalIgnoreCase) ||
                c.CourseNum.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (byCode != null) return byCode;
        }
        var n = NormCourseName(name);
        if (n.Length < 2) return null;
        var byName = list.FirstOrDefault(c => NormCourseName(c.Name) == n);
        if (byName != null) return byName;
        return list.FirstOrDefault(c =>
        {
            var cn = NormCourseName(c.Name);
            return cn.Length >= 2 &&
                (cn.Contains(n, StringComparison.Ordinal) || n.Contains(cn, StringComparison.Ordinal));
        });
    }

    private static string NormCourseName(string s)
    {
        var t = (s ?? "").Trim();
        // 课表课程名常带班号 / 校区的括号说明，先整段去掉
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[（(【\[].*?[)）】\]]", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[\s·・、，,。.\-—_（）()【】\[\]]", "");
        return t;
    }

    /// <summary>
    /// 点击课表课程后：确保课程列表就绪 → 匹配 → 切到「课程课件」并选中该课，
    /// 由 ComboBox 的 SelectionChanged 自动带出目录与文件。UI 与测试模式共用此路径。
    /// </summary>
    internal static async Task<bool> OpenCoursewareForAsync(string code, string name)
    {
        if (client == null) return false;
        if (coursesView == null || coursesView.Courses == null || coursesView.Courses.Count == 0)
            await LoadCoursesAsync(false);

        var match = FindCoursewareCourse(code, name);
        if (match == null)
        {
            var msg = $"课表里的「{name}」在课程平台没有对应课件（代码 {code}），这门课可能不提供资料。";
            SetScheduleStatus(msg);
            if (pageSubtitle != null) pageSubtitle.Text = msg;
            return false;
        }

        SelectTab(1);
        var item = courseBox?.Items.OfType<ComboItem>().FirstOrDefault(x => x.Value == match.Key);
        if (item != null && courseBox != null)
        {
            // 不 suppress：要靠 SelectionChanged 自动加载这门课的目录与文件
            courseBox.SelectedItem = item;
            if (pageSubtitle != null)
                pageSubtitle.Text = $"已从课表进入「{match.Name}」的课件 · 课程代码 {match.Key}";
            return true;
        }
        SetCourseStatus($"已切到课程课件，但列表里没有「{match.Name}」，请手动选择。");
        return false;
    }

    private static void SetScheduleStatus(string text)
    {
        if (scheduleStatus != null) scheduleStatus.Text = text;
    }

    private static string DayName(int day) => day is >= 1 and <= 7
        ? new[] { "星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日" }[day - 1]
        : "未知";

    /* ---------- 课程资料页 ---------- */

    private static Grid BuildCoursewareTab()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var filter = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var comboRow = new WrapPanel();
        termBox = CompanionTheme.Combo(216);
        termBox.SelectionChanged += (_, _) =>
        {
            if (suppressing || termBox?.SelectedItem is not ComboItem t) return;
            _ = LoadCoursesAsync(false, t.Value);
        };
        courseBox = CompanionTheme.Combo(220);
        courseBox.SelectionChanged += (_, _) =>
        {
            if (suppressing || courseBox?.SelectedItem is not ComboItem c) return;
            _ = LoadFoldersAsync(c.Value);
        };
        folderBox = CompanionTheme.Combo(168);
        folderBox.SelectionChanged += (_, _) =>
        {
            if (suppressing || folderBox?.SelectedItem is not ComboItem f) return;
            var ck = (courseBox?.SelectedItem as ComboItem)?.Value ?? "";
            _ = LoadFilesAsync(ck, f.Value, f.Text);
        };
        comboRow.Children.Add(Labeled("学期", termBox));
        comboRow.Children.Add(Labeled("课程", courseBox));
        comboRow.Children.Add(Labeled("目录", folderBox));
        Grid.SetColumn(comboRow, 0);
        filter.Children.Add(comboRow);

        courseRefresh = CompanionTheme.Button("刷新课程");
        courseRefresh.Margin = new Thickness(0, 0, 8, 0);
        courseRefresh.Click += (_, _) => _ = LoadCoursesAsync(true);
        Grid.SetColumn(courseRefresh, 1);
        filter.Children.Add(courseRefresh);

        var openDir = CompanionTheme.Button("打开目录");
        openDir.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(downloadDir)) OpenWithShell(downloadDir, "下载目录");
        };
        Grid.SetColumn(openDir, 2);
        filter.Children.Add(openDir);
        Grid.SetRow(filter, 0);
        grid.Children.Add(filter);

        courseStatus = T("正在读取课程列表（首次要登录学校系统，约 5-10 秒）…",
            CompanionTheme.SmallSize, CompanionTheme.Subtle);
        courseStatus.Margin = new Thickness(2, 0, 2, 16);
        courseStatus.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(courseStatus, 1);
        grid.Children.Add(courseStatus);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        /* 左：文件列表卡片 */
        var leftStack = new StackPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var all = CompanionTheme.Button("全选");
        all.Click += (_, _) => { foreach (var f in currentFiles) f.Checked = true; RenderFiles(); };
        var none = CompanionTheme.Button("取消全选");
        none.Click += (_, _) => { foreach (var f in currentFiles) f.Checked = false; RenderFiles(); };
        downloadButton = CompanionTheme.Button("下载选中课件", true);
        downloadButton.Click += (_, _) => _ = DownloadSelectedAsync();
        foreach (var b in new[] { all, none, downloadButton }) { b.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(b); }
        leftStack.Children.Add(actions);
        leftStack.Children.Add(CompanionTheme.Rule(0, 4));

        fileRows = new StackPanel();
        var fileScroll = new ScrollViewer
        {
            Content = fileRows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 4, 6, 0)
        };
        leftStack.Children.Add(fileScroll);
        var leftCard = CompanionTheme.Panel(leftStack, new Thickness(18, 16, 18, 14));
        leftCard.Margin = new Thickness(0, 0, 14, 0);
        Grid.SetColumn(leftCard, 0);
        body.Children.Add(leftCard);

        /* 右：本机课件 */
        var localStack = new StackPanel();
        localStack.Children.Add(CompanionTheme.Section("已下载到本机"));
        dirText = T("目录读取中…", CompanionTheme.MicroSize, CompanionTheme.Subtle);
        dirText.Margin = new Thickness(0, 7, 0, 0);
        dirText.TextWrapping = TextWrapping.Wrap;
        localStack.Children.Add(dirText);
        localSummary = T("—", CompanionTheme.BodySize, CompanionTheme.Ink, FontWeights.SemiBold);
        localSummary.Margin = new Thickness(0, 12, 0, 10);
        localStack.Children.Add(localSummary);
        localRows = new StackPanel();
        var localScroll = new ScrollViewer
        {
            Content = localRows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        localStack.Children.Add(localScroll);
        var rightCard = CompanionTheme.Panel(localStack, new Thickness(18, 16, 18, 14));
        Grid.SetColumn(rightCard, 1);
        body.Children.Add(rightCard);

        Grid.SetRow(body, 2);
        grid.Children.Add(body);

        var hint = T("下载优先取原件（PPT / DOCX 等按真实文件类型保存）；原件不可用时自动存转换版 PDF，已存在的文件会跳过。",
            CompanionTheme.MicroSize, CompanionTheme.Subtle);
        hint.Margin = new Thickness(2, 14, 2, 0);
        hint.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(hint, 3);
        grid.Children.Add(hint);
        return grid;
    }

    private static async Task LoadCoursesAsync(bool refresh, string? xqCode = null)
    {
        if (client == null || termBox == null || courseBox == null) return;
        if (courseRefresh != null) courseRefresh.IsEnabled = false;
        SetCourseStatus(refresh ? "正在重新登录并拉取课程…" : "正在读取课程列表…");
        try
        {
            var v = await client.GetCoursesAsync(xqCode ?? "", refresh, CancellationToken.None);
            if (!v.Reachable || !v.Ok)
            {
                SetCourseStatus("课程读取失败：" + (v.TransportError.Length > 0 ? v.TransportError : v.Error));
                return;
            }
            coursesView = v;
            downloadDir = v.DownloadDir ?? "";
            if (dirText != null) dirText.Text = "目录：" + downloadDir;

            var terms = v.Terms ?? new List<CompanionStudyClient.CoursewareTerm>();
            WithSuppress(() =>
            {
                termBox.Items.Clear();
                foreach (var t in terms)
                    termBox.Items.Add(new ComboItem(t.Code, t.Name + (t.Current ? "（当前）" : "")));
                if (termBox.Items.Count > 0)
                {
                    int idx = terms.FindIndex(t => t.Current);
                    termBox.SelectedIndex = idx >= 0 ? idx : 0;
                }
                courseBox.Items.Clear();
                foreach (var c in v.Courses ?? new List<CompanionStudyClient.CoursewareCourse>())
                    courseBox.Items.Add(new ComboItem(c.Key, c.Name + (string.IsNullOrEmpty(c.Teacher) ? "" : " · " + c.Teacher)));
                if (courseBox.Items.Count > 0) courseBox.SelectedIndex = 0;
            });
            SetCourseStatus($"{v.XqName} · 共 {v.Courses?.Count ?? 0} 门课程");
            var first = (courseBox.SelectedItem as ComboItem)?.Value ?? "";
            if (!string.IsNullOrEmpty(first)) await LoadFoldersAsync(first);
        }
        finally
        {
            if (courseRefresh != null) courseRefresh.IsEnabled = true;
        }
    }

    private static async Task LoadFoldersAsync(string courseKey)
    {
        if (client == null || string.IsNullOrEmpty(courseKey) || folderBox == null) return;
        var v = await client.GetFoldersAsync(courseKey, CancellationToken.None);
        if (!v.Ok)
        {
            SetCourseStatus("目录读取失败：" + v.Error);
            return;
        }
        var folders = v.Folders ?? new List<CompanionStudyClient.CoursewareFolder>();
        WithSuppress(() =>
        {
            folderBox.Items.Clear();
            foreach (var f in folders)
                folderBox.Items.Add(new ComboItem(f.Id, f.Name));
            if (folderBox.Items.Count > 0) folderBox.SelectedIndex = 0;
        });

        /* 顶层「电子课件」常是空容器，而「教学演示系统」只有两个上百 MB 的 zip。
         * 逐个目录扫一遍，优先选「有 PPT / PDF 且文件多」的，打开就看到课件本体。 */
        CompanionStudyClient.CoursewareFolder? best = null;
        List<CompanionStudyClient.CoursewareFile>? bestFiles = null;
        int bestScore = -1;
        foreach (var f in folders)
        {
            var files = await client.GetFilesAsync(courseKey, f.Id, CancellationToken.None);
            if (!files.Ok || files.Files == null || files.Files.Count == 0) continue;
            bool hasSlide = files.Files.Any(x =>
                x.Ext.Contains("ppt", StringComparison.OrdinalIgnoreCase) ||
                x.Ext.Contains("pdf", StringComparison.OrdinalIgnoreCase));
            int score = files.Files.Count + (hasSlide ? 100 : 0);
            if (score > bestScore)
            {
                bestScore = score;
                best = f;
                bestFiles = files.Files;
            }
        }
        if (best != null && bestFiles != null)
        {
            WithSuppress(() => folderBox.SelectedItem = folderBox.Items
                .OfType<ComboItem>().FirstOrDefault(x => x.Value == best.Id));
            currentFiles.Clear();
            currentFiles.AddRange(bestFiles);
            RenderFiles();
            SetCourseStatus($"{v.Course} · {folders.Count} 个目录 · 当前「{best.Name}」{bestFiles.Count} 个文件");
            return;
        }
        currentFiles.Clear();
        RenderFiles();
        SetCourseStatus($"{v.Course} · {folders.Count} 个目录，暂时都没有文件");
    }

    private static async Task LoadFilesAsync(string courseKey, string folderId, string folderName)
    {
        if (client == null || string.IsNullOrEmpty(courseKey)) return;
        var v = await client.GetFilesAsync(courseKey, folderId, CancellationToken.None);
        currentFiles.Clear();
        if (v.Ok && v.Files != null) currentFiles.AddRange(v.Files);
        RenderFiles();
        SetCourseStatus(v.Ok
            ? $"「{folderName}」共 {currentFiles.Count} 个文件"
            : "文件读取失败：" + v.Error);
    }

    private static void RenderFiles()
    {
        if (fileRows == null) return;
        fileRows.Children.Clear();
        // 列表重建后回到顶部，否则沿用上一次滚动偏移会把第一行裁掉半截
        (fileRows.Parent as ScrollViewer)?.ScrollToTop();
        if (currentFiles.Count == 0)
        {
            var empty = T("这个目录暂时没有课件文件，换一个课程或目录看看。",
                CompanionTheme.BodySize, CompanionTheme.Subtle);
            empty.Margin = new Thickness(4, 20, 4, 8);
            fileRows.Children.Add(empty);
            return;
        }
        foreach (var f in currentFiles)
        {
            var cb = new CheckBox
            {
                IsChecked = f.Checked,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 12, 0)
            };
            cb.Checked += (_, _) => { f.Checked = true; };
            cb.Unchecked += (_, _) => { f.Checked = false; };

            var name = T(f.Name, CompanionTheme.BodySize, CompanionTheme.Ink);
            name.ToolTip = f.Name;
            var ext = T(string.IsNullOrEmpty(f.Ext) ? "文件" : f.Ext.ToUpperInvariant(),
                CompanionTheme.MicroSize, CompanionTheme.Subtle);
            ext.Width = 56;
            var size = T(f.SizeMB > 0 ? f.SizeMB.ToString("0.##") + " MB" : "—",
                CompanionTheme.MicroSize, CompanionTheme.Subtle);
            size.Width = 78;
            size.TextAlignment = TextAlignment.Right;

            var g = new Grid { MinHeight = 42 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(cb, 0); g.Children.Add(cb);
            Grid.SetColumn(name, 1); g.Children.Add(name);
            Grid.SetColumn(ext, 2); g.Children.Add(ext);
            Grid.SetColumn(size, 3); g.Children.Add(size);

            var row = new Border
            {
                Background = f.Checked ? CompanionTheme.BrandSoft : Brushes.Transparent,
                BorderBrush = CompanionTheme.Hairline,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(6, 6, 6, 6),
                Child = g
            };
            fileRows.Children.Add(row);
        }
    }

    private static async Task DownloadSelectedAsync()
    {
        if (client == null || downloadButton == null) return;
        var ids = currentFiles.Where(f => f.Checked).Select(f => f.Id).ToList();
        if (ids.Count == 0)
        {
            SetCourseStatus("请先勾选要下载的课件，或点「全选」。");
            return;
        }
        var courseKey = (courseBox?.SelectedItem as ComboItem)?.Value ?? "";
        var folder = folderBox?.SelectedItem as ComboItem;
        if (string.IsNullOrEmpty(courseKey) || folder == null) return;

        downloadButton.IsEnabled = false;
        downloadButton.Content = $"下载中（{ids.Count} 个）…";
        SetCourseStatus($"正在下载 {ids.Count} 个课件，大文件可能需要一会儿…");
        try
        {
            var r = await client.DownloadAsync(courseKey, folder.Value, folder.Text, ids, CancellationToken.None);
            if (!r.Ok)
            {
                SetCourseStatus("下载失败：" + r.Error);
                return;
            }
            SetCourseStatus($"完成：新下载 {r.Downloaded} 个 · 已存在跳过 {r.Skipped} 个 · 失败 {r.Failed} 个" +
                (string.IsNullOrEmpty(r.Dir) ? "" : "　保存到 " + r.Dir));
            var failed = (r.Results ?? new List<CompanionStudyClient.CoursewareDownloadItem>())
                .Where(x => x.Status == "failed").Take(2).Select(x => x.Name + "：" + x.Note).ToList();
            if (failed.Count > 0) SetCourseStatus("部分失败：" + string.Join("；", failed));
            await LoadLocalAsync();
        }
        finally
        {
            downloadButton.IsEnabled = true;
            downloadButton.Content = "下载选中课件";
        }
    }

    private static async Task LoadLocalAsync()
    {
        if (client == null) return;
        var v = await client.GetLocalAsync(CancellationToken.None);
        if (localRows == null) return;
        if (!string.IsNullOrEmpty(v.Dir))
        {
            downloadDir = v.Dir;
            if (dirText != null) dirText.Text = "目录：" + v.Dir;
        }
        localRows.Children.Clear();
        if (localSummary != null)
            localSummary.Text = v.Ok ? $"本机共 {v.Count} 个课件" : "本地目录读取失败：" + v.Error;
        foreach (var f in (v.Items ?? new List<CompanionStudyClient.LocalFileItem>()).Take(12))
        {
            var stack = new StackPanel();
            var n = T(f.Name, CompanionTheme.SmallSize, CompanionTheme.Ink);
            stack.Children.Add(n);
            var sub = T((f.Size / 1024.0 / 1024.0).ToString("0.##") + " MB · " + f.Rel,
                CompanionTheme.MicroSize, CompanionTheme.Subtle);
            sub.Margin = new Thickness(0, 3, 0, 0);
            stack.Children.Add(sub);
            var row = new Border
            {
                BorderBrush = CompanionTheme.Hairline,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(2, 9, 2, 9),
                Child = stack,
                Cursor = Cursors.Hand,
                ToolTip = f.Path
            };
            row.MouseEnter += (_, _) => row.Background = CompanionTheme.Soft;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) OpenWithShell(f.Path, "课件文件");
            };
            localRows.Children.Add(row);
        }
    }

    /* ---------- 小工具 ---------- */

    private static TextBlock T(string text, double size, Brush fg, FontWeight? weight = null) =>
        CompanionTheme.Txt(text, size, fg, weight);

    private static void SetCourseStatus(string text)
    {
        if (courseStatus != null) courseStatus.Text = text;
    }

    private static void WithSuppress(Action action)
    {
        suppressing = true;
        try { action(); } finally { suppressing = false; }
    }

    private static void OpenWithShell(string target, string what)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            SetCourseStatus($"打不开{what}：{ex.Message}");
            if (pageSubtitle != null && what == "作业页面")
                pageSubtitle.Text = $"打不开{what}：{ex.Message}";
        }
    }

    private static StackPanel Labeled(string text, UIElement control)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        var label = T(text, CompanionTheme.MicroSize, CompanionTheme.Subtle);
        label.Margin = new Thickness(2, 0, 0, 5);
        p.Children.Add(label);
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

    /* ---------- 测试模式：自渲染截图 ---------- */

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
                try { File.AppendAllText("companion-errors.log",
                    DateTime.Now.ToString("s") + " study-hub-shot 失败：" + ex.Message + Environment.NewLine); }
                catch { }
            }
        }));
    }
}
