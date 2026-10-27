// 主题化的右键菜单渲染（docs/18 §2.6）：托盘菜单与主界面右键菜单都走这里。
//
// 为什么不用系统菜单外观：Windows 的菜单是白底黑字，在深色界面里弹出来像另一个程序；
// 而 ContextMenuStrip 的默认渲染器只接受 ProfessionalColorTable 的几种渐变，
// 控制不到圆角、行高与箭头颜色。所以直接接管 ToolStripRenderer 的几个绘制点，
// 菜单项仍是真正的 ToolStripMenuItem（点击语义、快捷键、子菜单全部原样保留）。
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

/// <summary>深色菜单渲染器。用 <see cref="Apply"/> 挂到任意菜单上。</summary>
internal sealed class ThemedMenuRenderer : ToolStripRenderer
{
    private readonly int _dpi;
    private readonly Font _fallback;

    public ThemedMenuRenderer(int dpi, Font fallback)
    {
        _dpi = dpi;
        _fallback = fallback;
    }

    /// <summary>把渲染器、字体与配色一次性挂到菜单上（调用方不必各自重复）。</summary>
    public static void Apply(ContextMenuStrip menu)
    {
        Font font = Theme.FontFor(menu, TypeRole.Body);
        menu.Renderer = new ThemedMenuRenderer(Theme.DpiOf(menu), font);
        menu.Font = font;
        menu.BackColor = Theme.Surface;
        menu.ForeColor = Theme.Text;
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.Padding = new Padding(Theme.Dip(menu, Theme.SpaceXs));

        foreach (ToolStripItem item in menu.Items)
        {
            item.Font = Theme.FontFor(menu, TypeRole.Body);
            item.ForeColor = item.Enabled ? Theme.Text : Theme.TextDim;
        }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = Theme.Brush(Theme.Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = Theme.Pen(Theme.Line, 1f);
        Rectangle box = new(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(pen, box);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        int radius = Theme.Dip(_dpi, Theme.RadiusSm);
        if (e.Item.Selected || e.Item.Pressed)
        {
            Ink.Fill(e.Graphics, new Rectangle(0, 0, e.Item.Width, e.Item.Height), radius, Theme.SurfaceHover);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        Color color = e.Item.Enabled ? Theme.Text : Theme.TextDim;
        Font font = e.Item.Font ?? _fallback;
        Ink.TextOneLine(e.Graphics, e.Text ?? string.Empty, font, color, e.TextRectangle, TextFormatFlags.Left);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int pad = Theme.Dip(_dpi, Theme.SpaceSm);
        int y = e.Item.Height / 2;
        Ink.Line(e.Graphics, pad, y, e.Item.Width - pad, y, Theme.Line);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        int size = Theme.Dip(_dpi, Theme.SpaceLg);
        var box = new Rectangle(
            e.ImageRectangle.X,
            e.ImageRectangle.Y + ((e.ImageRectangle.Height - size) / 2),
            size,
            size);
        Ink.CheckBox(e.Graphics, box, true, true, Theme.Dip(_dpi, Theme.SpaceXs));
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        Color ink = e.Item?.Enabled == false ? Theme.TextDim : Theme.Text;
        using var pen = new Pen(ink, 1.6f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
        };
        Rectangle box = e.ArrowRectangle;
        int w = Math.Min(box.Width, box.Height) / 2;
        int cx = box.X + (box.Width / 2);
        int cy = box.Y + (box.Height / 2);
        Point top = new(cx - (w / 2), cy - (w / 4));
        Point tip = new(cx + (w / 2), cy);
        Point bottom = new(cx - (w / 2), cy + (w / 4));
        e.Graphics.DrawLines(pen, new[] { top, tip, bottom });
    }
}