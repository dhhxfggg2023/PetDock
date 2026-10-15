// Program.cs 的自检辅助部分：命中探针重试、命中点空位查找与逐项实况取样。
// 拆自原 Program.cs，逻辑逐行一致（docs/40 D-03c：单文件不超过 400 行）。
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using PetDock.Core.Compositor;

namespace PetDock.Core;

internal static partial class Program
{
    /// <summary>
    /// 在屏幕上找一个「本窗放上去不会与任何非本进程顶层窗重叠」的位置，供命中探针使用。    /// 找不到时返回 null，并由调用方把结论标成不可信——绝不用默认位置硬测再谎报穿透坏了。
    /// </summary>
    private static Point? FindUncoveredSpot(IntPtr own, Size size, List<string> coverers)
    {
        Rectangle area = Screen.PrimaryScreen!.Bounds;
        int maxX = Math.Max(area.Left, area.Right - size.Width);
        int maxY = Math.Max(area.Top, area.Bottom - size.Height);

        foreach ((int x, int y) in CandidateSpots(area, size))
        {
            int left = Math.Min(Math.Max(area.Left, x), maxX);
            int top = Math.Min(Math.Max(area.Top, y), maxY);
            if (!AnyForeignWindowCovers(new Rectangle(left, top, size.Width, size.Height), own, coverers))
            {
                return new Point(left, top);
            }
        }

        return null;
    }

