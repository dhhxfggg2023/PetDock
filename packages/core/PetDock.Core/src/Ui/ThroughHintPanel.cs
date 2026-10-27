// 第 3 步的示意动画：窗内来回移动的高亮圈。
//
// 它是**示意**的 —— 真的穿透演示需要一个通用的分层覆盖窗，而宿主今天没有那个东西
// （RFC-16 §6 已否决拿 LayeredFrameWindow 当通用覆盖窗用）。
// 机器判据只到「计时器已启动且帧计数在推进」，不判「动画逐帧正确」。
//
// 外观（原规格 18 §2.6）：它是一块「示意画布」，所以画成面板而不是纯色矩形 ——
// 圆角虚线框 + 韭菜色高亮圈，与深色界面同一套令牌。颜色一律取自 Theme，不写死。
//
// 单独成文件的原因：D-03c 的单文件 400 行上限。
using System;
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;

namespace PetDock.Core.Ui;

/// <summary>来回移动的高亮圈（示意「透明区域可以点穿」）。</summary>
internal sealed class ThroughHintPanel : Panel
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
    private int _direction = 1;
    private int _offset;
    private int _frameCount;

    public ThroughHintPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        _timer.Tick += Advance;
    }

    /// <summary>已推进的帧数（判据读它证明动画真的在动）。</summary>
    public int FrameCount => _frameCount;

    /// <summary>动画计时器是否在跑。</summary>
    public bool Running => _timer.Enabled;

    /// <summary>起动画。</summary>
    public void StartAnimation()
    {
        if (!_timer.Enabled)
        {
            _timer.Start();
        }
    }

    /// <summary>停动画。</summary>
    public void StopAnimation()
    {
        if (_timer.Enabled)
        {
            _timer.Stop();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Tick -= Advance;
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Advance(object? sender, EventArgs e)
    {
        _frameCount++;
        int diameter = Theme.Dip(this, 32);
        int travel = Math.Max(1, Width - diameter - (Theme.Dip(this, Theme.SpaceMd) * 2));
        _offset += _direction * 6;
        if (_offset >= travel)
        {
            _offset = travel;
            _direction = -1;
        }
        else if (_offset <= 0)
        {
            _offset = 0;
            _direction = 1;
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        int pad = Theme.Dip(this, Theme.SpaceMd);
        int radius = Theme.Dip(this, Theme.RadiusMd);
        var frame = new Rectangle(pad, pad, Math.Max(1, Width - (pad * 2)), Math.Max(1, Height - (pad * 2)));
        Ink.Stroke(e.Graphics, frame, radius, Theme.Line, Theme.Dip(this, 1));

        int diameter = Theme.Dip(this, 32);
        int y = Math.Max(pad, (Height / 2) - (diameter / 2));
        int stroke = Theme.Dip(this, 3);
        using var pen = Theme.Pen(Theme.Accent, stroke);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.DrawEllipse(pen, pad + _offset, y, diameter, diameter);
    }
}