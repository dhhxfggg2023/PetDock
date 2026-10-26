// 实现 docs/12-window-compositor.md §2「规则：一切从 Win32 出发」：透明分层窗所需的 Win32 常量、结构体与 P/Invoke 集合
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace PetDock.Core.Compositor;

/// <summary>
/// Win32 原始入口集合。命中测试完全由 OS 读取 alpha 完成（docs/12 §2.3），
/// 因此本文件不包含任何命中判定逻辑，只提供取窗口矩形与发送 WM_NCHITTEST 的只读探针。
/// 诊断探针（ClassNameAtPoint / WindowDescription / ProcessNameOf / SampleScreen）在
/// LayeredWindowApi.Diagnostics.cs —— docs/40 D-03c 的拆分半边。
/// </summary>
internal static partial class Win32
{
    internal const int WM_ACTIVATEAPP = 0x001C;

    internal const int GWL_EXSTYLE = -20;
    internal const int GWL_STYLE = -16;
    internal const long WS_VISIBLE = 0x10000000L;

    internal const int WS_EX_LAYERED = 0x00080000;
    internal const int WS_EX_TRANSPARENT = 0x00000020;
    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    internal const byte AC_SRC_OVER = 0x00;
    internal const byte AC_SRC_ALPHA = 0x01;
    internal const uint ULW_ALPHA = 0x00000002;

    internal const uint BI_RGB = 0;
    internal const uint DIB_RGB_COLORS = 0;

    internal const int WM_DPICHANGED = 0x02E0;
    internal const int WM_NCHITTEST = 0x0084;
    internal const int WM_CONTEXTMENU = 0x007B;
    internal const int WM_RBUTTONUP = 0x0205;
    internal const int HTTRANSPARENT = -1;
    internal const int HTCLIENT = 1;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr DpiPerMonitorAwareV2 = new(-4);

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const uint SWP_NOOWNERZORDER = 0x0200;

    internal const int SW_SHOW = 5;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int SW_RESTORE = 9;
    internal const int ATTACH_PARENT_PROCESS = -1;

    // ---- DPI 感知 ----

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AreDpiAwarenessContextsEqual(IntPtr left, IntPtr right);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    // ---- 扩展样式（64 位安全：IntPtr + *Ptr 入口） ----

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr newLong);

    internal static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
    {
        return IntPtr.Size == sizeof(long)
            ? GetWindowLongPtr64(hwnd, index)
            : new IntPtr(GetWindowLong32(hwnd, index));
    }

    internal static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong)
    {
        return IntPtr.Size == sizeof(long)
            ? SetWindowLongPtr64(hwnd, index, newLong)
            : new IntPtr(SetWindowLong32(hwnd, index, newLong.ToInt32()));
    }

    // ---- 合成 ----

    // 目标位置与尺寸必须显式给出：传 NULL 时本机返回 TRUE 但整层不合成（见 DibFrame.Present 的注释）。
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref POINT pptDst,
        ref SIZE psize,
        IntPtr hdcSrc,
        ref POINT pptSrc,
        int crKey,
        ref BLENDFUNCTION pblend,
        uint dwFlags);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BITMAPINFO pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    // ---- 窗口管理 ----

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(POINT point);

    internal const uint GA_ROOT = 2;

    /// <summary>取某点的顶层窗口：WindowFromPoint 会返回最深的子控件，往上走到根才是「谁在最上面」。</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    /// <summary>某点的顶层窗口句柄（直达根窗口，不被子控件干扰）。</summary>
    internal static IntPtr TopLevelWindowFromPoint(POINT point)
    {
        IntPtr hwnd = WindowFromPoint(point);
        return hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>枚举顶层窗口（EnumWindows 按 Z 序从头到尾回调），诊断 Z 序用。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // ---- 控制台 ----
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AttachConsole(int dwProcessId);

    // ---- 光标 ----

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetCursorPos(int X, int Y);

    // ---- 前台窗口 ----

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    // ---- DWM（RFC-21 §3.2）：只用来回答「DWM 有没有把本窗藏起来」，不判遮挡 ----

    /// <summary>
    /// DWMWA_CLOAKED 的属性号。经实测钉死 = 14：传错属性号时 DWM **静默返回 S_OK + 0**（实测 attr=33 就是 S_OK/0），
    /// 于是「永远没被藏」看起来像一条正常读数。这个数字必须写死在常量上，不许从别处猜。
    /// </summary>
    internal const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    // ---- 显示器（RFC-21 §3.2 的全屏判定要显示器矩形） ----

    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>窗口类名（RFC-21 §3.2 的全屏判定要排除桌面本体与任务栏的窗口类）。取不到返回 "(unknown)"。
    /// <paramref name="buffer"/> 由调用方复用：调用点每 tick 都跑，每次 new 一个 StringBuilder(256)
    /// 就是每帧 512B 的纯垃圾（D-14d 稳态判据会因此变红）。</summary>
    internal static string ClassNameOf(IntPtr hwnd, StringBuilder buffer)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "(unknown)";
        }

        buffer.Clear();
        int length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : "(unknown)";
    }

    // ---- 只读小助手 ----

    /// <summary>取窗口在屏幕坐标（物理像素）下的外接矩形。</summary>
    internal static Rectangle ScreenRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out RECT rect);
        return new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    /// <summary>向窗口发 WM_NCHITTEST，返回 user32 的命中码（HTCLIENT / HTTRANSPARENT …）。</summary>
    internal static int HitTest(IntPtr hwnd, POINT screenPoint)
    {
        long packed = ((long)(ushort)screenPoint.Y << 16) | (uint)(ushort)screenPoint.X;
        return SendMessage(hwnd, WM_NCHITTEST, IntPtr.Zero, (IntPtr)packed).ToInt32();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int cx;
    public int cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// GetMonitorInfo 的输出（RFC-21 §3.2）。字段顺序由 Win32 定义写死，不许调换；
/// rcMonitor / rcWork 复用同一个 RECT，不重复定义。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    /// <summary>调用前必须写成 sizeof(MONITORINFO)，否则 GetMonitorInfo 返回 false。</summary>
    public int cbSize;

    /// <summary>显示器的完整矩形（物理像素）。全屏判定比的就是它。</summary>
    public RECT rcMonitor;

    /// <summary>工作区矩形（扣掉任务栏等）。本片不用，但结构体必须完整，否则后面字段错位。</summary>
    public RECT rcWork;

    /// <summary>MONITORINFOF_PRIMARY = 1；其余位保留。</summary>
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BLENDFUNCTION
{
    public byte BlendOp;
    public byte BlendFlags;
    public byte SourceConstantAlpha;
    public byte AlphaFormat;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFO
{
    public BITMAPINFOHEADER bmiHeader;

    // BI_RGB 且 32 位时不需要颜色表；保留固定 4 个 DWORD 让整块内存连续可预测
    public uint bmiColors0;
    public uint bmiColors1;
    public uint bmiColors2;
    public uint bmiColors3;
}
