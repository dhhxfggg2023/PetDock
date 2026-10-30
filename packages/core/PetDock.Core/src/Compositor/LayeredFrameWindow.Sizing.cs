// 窗口尺寸这一半（2026-10-28 从 LayeredFrameWindow.cs 拆出：D-03c 单文件 400 行上限）。
//
// 不变量：窗口尺寸的唯一权威是 GetWindowRect 的实测值；逻辑尺寸只决定「请求多大」。
// 帧缓冲必须与实测尺寸严格一致，否则 UpdateLayeredWindow 会从源 DC 之外取图 —— 整帧不可见，且不报错。
using System;
using System.Drawing;

namespace PetDock.Core.Compositor;

internal sealed partial class LayeredFrameWindow
{
    private Size _logicalSize = new(128, 128);

    /// <summary>窗口的逻辑尺寸（不随 DPI 变化）。</summary>
    public Size LogicalSize => _logicalSize;

    /// <summary>
    /// 改宠物窗口的逻辑边长（设置页的「宠物大小」）。
    /// 物理尺寸仍由 DPI 缩放决定；帧缓冲会在下一次合成前按实测尺寸重建，
    /// 所以这里不用手动碰 DIB。幂等：尺寸没变就什么都不做。
    /// </summary>
    public void SetLogicalSize(int edge)
    {
        int size = Math.Clamp(edge, 64, 256);
        if (_logicalSize.Width == size && _logicalSize.Height == size)
        {
            return;
        }

        _logicalSize = new Size(size, size);
        if (!IsHandleCreated)
        {
            return;
        }

        ApplyPhysicalSize();
        ApplyFrameToWindowSize();
        PresentFirstFrame();
    }

    /// <summary>窗口物理尺寸（逻辑尺寸 × 当前 DPI 缩放）。</summary>
    public Size GetPhysicalSize()
    {
        double scale = DeviceDpi / 96.0;
        return new Size(
            (int)Math.Round(LogicalSize.Width * scale),
            (int)Math.Round(LogicalSize.Height * scale));
    }

    /// <summary>窗口外接矩形的实际物理尺寸（实测值，不是请求值）。</summary>
    public Size WindowSize => Win32.ScreenRect(Handle).Size;

    /// <summary>帧缓冲尺寸（=窗口实际物理尺寸）；无帧缓冲时为 0x0。</summary>
    public Size FrameSize => _frame is null ? Size.Empty : new Size(_frame.Width, _frame.Height);

    /// <summary>
    /// 让帧缓冲的尺寸追上窗口的实际尺寸。
    /// 尺寸的唯一权威是 GetWindowRect 的实测值：SetWindowPos 请求 192x192 而窗口落地成 192x128
    /// 这种事真的发生过（见构造函数里为什么不设 MaximumSize），
    /// 而 DIB 与窗口一旦不等，UpdateLayeredWindow 就从源 DC 之外取图 —— 整帧不可见，且不报错。
    /// </summary>
    private void ApplyFrameToWindowSize()
    {
        Size actual = WindowSize;
        if (actual.Width <= 0 || actual.Height <= 0)
        {
            return;
        }

        if (_frame is not null && _frame.Width == actual.Width && _frame.Height == actual.Height)
        {
            return;
        }

        DibFrame rebuilt = DibFrame.Create(actual.Width, actual.Height);
        rebuilt.Hwnd = Handle;
        _frame?.Dispose();
        _frame = rebuilt;
    }

    /// <summary>把窗口尺寸设为逻辑尺寸 × 当前 DPI 缩放。</summary>
    public void ApplyPhysicalSize()
    {
        Size physical = GetPhysicalSize();
        Win32.SetWindowPos(Handle, IntPtr.Zero, 0, 0, physical.Width, physical.Height,
            Win32.SWP_NOMOVE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }
}
