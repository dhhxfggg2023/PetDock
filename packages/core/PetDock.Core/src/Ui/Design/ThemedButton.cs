// 主题化按钮（原规格 18 §2.6）：代替默认 Button 的系统边框外观。
//
// 两种形态：主按钮（粉色底、深字）与次按钮（透明底 + 描边 + 亮字）。都在 OnPaint 里画，
// 不做任何「按文字猜尺寸」的事——宽度由 MeasurePreferred() 用同一套字体量出来，
// 所以换语言、放大字体都不会把字裁掉（用户要求 m03850）。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

/// <summary>自绘按钮。改 Text 后调用 <see cref="MeasurePreferred"/> 重算尺寸。</summary>
internal sealed class ThemedButton : Button
{
    private bool _hover;
    private bool _pressed;

    public ThemedButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        TabStop = true;
        Font = Theme.FontFor(this, TypeRole.Body);
    }

    /// <summary>主按钮（用于「应用」「刷新」这类主动作）。</summary>
    public bool Primary { get; set; }

    /// <summary>本控件当前的字体（按它所在 DPI 取，缩放变了会自动跟上）。</summary>
    private Font RoleFont => Theme.FontFor(this, TypeRole.Body);

    /// <summary>按当前文字与 DPI 算出建议尺寸（高度含上下内边距）。</summary>
    public Size MeasurePreferred()
    {
        int dpi = Theme.DpiOf(this);
        int padX = Theme.Dip(dpi, Theme.SpaceXl);
        int h = RoleFont.Height + Theme.Dip(dpi, Theme.SpaceLg);
        int w = Ink.TextWidth(Text ?? string.Empty, RoleFont) + padX * 2;
        return new Size(w, h);
    }

    /// <summary>按文字自适应尺寸（调用方再决定放在哪）。</summary>
    public ThemedButton FitToText()
    {
        Size preferred = MeasurePreferred();
        MinimumSize = preferred;
        Size = preferred;
        return this;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = Enabled;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        int dpi = Theme.DpiOf(this);
        int radius = Theme.Dip(dpi, Theme.RadiusMd);
        Rectangle box = new(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));

        Color fill;
        Color border;
        Color ink;
        if (!Enabled)
        {
            // 不可点：降成卡片底 + 暗字。不靠鼠标形状说「不能点」，也不像系统按钮那样整块发灰。
            fill = Theme.Surface;
            border = Theme.Line;
            ink = Theme.TextDim;
        }
        else if (Primary)
        {
            fill = _pressed ? Theme.Accent : (_hover ? Theme.AccentHover : Theme.Accent);
            border = fill;
            ink = Theme.OnAccent;
        }
        else
        {
            fill = _pressed ? Theme.SurfaceHover : (_hover ? Theme.SurfaceHover : Theme.Surface);
            border = _hover || _pressed ? Theme.Accent : Theme.Line;
            ink = Theme.Text;
        }

        Ink.Fill(g, box, radius, fill);
        Ink.Stroke(g, box, radius, border);

        Rectangle textBox = new(
            Theme.Dip(dpi, Theme.SpaceSm),
            0,
            Math.Max(0, Width - Theme.Dip(dpi, Theme.SpaceSm) * 2),
            Height);
        Ink.TextOneLine(
            g, Text ?? string.Empty, RoleFont, ink, textBox, TextFormatFlags.HorizontalCenter);

        if (Focused && ShowFocusCues)
        {
            Rectangle focus = Rectangle.Inflate(box, -Theme.Dip(dpi, 3), -Theme.Dip(dpi, 3));
            Ink.Stroke(g, focus, radius, Theme.Accent);
        }
    }
}
