using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace BigBlueFish.Windows;

/// <summary>
/// 北交大肥鱼统一设计语言（2026-11-14 重构）。
///
/// 设计原则：浅色、克制、大留白。层级靠字号 / 留白 / 极淡分隔线表达，
/// 状态靠胶囊标签与细色条表达，不用整行染色、不用密集边框、不用系统默认控件外观。
///
/// 上半部分是桌宠既有色板（值保持不变），CompanionFateWindow 改为引用这里，
/// 于是课表 / 空教室 / 罗盘等窗口自动共用同一套 token，跨窗口风格一致。
/// 下半部分是学习中心与浏览器设置页共用的「精修」色板，色系与上半部分同源。
/// </summary>
internal static class CompanionTheme
{
    private static SolidColorBrush B(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /* ---------- 基础色板（与既有窗口完全一致，零视觉回归） ---------- */
    public static readonly SolidColorBrush Ink = B(42, 56, 83);
    public static readonly SolidColorBrush Muted = B(126, 130, 142);
    public static readonly SolidColorBrush Accent = B(80, 96, 152);
    public static readonly SolidColorBrush AccentHover = B(61, 76, 126);
    public static readonly SolidColorBrush Gold = B(197, 158, 96);
    public static readonly SolidColorBrush Paper = B(255, 251, 244);
    public static readonly SolidColorBrush Card = B(253, 250, 244);
    public static readonly SolidColorBrush Line = B(236, 230, 218);
    public static readonly SolidColorBrush Field = B(255, 255, 255);
    public static readonly SolidColorBrush Hover = B(246, 241, 229);

    /* ---------- 精修色板：学习中心 / 浏览器设置页共用 ----------
     * 2026-11 升级：暖中性底 + 单一沉稳强调色 + 克制的语义色。
     * 追求「安静的高级感」：色彩只承担功能，不做装饰；层级靠字号与留白。 */
    public static readonly SolidColorBrush Surface = B(250, 249, 247);   // 暖白画布
    public static readonly SolidColorBrush Raised = B(255, 255, 255);
    public static readonly SolidColorBrush Subtle = B(146, 150, 158);    // 更轻的辅助文字
    public static readonly SolidColorBrush Hairline = B(235, 233, 228);  // 更淡的极细分隔
    public static readonly SolidColorBrush Soft = B(245, 244, 241);
    public static readonly SolidColorBrush InkSoft = B(90, 98, 112);     // 次级正文
    public static readonly SolidColorBrush Brand = B(58, 74, 102);       // 沉墨蓝：单一强调色
    public static readonly SolidColorBrush BrandSoft = B(236, 240, 246);
    public static readonly SolidColorBrush BrandWash = B(245, 248, 252); // 今日列的极淡蓝底，强调「今天」
    public static readonly SolidColorBrush Danger = B(168, 70, 66);
    public static readonly SolidColorBrush DangerSoft = B(252, 241, 239);
    public static readonly SolidColorBrush Warning = B(158, 112, 48);
    public static readonly SolidColorBrush WarningSoft = B(251, 246, 235);
    public static readonly SolidColorBrush Done = B(122, 130, 142);
    public static readonly SolidColorBrush DoneSoft = B(244, 245, 247);
    public static readonly SolidColorBrush Experiment = B(76, 108, 120);
    public static readonly SolidColorBrush ExperimentSoft = B(233, 241, 243);
    public static readonly SolidColorBrush Report = B(104, 92, 136);
    public static readonly SolidColorBrush ReportSoft = B(240, 237, 248);

    /* ---------- 统一节奏（8px 基准，大留白） ---------- */
    public const double TitleSize = 30;      // 大标题，拉开层级
    public const double SectionSize = 15;
    public const double BodySize = 14;
    public const double SmallSize = 12;
    public const double MicroSize = 11;
    public const double StatSize = 30;       // 统计大数字
    public const double Space = 36;          // 区块之间的呼吸
    public const double Gap = 18;
    public const double Radius = 14;

    /* ---------- 小组件工厂 ---------- */

    public static TextBlock Txt(string text, double size, Brush fg, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = fg,
        FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>状态胶囊：只占文字宽度，不抢视觉重心。</summary>
    public static Border Pill(string text, Brush fg, Brush bg) => new()
    {
        Background = bg,
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(10, 4, 10, 4),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = Txt(text, MicroSize, fg, FontWeights.Normal)
    };

    /// <summary>白底卡片，极淡 1px 描边，无阴影，靠留白分层。</summary>
    public static Border Panel(UIElement child, Thickness? padding = null) => new()
    {
        Background = Raised,
        CornerRadius = new CornerRadius(Radius),
        BorderBrush = Hairline,
        BorderThickness = new Thickness(1),
        Padding = padding ?? new Thickness(20),
        Child = child
    };

    public static Border Rule(double top = 0, double bottom = 0) => new()
    {
        Height = 1,
        Background = Hairline,
        Margin = new Thickness(0, top, 0, bottom),
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    public static TextBlock Section(string text) =>
        Txt(text, SectionSize, Ink, FontWeights.SemiBold);

    public static TextBlock Caption(string text) =>
        Txt(text, SmallSize, Subtle);

    /// <summary>统计块：大数字 + 小标签，不用边框。数字用 Light 字重更显精致。</summary>
    public static StackPanel Stat(string number, string label, Brush numberInk)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 40, 0) };
        p.Children.Add(Txt(number, StatSize, numberInk, FontWeights.Light));
        p.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = MicroSize,
            Foreground = Subtle,
            Margin = new Thickness(2, 3, 0, 0)
        });
        return p;
    }

    /// <summary>眉标 / 小章节标签：字距感靠大写字母与字重，不用色块。</summary>
    public static TextBlock Overline(string text) =>
        Txt(text, MicroSize, Subtle, FontWeights.SemiBold);

    /// <summary>轻量文字入口（如「查看课件 →」）：没有胶囊底色，靠颜色与箭头表达可点。</summary>
    public static TextBlock GhostLink(string text = "查看课件 →") =>
        Txt(text, SmallSize, Brand, FontWeights.SemiBold);

    private static readonly ControlTemplate FlatTemplate = BuildFlatTemplate();

    /// <summary>去掉系统 Button 的蓝底 hover / 焦点框，只留 Border + 内容。</summary>
    private static ControlTemplate BuildFlatTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        // 用 Border.X 而不是 Control.X：项目里存在 BigBlueFish.Control 命名空间，Control 会被解析成命名空间
        border.SetBinding(Border.BackgroundProperty,
            new Binding(nameof(Border.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty,
            new Binding(nameof(Border.BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty,
            new Binding(nameof(Border.BorderThickness)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty,
            new Binding(nameof(Border.Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    /// <summary>幽灵按钮（次级动作）；primary=true 时是品牌色主按钮。</summary>
    public static Button Button(string text, bool primary = false)
    {
        var normal = primary ? Brand : Raised;
        var hover = primary ? AccentHover : Soft;
        var btn = new Button
        {
            Content = text,
            FontSize = BodySize,
            Padding = new Thickness(15, 8, 15, 8),
            Background = normal,
            Foreground = primary ? Brushes.White : Ink,
            BorderBrush = primary ? Brand : Hairline,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Template = FlatTemplate,
            MinHeight = 34
        };
        btn.MouseEnter += (_, _) => { if (btn.IsEnabled) btn.Background = hover; };
        btn.MouseLeave += (_, _) => btn.Background = normal;
        btn.IsEnabledChanged += (_, _) => btn.Background = btn.IsEnabled ? normal : DoneSoft;
        return btn;
    }

    /// <summary>顶部导航项：无边框文字按钮，选中态靠字重与颜色 + 外部下划线表达。</summary>
    public static Button NavButton(string text) => new()
    {
        Content = text,
        FontSize = SectionSize,
        Padding = new Thickness(0, 6, 0, 12),
        Background = Brushes.Transparent,
        Foreground = Subtle,
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Hand,
        Template = FlatTemplate,
        FontWeight = FontWeights.Normal
    };

    public static void SetNavActive(Button btn, bool active)
    {
        btn.Foreground = active ? Ink : Subtle;
        btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        btn.Opacity = active ? 1 : 0.85;
    }

    public static ComboBox Combo(int width) => new()
    {
        Width = width,
        Height = 34,
        Padding = new Thickness(9, 4, 9, 4),
        VerticalContentAlignment = VerticalAlignment.Center,
        FontSize = BodySize,
        Foreground = Ink,
        Background = Raised,
        BorderBrush = Hairline,
        BorderThickness = new Thickness(1)
    };
}
