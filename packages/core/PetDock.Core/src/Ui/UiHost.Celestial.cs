// 天体模式的宿主半边（2026-10-29；2026-10-30 改成万有引力）。
//
// 为什么用独立计时器而不是搭在 250ms 的滴答上：250ms 一步的运动是一跳一跳的，
// 看起来像卡顿而不是运动。30ms 只在模式打开时跑，关掉就停 —— 不占常态开销。
//
// 运动模型在 CelestialMotion（纯函数，等质量质点互相吸引）。本文件只负责：
//   1. 从桌面上的每只取位置，配上**上一步的速度**（速度是本模式的状态，宿主持有）；
//   2. 让位规则：模式打开时把每只的 ExternalMotion 置真（行为层的 moveTo 让位）；
//      正在拖动 / 正在抛掷的那只这一帧不进方程（用户手里的是它，物理在飞的是它），
//      并且把它积攒的速度清掉 —— 松手之后从静止重新加入，而不是带着旧速度窜出去。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    private const int CelestialIntervalMs = 33;

    private System.Windows.Forms.Timer? _celestialTimer;
    private bool _celestial;
    private long _celestialLastMs;

    /// <summary>
    /// 每只宠物在天体模式里的速度（像素/秒），按窗口句柄存。
    /// 为什么必须存：万有引力是二阶的（位置 + 速度），不像旧的匀速公转可以从位置反推角度；
    /// 键用句柄而不是槽位 id —— 主窗与额外槽位在这个模式里没有区别，都是桌面上的一个质点。
    /// </summary>
    private readonly Dictionary<IntPtr, (double Vx, double Vy)> _celestialVelocity = new();

    /// <summary>天体模式是否开着（自检与诊断读它）。</summary>
    internal bool CelestialMode => _celestial;

    /// <summary>设置页那枚开关的执行体：立即生效，不用重启。</summary>
    private void ApplyCelestialMode(bool enabled)
    {
        if (_celestial == enabled)
        {
            return;
        }

        _celestial = enabled;
        if (enabled)
        {
            StartCelestial();
        }
        else
        {
            StopCelestial();
        }

        KernelLog.Write($"celestial-mode enabled={enabled} pets={LiveWindows().Count}");
    }

    /// <summary>开始公转：先把每只宠物的运动属主收回来（ExternalMotion），再起 30ms 计时器。</summary>
    private void StartCelestial()
    {
        foreach ((LayeredFrameWindow _, PetInstance? instance) in LiveWindows())
        {
            if (instance is not null)
            {
                instance.ExternalMotion = true;
            }
        }

        // 每次都从静止开始：上一次模式里的速度没有意义（宠物可能已经被拖到别处）。
        _celestialVelocity.Clear();
        _celestialLastMs = Environment.TickCount64;
        _celestialTimer ??= new System.Windows.Forms.Timer { Interval = CelestialIntervalMs };
        _celestialTimer.Tick -= OnCelestialTick;
        _celestialTimer.Tick += OnCelestialTick;
        _celestialTimer.Start();
    }

    /// <summary>停公转：把运动属主还给行为层（ExternalMotion 归假）。</summary>
    private void StopCelestial()
    {
        _celestialTimer?.Stop();
        _celestialVelocity.Clear();
        foreach ((LayeredFrameWindow _, PetInstance? instance) in LiveWindows())
        {
            if (instance is not null)
            {
                instance.ExternalMotion = false;
            }
        }
    }

    private void OnCelestialTick(object? sender, EventArgs e) => StepCelestial();

    /// <summary>走一步公转（自检可以直接驱动它，不必等计时器）。</summary>
    internal void StepCelestial()
    {
        if (!_celestial)
        {
            return;
        }

        long now = Environment.TickCount64;
        double seconds = Math.Clamp((now - _celestialLastMs) / 1000.0, 0, 0.1);
        _celestialLastMs = now;
        if (seconds <= 0)
        {
            return;
        }

        List<(LayeredFrameWindow Window, PetInstance? Instance)> pets = LiveWindows();
        if (pets.Count == 0)
        {
            _celestialVelocity.Clear();
            return;
        }

        // 收集这一帧参与引力的那些：用户手里拖着的、正在飞的都不进方程。
        // 同时把它们的速度清零（松手之后从静止重新加入，不许带着旧速度窜出去）。
        var moving = new List<(LayeredFrameWindow Window, Rectangle Area)>(pets.Count);
        var bodies = new List<CelestialBody>(pets.Count);
        var live = new HashSet<IntPtr>();
        foreach ((LayeredFrameWindow window, PetInstance? _) in pets)
        {
            IntPtr handle = window.WindowHandle;
            live.Add(handle);
            if (window.IsDisposed || !window.Visible || window.PhysicsActive || window.IsDraggingNow)
            {
                _celestialVelocity.Remove(handle);
                continue;
            }

            Rectangle bounds = window.Bounds;
            Rectangle area = Screen.FromPoint(bounds.Location).WorkingArea;
            (double vx, double vy) = _celestialVelocity.TryGetValue(handle, out var v) ? v : (0, 0);
            moving.Add((window, area));
            bodies.Add(new CelestialBody(bounds.Left, bounds.Top, vx, vy));
        }

        // 桌面上已经没有的那些：把速度表里的残留清掉（不然句柄复用时会带着别人的速度）。
        if (_celestialVelocity.Count > live.Count)
        {
            var stale = new List<IntPtr>();
            foreach (IntPtr handle in _celestialVelocity.Keys)
            {
                if (!live.Contains(handle))
                {
                    stale.Add(handle);
                }
            }

            foreach (IntPtr handle in stale)
            {
                _celestialVelocity.Remove(handle);
            }
        }

        if (bodies.Count == 0)
        {
            return;
        }

        int width = moving[0].Window.Bounds.Width;
        int height = moving[0].Window.Bounds.Height;
        IReadOnlyList<CelestialBody> next = CelestialMotion.Step(
            bodies, moving[0].Area, width, height, seconds);

        for (int i = 0; i < moving.Count && i < next.Count; i++)
        {
            CelestialBody body = next[i];
            LayeredFrameWindow window = moving[i].Window;
            _celestialVelocity[window.WindowHandle] = (body.Vx, body.Vy);
            window.MoveTo((int)Math.Round(body.X), (int)Math.Round(body.Y));
        }
    }

    /// <summary>桌面上的每只（主窗 + 额外槽位）；与托盘/面板用的是同一份枚举口径。</summary>
    private List<(LayeredFrameWindow Window, PetInstance? Instance)> LiveWindows()
    {
        var list = new List<(LayeredFrameWindow, PetInstance?)>(1 + (_petSet?.Slots.Count ?? 0));
        if (!_primaryRemoved)
        {
            list.Add((_window, _pet));
        }

        if (_petSet is not null)
        {
            foreach (PetWindowSet.Slot slot in _petSet.Slots)
            {
                list.Add((slot.Window, slot.Instance));
            }
        }

        return list;
    }
}
