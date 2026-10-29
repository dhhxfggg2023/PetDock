// 诊断探针（原规格 12 §2）：把「命中点被谁占了」这类结论落到**具体进程**上的那几个只读辅助。
// Win32 入口本体（常量、结构体、P/Invoke）在 LayeredWindowApi.cs；本文件是 原规格 40 D-03c 的拆分半边，
// 与本体同为 partial，直接复用本体的 GetClassName/GetWindowText/GetWindowLongPtr。
using System.Drawing;
using System.Text;

namespace PetDock.Core.Compositor;

internal static partial class Win32
{
    /// <summary>屏幕某点由哪个窗口类响应（诊断用：看清到底是谁挡住了透明角）。</summary>
    internal static string ClassNameAtPoint(POINT screenPoint)
    {
        return WindowDescription(WindowFromPoint(screenPoint));
    }

    /// <summary>
    /// 一个句柄的完整身份描述（诊断用）：句柄、进程、类名、标题、exstyle。
    /// 「命中点被谁占了」这类结论必须能落到一个具体进程上，否则只能靠猜。
    /// </summary>
    internal static string WindowDescription(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "(none)";
        }

        var className = new StringBuilder(256);
        int length = GetClassName(hwnd, className, className.Capacity);
        string cls = length > 0 ? className.ToString() : "(unknown)";
        var title = new StringBuilder(128);
        GetWindowText(hwnd, title, title.Capacity);
        GetWindowThreadProcessId(hwnd, out uint pid);
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        return $"0x{hwnd.ToInt64():X} pid={pid} ({ProcessNameOf(pid)}) class={cls} style=0x{style:X} title={title}";
    }

    /// <summary>窗口的类名（拓扑过滤用：桌面、任务栏这些 class 不是家具）。取不到返回空串。</summary>
    internal static string ClassNameOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        int length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    /// <summary>进程名（诊断用）；取不到就如实写 (unknown)，不要编一个名字出来。</summary>
    internal static string ProcessNameOf(uint pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return "(unknown)";
        }
    }

    /// <summary>读屏幕某点的像素（诊断用：验证桌面上实际合成出来的 alpha 结果）。</summary>
    internal static string SampleScreen(POINT screenPoint)
    {
        using var bitmap = new Bitmap(1, 1);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(screenPoint.X, screenPoint.Y, 0, 0, new Size(1, 1));
        }

        Color color = bitmap.GetPixel(0, 0);
        return $"{color.R},{color.G},{color.B}";
    }
}
