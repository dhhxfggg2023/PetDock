// 自绘基元（docs/18 §2.6）：圆角块、描边、单行/多行文字。
//
// 为什么不用控件自带的 Appearance：WinForms 默认控件的外观是 2002 年的（灰底、直角、
// 系统色），用户对它的判词是「难看、复古」。自绘的代价是要自己量文字——所以这里的每个
// 文字方法都只有一个入口（TextRenderer），保证测量与绘制用同一套度量，不会出现
// 「按一个字号排版、按另一个字号绘制」这种文字被裁掉的情况（用户要求 m03850）。
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

/// <summary>自绘基元。全部是纯函数：给 Graphics 与矩形，画完就返回。</summary>
internal static class Ink
{
    /// <summary>填充圆角块。颜色经 <see cref="Theme.Paint"/>：磨砂/玻璃下块本身也带 alpha。</summary>
    public static void Fill(Graphics g, Rectangle box, int radius, Color color)
    {
        using GraphicsPath path = Rounded(box, radius);
        using var brush = new SolidBrush(Theme.Paint(color));
        g.FillPath(brush, path);
    }

    /// <summary>描边圆角块。</summary>
    public static void Stroke(Graphics g, Rectangle box, int radius, Color color, float width = 1f)
    {
        using GraphicsPath path = Rounded(box, radius);
        using var pen = new Pen(Theme.Paint(color), width);
        g.DrawPath(pen, path);
    }

    /// <summary>上下边线（分隔线用）。</summary>
    public static void Line(Graphics g, int x1, int y1, int x2, int y2, Color color)
    {
        using var pen = new Pen(Theme.Paint(color), 1f);
        g.DrawLine(pen, x1, y1, x2, y2);
    }

    /// <summary>
    /// 勾选框（圆角方块）。选中 = 粉底 + 深色勾；未选 = 卡片底 + 描边；
    /// 禁用（不可勾）把勾画成灰的——与「未选」区分得开，也不假装能点。
    /// </summary>
    public static void CheckBox(Graphics g, Rectangle box, bool isChecked, bool enabled, int radius)
    {
        Color fill = isChecked ? Theme.Accent : Theme.Surface;
        Color stroke = isChecked ? Theme.Accent : Theme.Line;
        Fill(g, box, radius, fill);
        Stroke(g, box, radius, stroke);

        if (!isChecked)
        {
            return;
        }

        // 勾：两根线，按盒子大小取比例，缩放时不会跑出格子。
        int inset = Math.Max(2, box.Width / 4);
        using var pen = new Pen(enabled ? Theme.Bg : Theme.TextDim, Math.Max(1.5f, box.Width / 8f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        Point left = new(box.X + inset, box.Y + (box.Height / 2));
        Point mid = new((box.X + (box.Width / 2)) - Math.Max(1, box.Width / 16), box.Bottom - inset);
        Point right = new(box.Right - inset, box.Y + inset);
        g.DrawLines(pen, new[] { left, mid, right });
    }

    /// <summary>单选框（圆形）。选中 = 粉环 + 粉心；禁用时整枚降成灰。</summary>
    public static void Radio(Graphics g, Rectangle box, bool isSelected, bool enabled)
    {
        Color ring = !enabled ? Theme.Line : (isSelected ? Theme.Accent : Theme.TextDim);
        using var pen = new Pen(ring, 1.6f);
        g.DrawEllipse(pen, box);

        if (!isSelected)
        {
            return;
        }

        int inset = Math.Max(2, box.Width / 3);
        using var brush = new SolidBrush(enabled ? Theme.Accent : Theme.Line);
        g.FillEllipse(brush, Rectangle.Inflate(box, -inset, -inset));
    }

    /// <summary>
    /// 单行文字，垂直居中、水平按 alignment。放不下时用省略号收尾——
    /// 比裁掉半个字可读，且配合 <see cref="ThemedList"/> 的列宽自适应基本不会触发。
    /// </summary>
    public static void TextOneLine(
        Graphics g, string text, Font font, Color color, Rectangle box, TextFormatFlags align)
    {
        TextFormatFlags flags = align
            | TextFormatFlags.NoPadding
            | TextFormatFlags.SingleLine
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(g, text ?? string.Empty, font, box, color, flags);
    }

    /// <summary>多行文字（自动换行）。box 高度不够时由调用方先 <see cref="WrapHeight"/> 量。</summary>
    public static void TextWrap(
        Graphics g, string text, Font font, Color color, Rectangle box)
    {
        TextFormatFlags flags = TextFormatFlags.NoPadding
            | TextFormatFlags.WordBreak
            | TextFormatFlags.Top;
        TextRenderer.DrawText(g, text ?? string.Empty, font, box, color, flags);
    }

    /// <summary>给定宽度下换行后的高度。布局用它决定行高，避免「文字比格子高」。 </summary>
    public static int WrapHeight(string text, Font font, int width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            return font.Height;
        }

        Size measured = TextRenderer.MeasureText(
            text,
            font,
            new Size(width, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        return measured.Height;
    }

    /// <summary>单行文字宽度（列宽自适应用）。</summary>
    public static int TextWidth(string text, Font font)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return TextRenderer.MeasureText(
            text, font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
    }

    /// <summary>圆角矩形路径。</summary>
    private static GraphicsPath Rounded(Rectangle box, int radius)
    {
        var path = new GraphicsPath();
        int r = radius * 2;
        if (r <= 0 || box.Width <= r || box.Height <= r)
        {
            path.AddRectangle(box);
            return path;
        }

        path.AddArc(box.X, box.Y, r, r, 180, 90);
        path.AddArc(box.Right - r, box.Y, r, r, 270, 90);
        path.AddArc(box.Right - r, box.Bottom - r, r, r, 0, 90);
        path.AddArc(box.X, box.Bottom - r, r, r, 90, 90);
        path.CloseFigure();
        return path;
    }
}
