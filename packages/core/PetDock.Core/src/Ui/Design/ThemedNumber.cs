// 主题化的数字输入（原规格 18 §2.6）：设置页的「空闲降帧阈值」用它，代替 NumericUpDown。
//
// 为什么不用 NumericUpDown：它的文字框与上下箭头都是系统绘制（浅底、固定高度），
// 在深色页面上是整页最显眼的「2002 年控件」，而且高度写死 20px —— 150% 缩放下
// 中文字形会被裁掉下半截（用户 m03850 点名不许）。这里自绘：值按角色字号量、
// 上下两个点击区按 DPI 取，宽度由调用方给逻辑像素。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

/// <summary>值变化（D-03b：自声明委托，不用系统泛型委托）。</summary>
internal delegate void NumberChanged(decimal value);

/// <summary>自绘数字输入：值 + 两个上下箭头点击区。键盘上下也能改。</summary>
internal sealed class ThemedNumber : Control
{
    private decimal _value;
    private decimal _minimum;
    private decimal _maximum = 100m;
    private decimal _increment = 1m;
    private bool _hoverUp;
    private bool _hoverDown;

    public ThemedNumber()
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

    /// <summary>值变化（程序化设置也会发一次）。</summary>
    public NumberChanged? Changed { get; set; }

    /// <summary>当前值（越界自动钳制到 [Minimum, Maximum]）。</summary>
    public decimal Value
    {
        get => _value;
        set
        {
            decimal clamped = Clamp(value);
            if (clamped == _value)
            {
                return;
            }

            _value = clamped;
            Invalidate();
            Changed?.Invoke(clamped);
        }
    }

    /// <summary>下限（设置值时一并钳制）。</summary>
    public decimal Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            if (_maximum < _minimum)
            {
                _maximum = _minimum;
            }

            Value = _value;
        }
    }

    /// <summary>上限（设置值时一并钳制）。</summary>
    public decimal Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value < _minimum ? _minimum : value;
            Value = _value;
        }
    }

    /// <summary>步长（箭头点一次改多少）。</summary>
    public decimal Increment
    {
        get => _increment;
        set => _increment = value <= 0 ? 1m : value;
    }

    /// <summary>按当前字号量出建议尺寸（宽度是可用宽度，高度跟着字体走）。</summary>
    public ThemedNumber FitToFont(int widthDip)
    {
        int dpi = Theme.DpiOf(this);
        Size = new Size(Theme.Dip(dpi, widthDip), PreferredSizeOf().Height);
        return this;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int dpi = Theme.DpiOf(this);
        int height = Theme.FontFor(this, TypeRole.Body).Height + (Theme.Dip(dpi, Theme.SpaceSm) * 2);
        return new Size(Theme.Dip(dpi, 96), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        int dpi = Theme.DpiOf(this);
        int radius = Theme.Dip(dpi, Theme.RadiusSm);
        Rectangle box = new(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        Ink.Fill(g, box, radius, Theme.Surface);
        Ink.Stroke(g, box, radius, Focused ? Theme.Accent : Theme.Line);

        int pad = Theme.Dip(dpi, Theme.SpaceSm);
        int buttonWidth = Theme.Dip(dpi, Theme.SpaceLg);
        int buttonBox = Math.Max(1, Height - (pad / 2) * 2);
        var up = new Rectangle(Width - pad - buttonWidth, (Height - buttonBox) / 2, buttonWidth, buttonBox / 2);
        var down = new Rectangle(up.X, up.Bottom, buttonWidth, buttonBox - (buttonBox / 2));

        var textBox = new Rectangle(
            pad, 0, Math.Max(0, up.X - pad - pad), Height);
        string text = _value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        Ink.TextOneLine(g, text, Theme.FontFor(this, TypeRole.Body), Enabled ? Theme.Text : Theme.TextDim, textBox, TextFormatFlags.Left);

        DrawArrow(g, up, true, _hoverUp);
        DrawArrow(g, down, false, _hoverDown);
    }

    private static void DrawArrow(Graphics g, Rectangle box, bool up, bool hover)
    {
        Color ink = hover ? Theme.Accent : Theme.TextDim;
        using var pen = new Pen(ink, 1.6f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        int w = Math.Min(box.Width, box.Height) / 2;
        int cx = box.X + (box.Width / 2);
        int cy = box.Y + (box.Height / 2);
        Point left = new(cx - (w / 2), up ? cy + (w / 3) : cy - (w / 3));
        Point tip = new(cx, up ? cy - (w / 3) : cy + (w / 3));
        Point right = new(cx + (w / 2), up ? cy + (w / 3) : cy - (w / 3));
        g.DrawLines(pen, new[] { left, tip, right });
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool up = UpBox().Contains(e.Location);
        bool down = DownBox().Contains(e.Location);
        if (up != _hoverUp || down != _hoverDown)
        {
            _hoverUp = up;
            _hoverDown = down;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverUp || _hoverDown)
        {
            _hoverUp = false;
            _hoverDown = false;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!Enabled || e.Button != MouseButtons.Left)
        {
            return;
        }

        if (UpBox().Contains(e.Location))
        {
            Value = _value + _increment;
        }
        else if (DownBox().Contains(e.Location))
        {
            Value = _value - _increment;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!Enabled)
        {
            return;
        }

        if (e.KeyCode == Keys.Up)
        {
            Value = _value + _increment;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Down)
        {
            Value = _value - _increment;
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

    private Rectangle UpBox()
    {
        int dpi = Theme.DpiOf(this);
        int pad = Theme.Dip(dpi, Theme.SpaceSm);
        int buttonWidth = Theme.Dip(dpi, Theme.SpaceLg);
        int buttonBox = Math.Max(1, Height - (pad / 2) * 2);
        return new Rectangle(Width - pad - buttonWidth, (Height - buttonBox) / 2, buttonWidth, buttonBox / 2);
    }

    private Rectangle DownBox()
    {
        Rectangle up = UpBox();
        int dpi = Theme.DpiOf(this);
        int buttonBox = Math.Max(1, Height - (Theme.Dip(dpi, Theme.SpaceSm) / 2) * 2);
        return new Rectangle(up.X, up.Bottom, up.Width, buttonBox - (buttonBox / 2));
    }

    private decimal Clamp(decimal value) =>
        value < _minimum ? _minimum : (value > _maximum ? _maximum : value);

    private Size PreferredSizeOf() => GetPreferredSize(Size.Empty);
}