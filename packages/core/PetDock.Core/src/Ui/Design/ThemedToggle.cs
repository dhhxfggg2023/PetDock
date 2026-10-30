// 主题化的勾选框与单选框（原规格 18 §2.6）：深色底上的自绘方形/圆形标记 + 文字。
//
// 为什么不用 CheckBox/RadioButton：它们的方框与圆点由系统主题绘制（浅色、固定尺寸），
// 在深色卡片上既突兀又不会跟着字体放大；而用户的判词正是「难看、复古」+「不要有缩放问题」。
// 自绘之后：标记尺寸按 DPI 取、文字按角色字号量，两者一起缩放，100%~200% 都不会挤。
//
// 单选的分组规则沿用 WinForms 的直觉：同一个父容器里的 ThemedRadio 互斥。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

/// <summary>勾选状态变化（D-03b：自声明委托，不用系统泛型委托）。</summary>
internal delegate void CheckToggled(bool isChecked);

/// <summary>勾选/单选共用的自绘底座。子类只决定标记长什么样。</summary>
internal abstract class ThemedPick : Control
{
    private bool _checked;
    private bool _hover;
    private bool _syncing;

    protected ThemedPick()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        TabStop = true;
        Font = Theme.FontFor(this, TypeRole.Body);
        Size = PreferredSizeOf();
    }

    /// <summary>勾选状态变化（程序化设置也会发一次，调用方按需去重）。</summary>
    public CheckToggled? Toggled { get; set; }

    /// <summary>是否勾着。</summary>
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }

            _checked = value;
            Invalidate();
            AfterCheckedChanged();
            Toggled?.Invoke(value);
        }
    }

    /// <summary>按当前文字与 DPI 把控件尺寸量成刚好放得下（换语言、换缩放都要重调一次）。</summary>
    public ThemedPick FitToText()
    {
        Size = PreferredSizeOf();
        return this;
    }

    /// <summary>标记的边长（设备像素）。单选框用同一档尺寸，视觉上同高。</summary>
    protected int MarkSize(int dpi) => Theme.Dip(dpi, Theme.SpaceLg);

    public override Size GetPreferredSize(Size proposedSize)
    {
        int dpi = Theme.DpiOf(this);
        Font font = Theme.FontFor(this, TypeRole.Body);
        int gap = Theme.Dip(dpi, Theme.SpaceSm);
        int padX = Theme.Dip(dpi, Theme.SpaceXs);
        int textWidth = Ink.TextWidth(Text ?? string.Empty, font);
        int height = Math.Max(MarkSize(dpi), font.Height) + Theme.Dip(dpi, Theme.SpaceSm);
        return new Size((padX * 2) + MarkSize(dpi) + gap + textWidth, height);
    }

    protected abstract void DrawMark(Graphics g, Rectangle box, int dpi);

    /// <summary>勾上之后要做的连带动作（单选框用它清掉同组的兄弟）。</summary>
    protected virtual void AfterCheckedChanged()
    {
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        int dpi = Theme.DpiOf(this);
        int padX = Theme.Dip(dpi, Theme.SpaceXs);
        int mark = MarkSize(dpi);
        var markBox = new Rectangle(padX, (Height - mark) / 2, mark, mark);
        DrawMark(g, markBox, dpi);

        int gap = Theme.Dip(dpi, Theme.SpaceSm);
        var textBox = new Rectangle(
            markBox.Right + gap,
            0,
            Math.Max(0, Width - markBox.Right - gap - padX),
            Height);
        Color ink = !Enabled ? Theme.TextDim : (_hover || Focused ? Theme.Text : Theme.TextDim);
        Ink.TextOneLine(g, Text ?? string.Empty, Theme.FontFor(this, TypeRole.Body), ink, textBox, TextFormatFlags.Left);

        if (Focused && ShowFocusCues)
        {
            using var pen = Theme.Pen(Theme.Accent, 1f);
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (Enabled && e.Button == MouseButtons.Left)
        {
            Toggle();
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
        {
            Toggle();
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    /// <summary>点一下：勾选框翻转；单选框只能被勾上（再点一次不会取消，与系统单选一致）。</summary>
    protected virtual void Toggle() => Checked = !Checked;

    /// <summary>把同组（同父容器）的其它单选框清掉；同步期间不回头触发事件。</summary>
    protected void UncheckSiblings()
    {
        if (Parent is null || _syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            foreach (Control sibling in Parent.Controls)
            {
                if (!ReferenceEquals(sibling, this) && sibling is ThemedRadio other && other.Checked)
                {
                    other.Checked = false;
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private Size PreferredSizeOf() => GetPreferredSize(Size.Empty);
}

/// <summary>单选框：同组互斥，选中的那一枚画实心点。</summary>
internal sealed class ThemedRadio : ThemedPick
{
    protected override void DrawMark(Graphics g, Rectangle box, int dpi) =>
        Ink.Radio(g, box, Checked, Enabled);

    protected override void AfterCheckedChanged()
    {
        if (Checked)
        {
            UncheckSiblings();
        }
    }

    /// <summary>单选再点一次不取消：取消会让「必须选一个」的三选一变成没有答案。</summary>
    protected override void Toggle() => Checked = true;
}