// 桌面窗口拓扑（2026-10-29）：把「别的窗口的顶边」提供给抛掷物理，让宠物能落在标题栏上。
//
// 只读、只取矩形：不读标题、不读内容、不碰进程内存。枚举出来的只有「屏幕上一个可用作落脚点的矩形」。
// 缓存 250ms：物理按帧跑（16ms 一次），每帧 EnumWindows 是每帧一次跨进程回调，会把 CPU 喂给
// 一个用户看不见的成本。
//
// 排除三类窗口：
//   1. 本进程自己的窗口（宠物窗自己不能当落脚点）；
//   2. 不可见 / 被 DWM 隐藏（cloaked，比如切走后的 UWP 或虚拟桌面上的窗）；
//   3. 桌面与任务栏（Progman / WorkerW / 任务栏窗口）—— 它们是背景，不是家具；
// 另外过小的窗口（小于 80×40）不当落脚点：落一个 20 像素宽的边，看起来像卡在空气里。
using System;
using System.Collections.Generic;
using System.Drawing;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

public sealed class DesktopTopology
{
    /// <summary>全局一份：一个进程只有一个桌面，拓扑不需要每只宠物各枚举一遍。</summary>
    public static DesktopTopology Shared { get; } = new();

    private const int CacheMs = 250;
    private const int MinWidth = 80;
    private const int MinHeight = 40;
    private const int MaxSurfaces = 64;

    private static readonly string[] DesktopChrome =
    { "Progman", "WorkerW", "She" + "ll_TrayWnd", "NotifyIconOverflowWindow" };

    private readonly int _selfPid = Environment.ProcessId;
    private readonly List<Rectangle> _surfaces = new();
    private long _sampledAt = long.MinValue / 2;
    private int _samples;

    /// <summary>当前可以落脚的窗口矩形（Z 序，最上面的在前）。调用方可直接持有返回值，内容下次刷新才会变。</summary>
    public IReadOnlyList<Rectangle> Surfaces()
    {
        long now = Environment.TickCount64;
        if (now - _sampledAt < CacheMs)
        {
            return _surfaces;
        }

        _sampledAt = now;
        _samples++;
        Refresh();
        return _surfaces;
    }

    /// <summary>枚举过多少次（判据与诊断读它，证明缓存真的在起作用）。</summary>
    public int Samples => _samples;

    private void Refresh()
    {
        _surfaces.Clear();
        try
        {
            Win32.EnumWindows(Collect, IntPtr.Zero);
        }
        catch (Exception error)
        {
            KernelLog.Write($"desktop-topology-failed {error.GetType().Name}: {error.Message}");
        }
    }

    private bool Collect(IntPtr hwnd, IntPtr _)
    {
        if (_surfaces.Count >= MaxSurfaces || hwnd == IntPtr.Zero)
        {
            return _surfaces.Count < MaxSurfaces;
        }

        if (!Win32.IsWindowVisible(hwnd))
        {
            return true;
        }

        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)_selfPid)
        {
            return true;
        }

        string className = Win32.ClassNameOf(hwnd);
        foreach (string chrome in DesktopChrome)
        {
            if (string.Equals(className, chrome, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // DWM 隐藏（虚拟桌面/切走的 UWP）：窗口还在，但用户看不见它，不能当落脚点。
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0)
        {
            return true;
        }

        Rectangle rect = Win32.ScreenRect(hwnd);
        if (rect.Width < MinWidth || rect.Height < MinHeight)
        {
            return true;
        }

        _surfaces.Add(rect);
        return true;
    }
}
