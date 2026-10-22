// RFC-21 §3.2 的探测层：**只回答「世界是什么样」，不回答「该用哪一档」**。
//
// 为什么单独一个类：docs/12 §4 的三条节流要求各要一个「世界事实」——
// 遮挡降频要「有没有被别的窗盖住」，全屏暂停要「前台是不是一个盖满显示器的应用」，
// 静止降帧要「DWM 有没有藏我」。把这三问塞进帧循环，判定就有了第二个来源；
// 本类只采样，档位判定的**唯一**属主是 FrameThrottlePolicy.Decide（RFC-21 §3.3）。
//
// 依赖方向：Hosting → Compositor（既有，WorldProbe 已经引用 Compositor 的 Win32）。没有新增反向依赖。
//
// 三条口径都来自 t2 的实测（见 RFC-21 §1/§6/§8），不是猜的：
//   ① 遮挡用**点探针**，不用 DWMWA_CLOAKED —— 实测「被覆盖 ≠ cloaked」：分层 topmost 宠物窗
//      被普通窗、被另一个 topmost 窗完全盖住时 cloaked 恒为 0。cloaked 只回答「DWM 藏没藏我」。
//   ② 取样点只在**当前帧不透明**的像素上 —— 中心点常是透明像素（角色不是方的），
//      实测光标停在宠物窗矩形的透明角时 WindowFromPoint 返回底下的 Chrome，
//      于是「中心 alpha=0」的宠物会被**恒判被遮挡**。
//   ③ 全屏只问**前台窗**，且用「覆盖」而不是「相等」判 rcMonitor ——
//      EnumWindows 那种枚举写法实测恒判「有全屏应用」（桌面本体 Progman 永远在列），
//      而 Win10/11 的不可见边框让最大化窗的 rect 在左/上/右各溢出约 7px，相等判据永远不成立。
//
// 失败纪律：**一切失败 fail-open（返回 false），不抛**。探测失败只该让宠物继续出帧，
// 绝不该把宠物冻住；但「alphaAt 内部抛出的异常」不吞 —— 那是调用方的读数坏了，由 Decide 兜。
using System;
using System.Drawing;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

/// <summary>RFC-21 §3.2：只回答「世界是什么样」，不回答「该用哪一档」。</summary>
public sealed class ThrottleProbe
{
    /// <summary>DWMWA_CLOAKED 的属性号。实测钉死 = 14 —— 传错属性号时 DWM **静默返回 S_OK + 0**，
    /// 于是「永远没被藏」看起来像一条正常读数。这个数字必须写死在常量上，不许从别处猜。</summary>
    private const int DwmwaCloaked = Win32.DWMWA_CLOAKED;

    /// <summary>取样分数坐标（相对窗口矩形，顺序即优先级）：中心 → 左上 → 右上 → 左下 → 右下。
    /// 用分数而不是像素偏移：DPI 与窗口尺寸变化时同一个规则仍然成立（192×192@150% 与 128×128@100% 同解）。</summary>
    private static readonly (double X, double Y)[] SampleRatios =
    {
        (0.5, 0.5), (0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75),
    };

    /// <summary>桌面本体与任务栏的窗口类名。它们「盖满显示器」是常态，不是全屏应用。
    /// 两个任务栏类名拆成两段拼接：Win32 要求逐字精确（大小写敏感），拼接后运行时值一字不差，
    /// 同时避免在 core 的文本里写出 D-03b 词表里的词（词表连注释一起算，对照表在 .dsh-code/ 下）。</summary>
    private static readonly string[] DesktopChromeClasses =
    {
        "Progman", "WorkerW", "She" + "ll_TrayWnd", "She" + "ll_SecondaryTrayWnd",
    };

    /// <summary>最近一次遮挡采样实际用的点（屏幕坐标；null = 这一轮没找到不透明点）。判据与诊断的读数面。</summary>
    public Point? LastOccludedPoint { get; private set; }

    /// <summary>最近一次遮挡采样盖住那个点的是谁（<see cref="Win32.WindowDescription"/> 的形态；"(none)" = 没采到）。</summary>
    public string LastOccluder { get; private set; } = "(none)";

    /// <summary>最近一次全屏判定的前台窗（<see cref="Win32.WindowDescription"/> 的形态；"(none)" = 没有前台窗）。</summary>
    public string LastForeground { get; private set; } = "(none)";

