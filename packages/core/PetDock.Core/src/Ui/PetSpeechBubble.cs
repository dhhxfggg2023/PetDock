// 宠物头顶的短气泡（2026-10-29）：点击/拖动时给一句即时反馈。
//
// 为什么是独立窗体而不是宠物窗的子控件：宠物窗是 WS_EX_LAYERED 的逐像素合成窗，
// 里面没有控件层；气泡必须另起一个置顶、不抢焦点、不挡鼠标的窗。它不进入任务栏，
// 也不改变前台应用 —— 用户点宠物时看到一句话，但焦点还在原来的窗口上。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.Ui.Design;

namespace PetDock.Core.Ui;

internal sealed class PetSpeechBubble : Form
{
    private const int TailHeight = 7;
    private const int LifeMs = 2800;

    private readonly string _text;
    private readonly System.Windows.Forms.Timer _timer;
    private GraphicsPath? _shape;
    private Region? _region;
    private bool _tailDown = true;

    public PetSpeechBubble(string text)
    {
        _text = text ?? string.Empty;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Surface;
        DoubleBuffered = true;
        Size = MeasureBubble(_text);
        _timer = new System.Windows.Forms.Timer { Interval = LifeMs };
        _timer.Tick += (_, _) => Close();
    }

    /// <summary>不抢焦点：宠物可以被点，但输入焦点必须留在用户正在用的窗口。</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    /// <summary>当前气泡是否可见（判据读它，证明点击/拖动真的说了话）。</summary>
    public bool BubbleVisible => Visible && !IsDisposed;

    /// <summary>把气泡摆在宠物上方；上方放不下就翻到下方。位置按屏幕工作区夹取。</summary>
    public void ShowNear(LayeredFrameWindow owner)
    {
        if (owner is null || owner.IsDisposed || !owner.Visible)
        {
            return;
        }

        Place(owner);
        Show();
        _timer.Start();
    }

    /// <summary>宠物移动时跟着走（拖动过程中气泡不会留在原地）。</summary>
    public void Reposition(LayeredFrameWindow owner)
    {
        if (!BubbleVisible || owner is null || owner.IsDisposed)
        {
            return;
        }

        Place(owner);
        Invalidate();
    }

    private void Place(LayeredFrameWindow owner)
    {
        Rectangle area = Screen.FromHandle(owner.Handle).WorkingArea;
        Rectangle pet = owner.Bounds;
        int gap = Theme.Dip(this, 6);
        int x = pet.Left + ((pet.Width - Width) / 2);
        int y = pet.Top - Height - gap;
        _tailDown = true;
        if (y < area.Top)
        {
            y = pet.Bottom + gap;
            _tailDown = false;
        }

        x = Math.Clamp(x, area.Left + 2, Math.Max(area.Left + 2, area.Right - Width - 2));
        y = Math.Clamp(y, area.Top + 2, Math.Max(area.Top + 2, area.Bottom - Height - 2));
        Location = new Point(x, y);
        UpdateShape();
    }

    private Size MeasureBubble(string text)
    {
        Font font = Theme.Font(TypeRole.Body);
        int maxText = Theme.Dip(this, 220);
        int pad = Theme.Dip(this, 10);
        Size measured = TextRenderer.MeasureText(
            text,
            font,
            new Size(maxText, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        int width = Math.Clamp(measured.Width + (pad * 2), Theme.Dip(this, 64), maxText + (pad * 2));
        int height = measured.Height + (pad * 2) + TailHeight;
        return new Size(width, height);
    }

    private Rectangle BodyBox => new(0, 0, Width, Math.Max(1, Height - TailHeight));

    private Point[] TailPoints
    {
        get
        {
            Rectangle body = BodyBox;
            int radius = Theme.Dip(this, 9);
            int tailWidth = Theme.Dip(this, 7);
            int tailX = Math.Clamp(body.Width / 2, radius + tailWidth, Math.Max(radius + tailWidth, body.Width - radius - tailWidth));
            return _tailDown
                ? new[]
                {
                    new Point(tailX - tailWidth, body.Bottom - 1),
                    new Point(tailX, body.Bottom + TailHeight),
                    new Point(tailX + tailWidth, body.Bottom - 1),
                }
                : new[]
                {
                    new Point(tailX - tailWidth, body.Top + 1),
                    new Point(tailX, body.Top - TailHeight),
                    new Point(tailX + tailWidth, body.Top + 1),
                };
        }
    }

    private void UpdateShape()
    {
        _region?.Dispose();
        _shape?.Dispose();
        _shape = new GraphicsPath();
        AddRounded(_shape, BodyBox, Theme.Dip(this, 9));
        _shape.AddPolygon(TailPoints);
        _region = new Region(_shape);
        Region = _region;
    }

    private static void AddRounded(GraphicsPath path, Rectangle box, int radius)
    {
        int d = radius * 2;
        if (d <= 0 || box.Width <= d || box.Height <= d)
        {
            path.AddRectangle(box);
            return;
        }

        path.AddArc(box.X, box.Y, d, d, 180, 90);
        path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
        path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
        path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 背景由 OnPaint 按 Region 画；默认底色会在 Region 内再刷一遍，边缘会闪。
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_shape is null)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(Theme.Surface);
        using var border = new Pen(Theme.Line, 1f);
        g.FillPath(fill, _shape);
        Rectangle body = BodyBox;
        using (var bodyPath = new GraphicsPath())
        {
            AddRounded(bodyPath, body, Theme.Dip(this, 9));
            g.DrawPath(border, bodyPath);
        }

        // 让尾巴和身体接上：先把身体底边/顶边在尾巴根部的 1px 描边盖掉，再补两条侧边。
        Point[] tail = TailPoints;
        using (var erase = new SolidBrush(Theme.Surface))
        {
            int y = _tailDown ? body.Bottom - 1 : body.Top;
            g.FillRectangle(erase, tail[0].X, y, tail[2].X - tail[0].X, 2);
        }

        g.DrawLine(border, tail[0], tail[1]);
        g.DrawLine(border, tail[1], tail[2]);

        int pad = Theme.Dip(this, 10);
        var textBox = new Rectangle(
            body.X + pad,
            body.Y + pad,
            Math.Max(1, body.Width - (pad * 2)),
            Math.Max(1, body.Height - (pad * 2)));
        Ink.TextWrap(g, _text, Theme.Font(TypeRole.Body), Theme.Text, textBox);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _region?.Dispose();
            _shape?.Dispose();
        }

        base.Dispose(disposing);
    }
}
