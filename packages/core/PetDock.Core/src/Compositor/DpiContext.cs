// 实现 docs/12 §3「尺寸 / 坐标 / DPI」：进程感知等级与窗口 DPI 的只读查询
using System.Runtime.InteropServices;

namespace PetDock.Core.Compositor;

/// <summary>
/// DPI 只读帮助。进程感知等级在进程启动那一刻就固定（见 Program.cs），
/// 这里只负责读出来，不做任何设置。
/// </summary>
internal static class DpiContext
{
    /// <summary>
    /// 该窗口所在线程的 DPI 感知等级是否为 Per-Monitor V2。
    /// 只读查询，不设置任何东西——进程感知等级在启动时固定（Program.cs）。
    /// </summary>
    public static bool IsPerMonitorV2(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        // 用窗口句柄反查感知上下文：GetWindowDpiAwarenessContext 返回的上下文与进程级
        // SetProcessDpiAwarenessContext 设的那个可比较；而 user32 里并不存在
        // GetProcessDpiAwarenessContext 这个入口（EntryPointNotFoundException）。
        // 线程创建的窗口继承创建线程的感知等级，查窗口即等价于查进程。
        IntPtr current = Win32.GetWindowDpiAwarenessContext(hwnd);
        if (current == IntPtr.Zero)
        {
            return false;
        }

        return Win32.AreDpiAwarenessContextsEqual(current, Win32.DpiPerMonitorAwareV2);
    }

    /// <summary>取窗口的 DPI（Win10 1607+；不支持时返回 96 作为系统 DPI 约定值）。</summary>
    public static uint DpiForWindow(IntPtr hwnd)
    {
        uint dpi = Win32.GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : dpi;
    }
}