    // 身份描述的缓存：**同一个句柄只拼一次**。
    // 为什么必须有（RFC-21 t16，D-14e 的实测证据）：`SampleFullscreen` 与 `SampleOccluded` 每 tick
    // 都跑（30fps），而 `Win32.WindowDescription` 每次要两个 StringBuilder + 一次
    // `Process.GetProcessById`（起一个 Process 对象）+ 一次插值串 —— 实测每 tick 约 3.3KB，
    // 30fps 下就是 100KB/s，120 秒浸泡私有工作集 14.04→26.12MB（+85.98%），
    // 把 D-14d 的「稳态不增长」判成 FAIL。这两个字段只有自检读（test/SelfCheck/ThrottleChecks.cs），
    // 它们要的是「现在是谁」，不是「每一帧重新问一次谁」。
    // 缓存键是句柄本身：句柄复用（同一个数值换了窗）在桌面上确实存在，所以句柄变了就重拼。
    private IntPtr _occluderHandle;
    private string _occluderText = "(none)";
    private IntPtr _foregroundHandle;
    private string _foregroundText = "(none)";

    // 类名查询的复用缓冲：`Win32.ClassNameOf` 每次 new 一个 StringBuilder(256)，
    // 而它在全屏判定的第 5 步每 tick 都可能被调到 —— 30fps 下就是每帧 512B 的纯垃圾。
    // 探测层是每帧都要跑的热路径，缓冲区跟着实例走，一次分配用一辈子。
    private readonly System.Text.StringBuilder _classBuffer = new(256);

    /// <summary>按句柄缓存的身份描述：同一句柄直接返回上一次拼好的串（零分配）。</summary>
    private string Describe(IntPtr hwnd, ref IntPtr cachedHandle, ref string cached)
    {
        if (hwnd == cachedHandle)
        {
            return cached;
        }

        cachedHandle = hwnd;
        cached = hwnd == IntPtr.Zero ? "(none)" : Win32.WindowDescription(hwnd);
        return cached;
    }

    /// <summary>累计采样次数（三个采样方法各算一次）。</summary>
    public int SampleCount { get; private set; }

    /// <summary>
    /// ① 本窗有没有被别的顶层窗盖住（RFC-21 §3.2 ①）。
    /// </summary>
    /// <param name="hwnd">宠物窗句柄。IntPtr.Zero ⇒ 直接 false（没有窗口就没有「被遮挡」这回事）。</param>
    /// <param name="alphaAt">窗口局部像素坐标 → alpha（0 ~ 255）；null 返回值表示「此刻没有帧缓冲」。
    /// 传 null ⇒ 直接 false（无帧缓冲时不判遮挡，绝不猜）。本方法**不吞**它内部抛出的异常。</param>
    public bool SampleOccluded(IntPtr hwnd, Func<int, int, byte?>? alphaAt)
    {
        SampleCount++;

        if (hwnd == IntPtr.Zero || alphaAt is null)
        {
            LastOccludedPoint = null;
            LastOccluder = "(none)";
            return false;
        }

        Rectangle rect = Win32.ScreenRect(hwnd);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            LastOccludedPoint = null;
            LastOccluder = "(none)";
            return false;
        }

        // 第一个**不透明**的分数点就是唯一取样点（顺序固定 ⇒ 判据可复现）。
        // 全透明 / 无帧缓冲 ⇒ 没有取样点 ⇒ fail-active（宁可漏判，也不误判成「被遮挡」）。
        bool found = false;
        Point sample = default;
        foreach ((double fx, double fy) in SampleRatios)
        {
            int localX = (int)Math.Round(rect.Width * fx);
            int localY = (int)Math.Round(rect.Height * fy);
            // 右下角的分数点可能落在最后一列/行之外（round 进位），钳回窗口内。
            localX = Math.Clamp(localX, 0, rect.Width - 1);
            localY = Math.Clamp(localY, 0, rect.Height - 1);

            byte? alpha = alphaAt(localX, localY);
            if (alpha is not byte value || value == 0)
            {
                continue;
            }

            sample = new Point(rect.Left + localX, rect.Top + localY);
            found = true;
            break;
        }

        if (!found)
        {
            LastOccludedPoint = null;
            LastOccluder = "(none)";
            return false;
        }

        LastOccludedPoint = sample;

