using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BigBlueFish.Windows;

// NotifyIcon keeps native positioning, keyboard navigation and outside-click dismissal.
internal sealed class CompanionTrayMenu : ContextMenuStrip
{
    private Font? menuFont;
    internal CompanionTrayMenu()
    {
        Renderer = new CompanionTrayMenuRenderer();
        ShowImageMargin = false;
        ShowCheckMargin = false;
        BackColor = Color.White;
        ForeColor = Color.FromArgb(49, 64, 94);
    }

    protected override void OnOpening(System.ComponentModel.CancelEventArgs e)
    {
        _ = Handle;
        ApplyMetrics();
        base.OnOpening(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        ApplyMetrics();
        PerformLayout();
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        base.OnOpened(e);
    }

    private void ApplyMetrics()
    {
        float scale = DeviceDpi / 96f;
        if (menuFont == null || Font.Unit != GraphicsUnit.Pixel || Math.Abs(Font.Size - 14 * scale) > .01f)
        {
            var previous = menuFont;
            menuFont = new Font("Microsoft YaHei UI", 14 * scale, GraphicsUnit.Pixel);
            Font = menuFont;
            previous?.Dispose();
        }
        Padding = new Padding((int)(7 * scale));
        foreach (ToolStripItem item in Items)
        {
            item.AutoSize = false;
            item.Margin = new Padding(0, (int)(2 * scale), 0, (int)(2 * scale));
            if (item is ToolStripControlHost host && host.Control is AffinityProgressControl)
            {
                // 好感度进度条项：两行布局（档位行 + 进度条行），比普通菜单项更高更宽。
                item.Size = new Size((int)(264 * scale), (int)(58 * scale));
                continue;
            }
            var text = TextRenderer.MeasureText(item.Text, Font);
            item.Size = new Size(Math.Max((int)(194 * scale), text.Width + (int)(66 * scale)),
                Math.Max((int)(38 * scale), text.Height + (int)(16 * scale)));
        }
        int width = 0, height = Padding.Vertical;
        foreach (ToolStripItem item in Items)
        {
            width = Math.Max(width, item.Width);
            height += item.Height + item.Margin.Vertical;
        }
        MinimumSize = new Size(width + Padding.Horizontal, height);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { menuFont?.Dispose(); menuFont = null; }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using var shape = CompanionTrayMenuRenderer.Outline(new RectangleF(0, 0, Width, Height), 12 * DeviceDpi / 96f);
        var previous = Region;
        Region = new Region(shape);
        previous?.Dispose();
    }
}

internal sealed class CompanionTrayMenuRenderer : ToolStripProfessionalRenderer
{
    internal CompanionTrayMenuRenderer() : base(new Palette()) => RoundedEdges = false;

    private sealed class Palette : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.White;
        public override Color MenuBorder => Color.FromArgb(220, 229, 242);
    }

    internal static GraphicsPath Outline(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Color.White);

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip == null || e.ToolStrip.Width <= 1 || e.ToolStrip.Height <= 1) return;
        float scale = e.ToolStrip.DeviceDpi / 96f;
        using var path = Outline(new RectangleF(.5f, .5f, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 12 * scale);
        using var pen = new Pen(Color.FromArgb(220, 229, 242), scale);
        var smoothing = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
        e.Graphics.SmoothingMode = smoothing;
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed) return;
        float scale = (e.ToolStrip?.DeviceDpi ?? 96) / 96f;
        using var outline = Outline(new RectangleF(0, 0, e.Item.Width - 1, e.Item.Height - 1), 7 * scale);
        using var brush = new SolidBrush(e.Item.Pressed ? Color.FromArgb(220, 234, 255) : Color.FromArgb(237, 243, 252));
        var smoothing = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, outline);
        e.Graphics.SmoothingMode = smoothing;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        float scale = (e.ToolStrip?.DeviceDpi ?? 96) / 96f;
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        if (e.Item.Tag is string glyph)
        {
            using var iconFont = new Font("Segoe MDL2 Assets", 16 * scale, GraphicsUnit.Pixel);
            TextRenderer.DrawText(e.Graphics, glyph, iconFont,
                new Rectangle((int)(12 * scale), 0, (int)(24 * scale), e.Item.Height),
                Color.FromArgb(80, 96, 152), flags);
        }
        TextRenderer.DrawText(e.Graphics, e.Text, e.Item.Font,
            new Rectangle((int)(44 * scale), 0, e.Item.Width - (int)(56 * scale), e.Item.Height),
            e.Item.Enabled ? Color.FromArgb(49, 64, 94) : SystemColors.GrayText, flags);
    }
}

