using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BigBlueFish.Fate;
// 注意：BigBlueFish.Control 是同级命名空间，简单名 Control 会被解析为命名空间而非 WPF 控件类型；本文件内一律用全限定名。

namespace BigBlueFish.Windows;

/// <summary>
/// 命运罗盘 · 本地命理工具箱（纯代码建窗，无 XAML）。
/// 视觉基调：米白纸面 + 靛蓝墨色 + 鎏金点缀；表单与结果均为圆角卡片，柔和投影。
/// 注：不可给桌宠主窗口（WS_EX_NOACTIVATE）当 Owner 弹模态窗——宿主无法激活时模态窗链路会失效。
/// </summary>
public static class CompanionFateWindow
{
    // ── 主题色板：统一由 CompanionTheme 提供，课表 / 空教室 / 罗盘 / 学习中心共用同一套 token ──
    internal static readonly SolidColorBrush Ink = CompanionTheme.Ink;
    internal static readonly SolidColorBrush Muted = CompanionTheme.Muted;
    internal static readonly SolidColorBrush Accent = CompanionTheme.Accent;
    internal static readonly SolidColorBrush AccentHover = CompanionTheme.AccentHover;
    internal static readonly SolidColorBrush Gold = CompanionTheme.Gold;
    internal static readonly SolidColorBrush Paper = CompanionTheme.Paper;
    internal static readonly SolidColorBrush Card = CompanionTheme.Card;
    internal static readonly SolidColorBrush Line = CompanionTheme.Line;
    internal static readonly SolidColorBrush Field = CompanionTheme.Field;
    internal static readonly SolidColorBrush Hover = CompanionTheme.Hover;
    // 单实例：已打开的罗盘不再重复弹窗，改为激活复用。
    private static Window? _instance;

    public static void Show()
    {
        if (_instance != null)
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
            return;
        }