    /// <summary>
    /// 本进程窗口在全局 Z 序里的位置，外加它前面 12 个可见顶层窗的身份。
    /// WindowFromPoint 的答案由这张表决定；「谁压着谁」说不清时，这张表就是答案。
    /// </summary>
    private static string ZOrderReport(IntPtr own)
    {
        int ownRank = -1;
        var ahead = new List<string>();
        int rank = 0;

        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd))
            {
                return true;
            }

            if (hwnd == own)
            {
                ownRank = rank;
                return false;
            }

            if (ownRank < 0 && ahead.Count < 12)
            {
                Win32.GetWindowRect(hwnd, out RECT rect);
                ahead.Add($"#{rank} {Win32.WindowDescription(hwnd)} rect={rect.Left},{rect.Top} {rect.Width}x{rect.Height}");
            }

            rank++;
            return true;
        }, IntPtr.Zero);

        return $"own-rank={ownRank} ahead-of-us={string.Join(" >> ", ahead)}";
    }

    /// <summary>
    /// 窗口此刻的 Win32 实况：矩形、是否可见、是否最小化、样式位。
    /// 「窗口被创建了」和「窗口在屏幕上是可见的」是两件事，命中探针答不了后者，这条答得了。
    /// </summary>
    private static string WindowStateReport(IntPtr hwnd)
    {
        Rectangle rect = Win32.ScreenRect(hwnd);
        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        long exStyle = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        bool visible = (style & Win32.WS_VISIBLE) != 0;
        return $"rect={rect.Left},{rect.Top} {rect.Width}x{rect.Height} visible={Flag(visible)}"
            + $" iconic={Flag(Win32.IsIconic(hwnd))} style=0x{style:X} exstyle=0x{exStyle:X}";
    }

    /// <summary>
    /// 把窗口矩形中心的屏幕像素与帧缓冲中心像素对一遍。
    /// 这是唯一一条「像素真的落到屏幕上了」的证据：WindowFromPoint 只回答谁在最上面，
    /// 一个铺满屏幕的顶层窗就能让它说不清；而屏幕像素对上了，就说明这一帧确实合成可见。
    /// 对不上时如实报出两个颜色，不下「穿透坏了」这种结论。
    /// </summary>
    private static string ScreenShowsPetPixel(LayeredFrameWindow window, HitReport hit)
    {
        // 取样点必须是帧缓冲自己的中心：帧缓冲是物理尺寸（150% DPI 下 192x192），写死 64 是逻辑中心，
        // 两者对不上时这条「像素真的落到屏幕上了」的证据会假阴性。
        Size frameSize = window.FrameSize;
        (byte B, byte G, byte R, byte A)? frame = frameSize.IsEmpty
            ? null
            : window.SampleFramePixel(frameSize.Width / 2, frameSize.Height / 2);
        if (frame is null)
        {
            return "frame=none（帧缓冲不可用）";
        }

        string onScreen = Win32.SampleScreen(hit.Center);
        string[] parts = onScreen.Split(',');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out int r)
            || !int.TryParse(parts[1], out int g)
            || !int.TryParse(parts[2], out int b))
        {
            return $"screen-unknown({onScreen})";
        }

        int delta = Math.Abs(r - frame.Value.R) + Math.Abs(g - frame.Value.G) + Math.Abs(b - frame.Value.B);
        return $"frame=({frame.Value.R},{frame.Value.G},{frame.Value.B}) screen=({onScreen}) delta={delta}";
    }

    /// <summary>
    /// 把自己最小化一瞬，量「同一个窗口矩形」的命中结果，然后把它放回来。
    /// 这是屏幕被第三方顶层窗占满时唯一还能回答「宿主窗之外的窗有没有在这一层压着它」的办法；
    /// 代价是采样到的是最小化状态，所以调用方必须把这件事写进报告（hit-coverage）。
    /// 窗口放回后位置由调用方紧接着的 MoveToCenter 重新钉住。
    /// </summary>
    private static MinimizedMeasurement MeasureMinimized(LayeredFrameWindow window)
    {
        Size size = window.GetPhysicalSize();
        Rectangle rect = Win32.ScreenRect(window.WindowHandle);
        POINT center = new() { X = rect.Left + (rect.Width / 2), Y = rect.Top + (rect.Height / 2) };

        Win32.ShowWindow(window.WindowHandle, Win32.SW_SHOWNOACTIVATE);
        Application.DoEvents();

        var vacant = new List<string>();
        if (AnyForeignWindowCovers(rect, window.WindowHandle, vacant))
        {
            return new MinimizedMeasurement(null, $"minimized-flash-skipped: 仍被 {string.Join("|", vacant)} 压着，取不到样");
        }

        HitReport measured = HitProbe.Probe(window.WindowHandle);

        Win32.ShowWindow(window.WindowHandle, Win32.SW_RESTORE);
        Application.DoEvents();
        Thread.Sleep(SettleMs);
        Application.DoEvents();

        return new MinimizedMeasurement(measured, "minimized-flash");
    }

    /// <summary>最小化闪烁测量的结果：取到样就带上报告，没取到就只说明为什么。</summary>
    private readonly record struct MinimizedMeasurement(HitReport? Hit, string Note);

    /// <summary>
    /// 连测数次，取第一次「本窗在命中点上确实最靠前」的结果；都拿不到就返回最后一次。
    /// 为什么要重试：插到 topmost 层最前面之后，Windows 的 Z 序重排与下一个采样点之间可能还差一拍，
    /// 单次采样会把「刚挪完还没生效」误报成穿透坏了。这里的返回值本身会如实报告是谁占着命中点。
    /// </summary>
    private static HitReport ProbeUntilSelfOnTop(LayeredFrameWindow window)
    {
        HitReport? last = null;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0)
            {
                window.BringToFrontOfTopmost();
                Application.DoEvents();
                Thread.Sleep(120);
                Application.DoEvents();
            }

            HitReport probe = HitProbe.Probe(window.WindowHandle);
            if (probe.OpaqueHit)
            {
                return probe;
            }

            last = probe;
        }

        return last!;
    }

    /// <summary>候选位置按「越靠边越优先」排序：正中、四角、四边中点，最后是整屏按 16px 步长的网格。</summary>
    private static IEnumerable<(int X, int Y)> CandidateSpots(Rectangle area, Size size)
    {
        int cx = area.X + (area.Width - size.Width) / 2;
        int cy = area.Y + (area.Height - size.Height) / 2;
        yield return (cx, cy);

        int right = area.Right - size.Width;
        int bottom = area.Bottom - size.Height;
        yield return (cx, bottom);
        yield return (right, cy);
        yield return (area.Left, cy);
        yield return (cx, area.Top);
        yield return (area.Left, area.Top);
        yield return (right, area.Top);
        yield return (area.Left, bottom);
        yield return (right, bottom);

        for (int gy = 0; gy < 5; gy++)
        {
            for (int gx = 0; gx < 5; gx++)
            {
                yield return (area.X + (area.Width / 5) * gx, area.Y + (area.Height / 5) * gy);
            }
        }

        // 兜底：整屏按 16px 步长扫一遍，尽量在「屏幕被别的顶层窗占得差不多」时也能找到一个空位。
        for (int y = area.Top; y <= area.Bottom - size.Height; y += 16)
        {
            for (int x = area.Left; x <= area.Right - size.Width; x += 16)
            {
                yield return (x, y);
            }
        }
    }

    /// <summary>
    /// 矩形内是否有「别的进程的顶层窗」中心落在里面。自身句柄忽略；
    /// 只认真正的顶层窗（WS_EX_TOOLWINDOW 不算），所以与文档无关的小工具窗也照样躲开。
    /// </summary>
    private static bool AnyForeignWindowCovers(Rectangle rect, IntPtr own, List<string> coverers)
    {
        uint self = (uint)Environment.ProcessId;
        int cx = rect.Left + rect.Width / 2;
        int cy = rect.Top + rect.Height / 2;
        var probes = new[] { new Point(cx, cy), new Point(cx, rect.Top + 2), new Point(cx, rect.Bottom - 2), new Point(rect.Left + 2, cy), new Point(rect.Right - 2, cy) };
        bool any = false;

        foreach (Point probe in probes)
        {
            IntPtr found = Win32.TopLevelWindowFromPoint(new POINT { X = probe.X, Y = probe.Y });
            if (found == IntPtr.Zero || found == own)
            {
                continue;
            }

            long style = Win32.GetWindowLongPtr(found, Win32.GWL_EXSTYLE).ToInt64();
            if ((style & Win32.WS_EX_TOOLWINDOW) != 0)
            {
                continue;
            }

            // 我们自己整窗都在 TOPMOST 层里，普通顶层窗（哪怕最大化铺满整屏）在这一层之下，
            // 根本盖不住我们。不判这一位，就会把「桌面上开着一个最大化的浏览器」当成挡路，
            // 于是每次自检都在这条与本机无关的判据上 SELF-FAIL。
            // 本机实测：DSH 的 GUI 窗 exstyle=0x200100 里没有任何 TOPMOST 位，却一直被算成 coverer。
            if ((style & Win32.WS_EX_TOPMOST) == 0)
            {
                continue;
            }

            Win32.GetWindowThreadProcessId(found, out uint pid);
            if (pid == self)
            {
                continue;
            }

            // 看不见的窗口不算挡路：WindowFromPoint 会命中已隐藏/零尺寸但尚未销毁的残留窗
            // （本机实测：一个 title 为空、矩形为 0x0 的壳窗），把它当真会让扫描永远找不到空位。
            Win32.GetWindowRect(found, out RECT covered);
            if (!Win32.IsWindowVisible(found) || covered.Width <= 0 || covered.Height <= 0)
            {
                continue;
            }

            var title = new StringBuilder(128);
            Win32.GetWindowText(found, title, title.Capacity);
            if (title.Length == 0)
            {
                continue;
            }

            any = true;
            string label = Win32.WindowDescription(found);
            if (!coverers.Contains(label))
            {
                coverers.Add(label);
            }
        }

        return any;
    }

    private static string Pixel((byte B, byte G, byte R, byte A)? pixel)
    {
        return pixel is null ? "none" : $"bgra={pixel.Value.B},{pixel.Value.G},{pixel.Value.R},{pixel.Value.A}";
    }

    private static string Fmt(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static void AttachConsole()
    {
        if (!Win32.AttachConsole(Win32.ATTACH_PARENT_PROCESS))
        {
            // 没有父进程控制台可接（例如从资源管理器启动）：自建一个
            Win32.AllocConsole();
        }

        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // 重定向场景下改编码会失败，保持默认即可
        }
    }

    private sealed record Check(string Key, string Value, bool Expected);
}