// 好感度可见化：托盘菜单里的自绘进度条项（爱心 + 档位 + 进度条 + 距下一档）。
// 风格参考成熟桌宠（Shimeji XP 升级制 / 爱心计量），与全局奶白、蓝发蓝配色统一。
internal sealed class AffinityProgressControl : UserControl
{
    internal event EventHandler? AffinityClicked;
    private static readonly Color Ink = Color.FromArgb(49, 64, 94);
    private static readonly Color Blue = Color.FromArgb(80, 96, 152);
    private static readonly Color Track = Color.FromArgb(237, 243, 252);
    private bool hovered;
    private double points;

    internal AffinityProgressControl(double affinityPoints)
    {
        points = affinityPoints;
        BackColor = Color.White;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        MouseEnter += (_, _) => { hovered = true; BackColor = Track; Invalidate(); };
        MouseLeave += (_, _) => { hovered = false; BackColor = Color.White; Invalidate(); };
        MouseClick += (_, _) => AffinityClicked?.Invoke(this, EventArgs.Empty);
    }

    internal void SetAffinityPoints(double value)
    {
        if (Math.Abs(points - value) > .001) { points = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        using var font = new Font("Microsoft YaHei UI", 14 * scale, GraphicsUnit.Pixel);
        string tier = CompanionBrain.AffinityTier(points);
        double progress = CompanionBrain.AffinityProgress(points);
        var (next, remaining) = CompanionBrain.AffinityNext(points);
        float left = 44 * scale;
        float top = 8 * scale;
        float rightPad = 12 * scale;
        float lineHeight = font.GetHeight() + 2 * scale;
        var centered = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        // 行1：♥ 好感度（左对齐固定）＋ 128 分 · 眼熟（紧跟其后放入矩形区，超宽省略号保底，绝不重叠）
        string header = "♥ 好感度";
        var headerSize = TextRenderer.MeasureText(header, font);
        TextRenderer.DrawText(g, header, font, new Rectangle((int)left, (int)top, (int)(left + headerSize.Width), (int)lineHeight),
            Blue, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        string value = $"{points:0} 分 · {tier}";
        float valueX = left + headerSize.Width + 8 * scale;
        float valueMaxW = Width - rightPad - valueX;
        if (valueMaxW > 12 * scale)
            TextRenderer.DrawText(g, value, font,
                new Rectangle((int)valueX, (int)top, (int)valueMaxW, (int)lineHeight), Ink, centered | TextFormatFlags.EndEllipsis);
        // 行2：圆角进度条 + 距下一档（suffix 右对齐，bar 动态宽度取剩余空间，最小 24*scale）
        string suffix = next == null ? "已满级" : $"再 {remaining:0} 分 → {next}";
        var suffixSize = TextRenderer.MeasureText(suffix, font);
        float suffixX = Width - rightPad - suffixSize.Width;
        float barX = left;
        float barRight = suffixX - 10 * scale;
        float barWidth = Math.Max(24 * scale, barRight - barX);
        float barTop = top + headerSize.Height + 6 * scale;
        float barHeight = 8 * scale;
        var rect = new RectangleF(barX, barTop, barWidth, barHeight);
        using (var track = new SolidBrush(hovered ? Color.FromArgb(226, 234, 248) : Track))
            g.FillPath(track, CompanionTrayMenuRenderer.Outline(rect, 4 * scale));
        if (progress > 0)
        {
            float fillWidth = (float)(barWidth * Math.Clamp(progress, 0, 1));
            if (fillWidth > 2 * scale)
            {
                using var brush = new SolidBrush(Blue);
                g.FillPath(brush, CompanionTrayMenuRenderer.Outline(
                    new RectangleF(barX, barTop, fillWidth, barHeight), 4 * scale));
            }
        }
        TextRenderer.DrawText(g, suffix, font,
            new Rectangle((int)suffixX, (int)(barTop - 2 * scale), (int)(Width - rightPad - suffixX), (int)lineHeight),
            Ink, centered | TextFormatFlags.EndEllipsis);
    }
}