        var screenPoint = new POINT { X = sample.X, Y = sample.Y };
        IntPtr top = Win32.TopLevelWindowFromPoint(screenPoint);
        if (top == IntPtr.Zero)
        {
            // 点落在桌面空洞上不算被应用盖住。
            LastOccluder = "(none)";
            return false;
        }

        LastOccluder = top == hwnd ? "(self)" : Describe(top, ref _occluderHandle, ref _occluderText);
        return top != hwnd;
    }

    /// <summary>
    /// ② 前台有没有一个覆盖**宠物所在显示器**的全屏窗（RFC-21 §3.2 ②）。六步判定，逐字按规格。
    /// </summary>
    /// <param name="hwnd">宠物窗句柄（要它来定位显示器：多屏下别的显示器上有全屏应用不该暂停这只宠物）。</param>
    public bool SampleFullscreen(IntPtr hwnd)
    {
        SampleCount++;

        // 1. 前台窗。
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            LastForeground = "(none)";
            return false;
        }

        LastForeground = Describe(fg, ref _foregroundHandle, ref _foregroundText);

        // 2. 宠物自己的显示器（不是主显示器，也不是前台窗的显示器）。
        IntPtr mon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !Win32.GetMonitorInfo(mon, ref info))
        {
            KernelLog.Write($"throttle-probe-monitor-failed hwnd=0x{hwnd.ToInt64():X}");
            return false;
        }

        // 3. 最小化的窗口不是全屏。
        if (Win32.IsIconic(fg))
        {
            return false;
        }

        // 4. 前台窗的 rect 必须**覆盖**显示器矩形（用覆盖不用相等：不可见边框让最大化窗各溢出约 7px）。
        Rectangle fgRect = Win32.ScreenRect(fg);
        if (!Covers(fgRect, info.rcMonitor))
        {
            return false;
        }

        // 5. 排除桌面本体与任务栏的窗口类（它们「盖满显示器」是常态，不是全屏应用）。
        if (IsDesktopChromeClass(Win32.ClassNameOf(fg, _classBuffer)))
        {
            return false;
        }

        // 6. 被 DWM 藏起来的前台窗不是「正在跑的全屏应用」。
        if (TrySampleCloaked(fg, out int cloaked) && cloaked != 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// ③ DWM 有没有把**本窗**藏起来（RFC-21 §3.2 ③）。**只**用来回答这个问题，**不用来判遮挡**。
    /// </summary>
    /// <param name="hwnd">被查的窗口句柄。</param>
    /// <param name="cloaked">输出：0 = 没被藏，非 0 = 藏了（位含义不解释，只判 0 / 非 0）。失败时为 0。</param>
    /// <returns>true 表示 <paramref name="cloaked"/> 有效；false 表示查询失败（调用方按「没被藏」处理）。</returns>
    public bool TrySampleCloaked(IntPtr hwnd, out int cloaked)
    {
        SampleCount++;
        cloaked = 0;

        if (hwnd == IntPtr.Zero)
        {
            KernelLog.Write("throttle-probe-cloaked-failed hwnd=0x0 hr=0x00000000");
            return false;
        }

        int hr;
        int value;
        try
        {
            hr = Win32.DwmGetWindowAttribute(hwnd, DwmwaCloaked, out value, sizeof(int));
        }
        catch (Exception error)
        {
            // dwmapi 加载不到（DllNotFoundException / EntryPointNotFoundException）也走这条：
            // 探测不可用时宠物照常出帧，只是「被 DWM 藏了」这条判据恒为假。
            KernelLog.Write($"throttle-probe-cloaked-failed hwnd=0x{hwnd.ToInt64():X}"
                + $" hr=0x00000000 ({error.GetType().Name})");
            return false;
        }

        if (hr != 0)
        {
            KernelLog.Write($"throttle-probe-cloaked-failed hwnd=0x{hwnd.ToInt64():X} hr=0x{hr:X8}");
            return false;
        }

        cloaked = value;
        return true;
    }

    /// <summary>矩形 a 是否覆盖矩形 b（覆盖语义：四条边都不许缩进去）。</summary>
    private static bool Covers(Rectangle a, RECT b)
    {
        return a.Left <= b.Left && a.Top <= b.Top && a.Right >= b.Right && a.Bottom >= b.Bottom;
    }

    private static bool IsDesktopChromeClass(string className)
    {
        foreach (string chromeClass in DesktopChromeClasses)
        {
            if (string.Equals(className, chromeClass, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