        // ── 输入控件 ──
        var date = new DatePicker { SelectedDate = DateTime.Today, Width = 132, Height = 34, FontSize = 13 };
        var hour = new ComboBox { Width = 92, Height = 34, FontSize = 13, ItemsSource = Enumerable.Range(0, 24).Select(x => $"{x:00} 时").ToList(), SelectedIndex = Math.Clamp(DateTime.Now.Hour, 0, 23) };
        var male = new RadioButton { Content = "男", IsChecked = true, Margin = new Thickness(0, 0, 16, 0), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var female = new RadioButton { Content = "女", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var question = new TextBox { Text = "", Width = 206, Height = 34, FontSize = 13, Padding = new Thickness(8, 6, 8, 6), ToolTip = "六爻所问之事（可留空）" };
        var shuA = new TextBox { Text = "7", Width = 50, Height = 34, FontSize = 13, Padding = new Thickness(8, 6, 8, 6) };
        var shuB = new TextBox { Text = "3", Width = 50, Height = 34, FontSize = 13, Padding = new Thickness(8, 6, 8, 6) };
        var useNow = new CheckBox { Content = "按当前时间起卦", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

        SetFieldStyle(question);
        SetFieldStyle(shuA);
        SetFieldStyle(shuB);
        date.Background = Field;
        date.BorderBrush = Line;
        hour.Background = Field;
        hour.BorderBrush = Line;

        // ── 表单卡片 ──
        var gender = new StackPanel { Orientation = Orientation.Horizontal };
        gender.Children.Add(male);
        gender.Children.Add(female);

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddField(form, 0, 0, "出生日期", date);
        AddField(form, 0, 1, "出生时辰", hour);
        AddField(form, 0, 2, "性别", gender);

        var fateInputs = new StackPanel { Orientation = Orientation.Horizontal };
        fateInputs.Children.Add(Tag("六爻问题"));
        fateInputs.Children.Add(question);
        fateInputs.Children.Add(Tag("梅花数", 16));
        fateInputs.Children.Add(shuA);
        fateInputs.Children.Add(new TextBlock { Text = "/", FontSize = 14, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) });
        fateInputs.Children.Add(shuB);
        fateInputs.Children.Add(useNow);
        Grid.SetColumn(fateInputs, 0);
        Grid.SetColumnSpan(fateInputs, 4);
        Grid.SetRow(fateInputs, 2);
        form.Children.Add(fateInputs);

        var formCard = new Border
        {
            Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 16, 18, 16),
            Child = form, Effect = SoftShadow()
        };

        // ── 排盘按钮 ──
        var run = PrimaryButton("开始排盘", 112, 38);
        var runRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        runRow.Children.Add(run);

        // ── 分段页签 + 结果区 ──
        string[] tabNames = { "八字", "六爻", "梅花易数", "紫微斗数" };
        var pages = new List<UIElement>(4)
        {
            Placeholder("起好盘后，八字排盘结果将呈现在这里。"),
            Placeholder("默写心中所问，起卦后本卦、变卦与六爻在此展开。"),
            Placeholder("输入两个先天数起卦，或勾选「按当前时间起卦」。"),
            Placeholder("起好盘后，十二宫与十四主星会铺陈于此。")
        };
        var chips = new List<ToggleButton>(4);
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 10, 0, 0) };
        int selectedTab = 0;
        for (int i = 0; i < tabNames.Length; i++)
        {
            int idx = i;
            var chip = TabChip(tabNames[i]);
            chip.MouseEnter += (_, _) => { if (!chip.IsChecked.GetValueOrDefault()) chip.Background = Hover; };
            chip.MouseLeave += (_, _) => { if (!chip.IsChecked.GetValueOrDefault()) chip.Background = Card; };
            chip.Click += (_, _) =>
            {
                selectedTab = idx;
                scroll.Content = pages[idx];
                for (int j = 0; j < chips.Count; j++)
                {
                    bool on = j == idx;
                    chips[j].IsChecked = on;
                    chips[j].Background = on ? Accent : Card;
                    chips[j].Foreground = on ? Brushes.White : Ink;
                }
            };
            chips.Add(chip);
            chipRow.Children.Add(chip);
        }
        chips[0].Background = Accent;
        chips[0].Foreground = Brushes.White;
        scroll.Content = pages[0];

        // ── 标题区 ──
        var title = new TextBlock { Text = "命运罗盘", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Ink };
        var goldBar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = Gold, Margin = new Thickness(0, 2, 12, 2) };
        var titleRow = new DockPanel();
        titleRow.Children.Add(goldBar);
        titleRow.Children.Add(title);
        var subtitle = new TextBlock
        {
            Text = "本地规则计算 · 不上传出生信息 · 四盘同启",
            FontSize = 12, Foreground = Muted, Margin = new Thickness(16, 4, 0, 0)
        };

        // ── 主布局 ──
        var root = new Grid { Margin = new Thickness(26, 20, 26, 22) };
        for (int i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleRow, 0); root.Children.Add(titleRow);
        Grid.SetRow(subtitle, 1); root.Children.Add(subtitle);
        Grid.SetRow(formCard, 2); root.Children.Add(formCard);
        Grid.SetRow(runRow, 3); root.Children.Add(runRow);
        Grid.SetRow(chipRow, 4); root.Children.Add(chipRow);
        Grid.SetRow(scroll, 5); root.Children.Add(scroll);

        var window = new Window
        {
            Title = "命运罗盘 · 本地命理工具箱",
            Width = 800, Height = 720, MinWidth = 700, MinHeight = 620,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false, Topmost = true,
            Content = root,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Background = Paper
        };

        run.Click += (_, _) =>
        {
            if (date.SelectedDate is not DateTime selectedDate) return;
            int hourValue = hour.SelectedIndex;
            int genderValue = male.IsChecked == true ? 1 : 0;
            try
            {
                pages[0] = BuildBazi(BaziEngine.Chart(selectedDate.Year, selectedDate.Month, selectedDate.Day, hourValue, 0, genderValue));
                pages[1] = BuildLiuYao(question.Text.Trim());
                MeiHuaResult meiHua = useNow.IsChecked == true
                    ? MeiHuaEngine.Cast()
                    : MeiHuaEngine.Cast(ParseNumber(shuA.Text, "梅花上卦数"), ParseNumber(shuB.Text, "梅花下卦数"));
                pages[2] = BuildMeiHua(meiHua);
                pages[3] = BuildZiWei(ZiWeiEngine.Chart(selectedDate.Year, selectedDate.Month, selectedDate.Day, hourValue, 0, genderValue));
                scroll.Content = pages[selectedTab];
            }
            catch (Exception ex)
            {
                MessageBox.Show(window, ex is ArgumentException or FormatException or OverflowException
                    ? ex.Message
                    : "排盘暂时失败，请检查输入后重试。", "排盘失败", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        };

        window.Loaded += (_, _) => run.Focus();
        window.Closed += (_, _) => _instance = null;
        _instance = window;
        window.Show();
    }

    // ── 视觉组件 ──
    internal static DropShadowEffect SoftShadow() => new()
    { Color = Color.FromRgb(120, 100, 60), BlurRadius = 18, ShadowDepth = 0, Opacity = 0.09 };

    private static TextBlock Tag(string text, double leftGap = 0) => new()
    {
        Text = text, FontSize = 12.5, Foreground = Muted,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(leftGap, 0, 8, 0)
    };

    private static void AddField(Grid grid, int row, int column, string label, UIElement control)
    {
        var title = new TextBlock
        {
            Text = label, FontSize = 12.5, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(column == 0 ? 0 : 26, 6, 10, 6)
        };
        Grid.SetRow(title, row);
        Grid.SetColumn(title, column);
        grid.Children.Add(title);
        Grid.SetRow(control, row + 1);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    private static void SetFieldStyle(TextBox box)
    {
        box.Background = Field;
        box.BorderBrush = Line;
        box.BorderThickness = new Thickness(1);
        var style = new Style(typeof(TextBox));
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, Accent));
        focus.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(1.5)));
        style.Triggers.Add(focus);
        box.Style = style;
    }

    private static Border CardBox(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children) panel.Children.Add(child);
        return new Border
        {
            Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 16, 20, 18),
            Margin = new Thickness(0, 0, 4, 6), Child = panel, Effect = SoftShadow()
        };
    }

    private static DockPanel SectionTitle(string title)
    {
        var bar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = Gold, Margin = new Thickness(0, 1, 10, 1) };
        var label = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center };
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        dock.Children.Add(bar);
        dock.Children.Add(label);
        return dock;
    }

    private static TextBlock Meta(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5,
        Foreground = new SolidColorBrush(Color.FromRgb(84, 92, 108)), LineHeight = 24, Margin = new Thickness(0, 2, 0, 0)
    };

    private static UIElement Placeholder(string text) => new Border
    {
        Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Effect = SoftShadow(),
        Margin = new Thickness(0, 0, 4, 6),
        Child = new TextBlock
        {
            Text = "✦  " + text, FontSize = 13.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        }
    };

    internal static Button PrimaryButton(string text, double width, double height)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
            Width = width, Height = height, Background = Accent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        button.Template = RoundTemplate(9, false);
        return button;
    }

    internal static ControlTemplate RoundTemplate(double radius, bool chip, Type? targetType = null)
    {
        var template = new ControlTemplate(targetType ?? typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        if (chip)
        {
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        }
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.MarginProperty, new Thickness(12, 0, 12, 0));
        border.AppendChild(presenter);
        template.VisualTree = border;
        // 实心按钮保留悬停加深/按下反馈；chip（描边）按钮悬停由调用方手动切换 Background——
        // 模板触发器会把本地值直接写到 Border 根并压过 TemplateBinding，导致前景/背景对比失配。
        if (!chip)
        {
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, AccentHover));
            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
            template.Triggers.Add(over);
            template.Triggers.Add(pressed);
        }
        return template;
    }

    private static ToggleButton TabChip(string text)
    {
        var chip = new ToggleButton
        {
            Content = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
            Height = 32, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(15, 0, 15, 0),
            Background = Card, Foreground = Ink, BorderBrush = Line,
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand
        };
        chip.Template = RoundTemplate(16, true, typeof(ToggleButton));
        return chip;
    }

    private static int ParseNumber(string text, string name)
    {
        if (!int.TryParse(text, out int value)) throw new FormatException($"{name}必须是整数。");
        return value;
    }

    // ── 四盘渲染 ──
    private static UIElement BuildBazi(BaziChart chart)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"公历：{chart.SolarText}    农历：{chart.LunarText}");
        sb.AppendLine(string.Join("　", chart.Pillars.Select(x => $"【{x.Name}】{x.GanZhi}")));
        sb.AppendLine($"日主：{chart.DayMaster}（{chart.DayMasterWuXing}）  {chart.StrengthText}");
        sb.AppendLine($"胎元：{chart.TaiYuan}    命宫：{chart.MingGong}    身宫：{chart.ShenGong}");
        sb.AppendLine($"起运：{chart.QiYunText}");
        sb.AppendLine("大运：" + string.Join(" → ", chart.DaYun.Select(x => $"{x.GanZhi}({x.StartAge}-{x.EndAge}岁)")));
        if (!string.IsNullOrEmpty(chart.LiuNian.GanZhi)) sb.AppendLine($"当前流年：{chart.LiuNian.GanZhi}");
        return CardBox(SectionTitle("八字排盘"), Meta(sb.ToString()));
    }

    private static UIElement BuildLiuYao(string question)
    {
        var random = Random.Shared;
        var yaos = new List<LiuYaoYaoType>(6);
        for (int i = 0; i < 6; i++)
        {
            int n = random.Next(8);
            yaos.Add(n switch { 0 => LiuYaoYaoType.LaoYin, <= 3 => LiuYaoYaoType.ShaoYin, <= 6 => LiuYaoYaoType.ShaoYang, _ => LiuYaoYaoType.LaoYang });
        }
        var result = LiuYaoEngine.Cast(question.Length == 0 ? "未填写问题" : question, yaos);
        var sb = new StringBuilder();
        sb.AppendLine($"所问：{result.Question}");
        sb.AppendLine($"本卦：{result.BenGua}    变卦：{(result.BianGua.Length == 0 ? "无" : result.BianGua)}");
        sb.AppendLine($"卦宫：{result.Gong}（{result.GongWuXing}）    世{result.ShiPos}爻，应{result.YingPos}爻");
        sb.AppendLine();
        foreach (var line in result.Lines.AsEnumerable().Reverse())
            sb.AppendLine($"{line.Name}  {line.Zhi}  {line.LiuQin}{(line.Dong ? " · 动" : "")}{(line.Dong ? $" → {line.BianZhi}{line.BianLiuQin}" : "")}");
        sb.AppendLine();
        sb.AppendLine(result.Verdict);
        return CardBox(SectionTitle("六爻起卦"), Meta(sb.ToString()));
    }

    private static UIElement BuildMeiHua(MeiHuaResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"取数：{result.ShuA} / {result.ShuB}    动爻：{result.DongYao}");
        sb.AppendLine($"本卦：{result.BenGua}    互卦：{result.HuGua}    变卦：{result.BianGua}");
        sb.AppendLine($"上卦：{result.ShangGua}（{result.ShangTri}）    下卦：{result.XiaGua}（{result.XiaTri}）");
        sb.AppendLine($"体卦：{result.TiGua}（{result.TiWuXing}）    用卦：{result.YongGua}（{result.YongWuXing}）");
        sb.AppendLine();
        sb.AppendLine(result.Verdict);
        return CardBox(SectionTitle("梅花易数"), Meta(sb.ToString()));
    }

    private static UIElement BuildZiWei(ZiWeiChart chart)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"公历：{chart.SolarText}    农历：{chart.LunarText}    生年：{chart.YearGanZhi}");
        sb.AppendLine($"命宫：{chart.MingGongGanZhi}    五行局：{chart.WuXingJu}    身宫序：{chart.ShenGongYin1}");
        sb.AppendLine();
        foreach (var palace in chart.Palaces)
        {
            string mark = palace.IsMingGong ? " · 命" : palace.IsShenGong ? " · 身" : "";
            string stars = palace.Stars.Count == 0 ? "无主星" : string.Join("、", palace.Stars);
            sb.AppendLine($"{palace.Name}{mark}  {palace.GanZhi}  {stars}");
        }
        return CardBox(SectionTitle("紫微斗数"), Meta(sb.ToString()));
    }
}