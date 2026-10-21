// 常驻形态的「窗口可见性 ↔ 帧循环」绑定（RFC-14 §2.1）。
//
// 为什么要有这个类：RFC-14 的实证是「常驻进程里有一个 128x128 的分层窗，但它 visible=False，
// 而且常驻形态根本没有帧泵」。修这两件事要同时做 —— 只显示不启泵，屏幕上是一张静止的首帧；
// 只起泵不显示，帧在跑而用户看不见。把这两件事绑在一个类里，是为了**让它们不可能只做一半**：
// 显示窗口就一定有泵，隐藏窗口就一定没有泵。
//
// 为什么不住在 UiHost 里：UiHost 已经是 partial × 4（135 + 180 + 88 + 203 行），
// D-03c 的 400 行上限是硬门禁；而更重要的是**属主清晰**：谁决定「现在该不该出帧」，
// 答案只有这一个类。托盘的三条命令（显示/隐藏全部、隐藏宠物、移除宠物）都走它，
// 不再直接摸窗口的 Show/Hide —— 直接摸就会绕过「隐藏即停泵」。
//
// 帧泵为什么复用 FramePump：docs/12 §4 的帧预算按 8.3ms 计，UI 线程忙时帧率不可预期；
// FramePump 已有 30fps 目标 + 睡眠补偿 + 超预算计数（src/Compositor/FramePump.cs），
// 复用它而不是再造一个节流器。
using System;
using PetDock.Core.Compositor;

namespace PetDock.Core.Ui;

/// <summary>
/// 常驻帧循环：宠物窗口可见时跑帧，不可见时停帧。
/// 幂等：<see cref="Start"/> 在已经跑着时什么都不做。
/// </summary>
internal sealed class ResidentFrameLoop : IDisposable
{
    private readonly LayeredFrameWindow _window;
    private FramePump? _pump;
    private bool _pumping;
    private bool _disposed;

    /// <summary>目标帧周期（毫秒）。泵还没起时先记着，起泵时套用（docs/18 §2.3 的空闲降帧阈值）。</summary>
    private double _targetFrameMs = 1000.0 / 30.0;

    public ResidentFrameLoop(LayeredFrameWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    /// <summary>
    /// 设置目标帧周期（毫秒）。立即生效：泵在跑就改泵，泵没起就记下来等它起。
    /// 越界由 FramePump.SetTargetFrameMs 拉回 [16.7, 200]。
    /// </summary>
    public void SetTargetFrameMs(double milliseconds)
    {
        _targetFrameMs = milliseconds;
        _pump?.SetTargetFrameMs(milliseconds);
    }

    /// <summary>当前目标帧周期（毫秒）。</summary>
    public double TargetFrameMs => _pump?.TargetFrameMs ?? _targetFrameMs;

    /// <summary>宠物窗口当前是否可见。</summary>
    public bool Visible => _window.Visible;

    /// <summary>帧泵是否在跑。</summary>
    public bool Pumping => _pumping;

    /// <summary>已出帧数（停泵后保留最后一次读数，便于判据比较「停之前」与「停之后」）。</summary>
    public int Frames => _pump?.Frames ?? 0;

    /// <summary>单帧最慢耗时（毫秒）。诊断包读它（RFC-17 §3.2）。</summary>
    public double MaxFrameMs => _pump?.MaxFrameMs ?? 0;

    /// <summary>单帧平均耗时（毫秒，累计平均 —— 诊断包必须标明这一点）。</summary>
    public double AvgFrameMs => _pump?.AvgFrameMs ?? 0;

    /// <summary>超过 SM-04 预算的帧数。</summary>
    public long OverBudgetFrames => _pump?.OverBudgetFrames ?? 0;

    /// <summary>帧泵已运行时长（毫秒）。</summary>
    public long ElapsedMs => _pump?.ElapsedMs ?? 0;

    /// <summary>
    /// 显示窗口并起帧泵。幂等：窗口已可见且泵在跑时直接返回。
    /// 顺序有意如此：**先显示再起泵** —— 反过来的话，头几帧会被画进一个还看不见的窗口。
    /// </summary>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        if (!_window.Visible)
        {
            _window.Show();
            _window.BringToFrontOfTopmost();
            // Show 之后再present一次：分层窗的内容是靠 UpdateLayeredWindow 挂在窗口上的，
            // 窗体从隐藏到可见这一次状态切换可能让 OS 丢掉那一层，先重画一遍最省事。
            _window.PresentFirstFrame();
        }

        if (_pumping)
        {
            return;
        }

        var pump = new FramePump();
        pump.SetTargetFrameMs(_targetFrameMs);
        pump.Start(() => _window.RenderFrame());
        _pump = pump;
        _pumping = true;
        KernelLog.Write("resident-loop started visible=true pumping=true");
    }

    /// <summary>
    /// 隐藏窗口并**停泵**。隐藏的宠物不该继续烧 CPU —— 这是 docs/12 §4 的节流精神，
    /// 也是托盘「隐藏宠物」的真实含义（不是「只是看不见了，CPU 照烧」）。
    /// </summary>
    public void Hide()
    {
        _window.Hide();
        StopPump();
    }

    /// <summary>显示/隐藏切换（托盘的「显示/隐藏全部」）。</summary>
    public void Toggle()
    {
        if (_window.Visible)
        {
            Hide();
            return;
        }

        Start();
    }

    /// <summary>停泵（不碰窗口可见性）。</summary>
    private void StopPump()
    {
        if (!_pumping)
        {
            return;
        }

        _pumping = false;
        _pump?.Stop();
        KernelLog.Write("resident-loop stopped visible=false pumping=false");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPump();
        _pump = null;
    }
}
