// 第 3 步的示意动画：窗内来回移动的高亮圈。
//
// 它是**示意**的 —— 真的穿透演示需要一个通用的分层覆盖窗，而宿主今天没有那个东西
// （RFC-16 §6 已否决拿 LayeredFrameWindow 当通用覆盖窗用）。
// 机器判据只到「计时器已启动且帧计数在推进」，不判「动画逐帧正确」。
//
// 单独成文件的原因：D-03c 的单文件 400 行上限。
using System;
using System.Drawing;
using System.Windows.Forms;

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
        BackColor = Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2);
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
        int travel = Math.Max(1, Width - 48);
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
        using var pen = new Pen(Color.FromArgb(0xFF, 0x3A, 0x6E, 0xC0), 3);
        int y = Math.Max(0, (Height / 2) - 16);
        e.Graphics.DrawEllipse(pen, _offset, y, 32, 32);
    }
}
