// 天体模式的宿主半边（2026-10-29）：开关、计时器、让位规则。
//
// 为什么用独立计时器而不是搭在 250ms 的滴答上：250ms 一步的「公转」是一跳一跳的，
// 看起来像卡顿而不是运动。30ms 只在模式打开时跑，关掉就停 —— 不占常态开销。
//
// 让位规则（三件事都来自「运动只能有一个属主」）：
//   1. 模式打开时把每只宠物的 ExternalMotion 置真：行为层的 moveTo 让位；
//   2. 正在拖动的那只不动：用户手里的是它，宿主别抢；
//   3. 正在抛掷的那只不动：物理在飞，落手之后再接着转。
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
            return;
        }

        foreach ((LayeredFrameWindow window, PetInstance? _) in pets)
        {
            if (window.IsDisposed || !window.Visible || window.PhysicsActive || window.IsDraggingNow)
            {
                continue;
            }

            Rectangle bounds = window.Bounds;
            Rectangle area = Screen.FromPoint(bounds.Location).WorkingArea;
            var center = new PetPoint(area.Left + (area.Width / 2.0), area.Top + (area.Height / 2.0));
            PetPoint next = CelestialMotion.Next(
                new PetPoint(bounds.Left, bounds.Top), center, seconds, area, bounds.Width, bounds.Height);
            window.MoveTo((int)Math.Round(next.X), (int)Math.Round(next.Y));
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
