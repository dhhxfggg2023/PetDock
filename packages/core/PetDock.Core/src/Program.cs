// 实现 docs/12 §6「可执行检查」的进程入口：DPI 感知设置、常驻运行与 --selftest 自检
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core;

internal static partial class Program
{
    private const int ExitOk = 0;
    private const int ExitFail = 1;
    private const int SettleMs = 300;
    private const int FrameLoopMs = 2000;

    [STAThread]
    private static int Main(string[] args)
    {
        // 开机第一件事，在任何窗口创建之前把进程钉成 Per-Monitor V2（docs/12 §3）。
        // 这是进程级设置，窗口一被创建就锁死，之后再设也无效——所以必须在 Main 最前面。
        if (!Win32.SetProcessDpiAwarenessContext(Win32.DpiPerMonitorAwareV2))
        {
            // 已被别的机制设过感知等级时这里会失败；感知不足的后果由自检的 dpi-awareness 项如实报告
            Console.Error.WriteLine($"kernel set-dpi-awareness-context-failed err={Marshal.GetLastWin32Error()}");
        }

        bool selfTest = args.Any(a => string.Equals(a, "--selftest", StringComparison.Ordinal));
        bool hostTest = args.Any(a => string.Equals(a, "--hosttest", StringComparison.Ordinal));

        // 等价于 ApplicationConfiguration.Initialize()：不调用生成版本，
        // 以免它内部的默认 DPI 模式覆盖上面刚设置的 Per-Monitor V2
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        return selfTest ? RunSelfTest() : hostTest ? RunHostTest(args) : RunResident();
    }

    private static int RunResident()
    {
        // 常驻也起插件宿主（docs/13 §3：内核宿主插件是常驻行为的一部分）：
        // 根目录探测失败 → 空根空转，不影响窗口；1Hz tick 对常驻 CPU 的影响可忽略。
        using PluginHost pluginHost = new(PluginHost.ProbePluginsRoot() ?? string.Empty);
        pluginHost.Start();

        // 内容装配走运行期接入口（FrameContentProvider）：内核不认识任何素材类型，
        // 上层插件实现 IFrameContentProvider 并提供像素；装配失败就如实退回内核纯色兜底。
        using var window = ComposeWindow("resident");
        Application.Run(window);
        return ExitOk;
    }

    /// <summary>插件宿主自检入口（--hosttest [expectedPluginId]）：
    /// 期望插件 id 由命令行传入 —— core 源码不写插件名特判（docs/01 P-02 第 4 条）。</summary>
    private static int RunHostTest(string[] args)
    {
        AttachConsole();
        KernelLog.ConsoleAttached = true;
        return PluginHostSelfTest.Run(args.Where(a => !string.Equals(a, "--hosttest", StringComparison.Ordinal)).ToArray());
    }

    /// <summary>
    /// 统一的内容装配：先问上层（FrameContentLoader，plugins 层实现 IFrameContentProvider），
    /// 拿不到就退回内核纯色兜底（SolidFrameContent）——窗口始终可见、始终可命中。
    /// </summary>
    private static LayeredFrameWindow ComposeWindow(string phase)
    {
        IFrameContentSource? content = FrameContentLoader.TryLoad(out string note);
        KernelLog.Write($"frame-content phase={phase} {note}");
        return content is null ? new LayeredFrameWindow() : new LayeredFrameWindow(content);
    }

    /// <summary>统一把自检里的布尔值写成 true / false，别处一律复用。</summary>
    private static string Flag(bool value) => value ? "true" : "false";

    private static int RunSelfTest()
    {
        AttachConsole();
        KernelLog.ConsoleAttached = true;

        // 构造窗口并计时：窗口创建 + 首次 UpdateLayeredWindow 都在这里面，
        // 这几个数字是自检能拿到的唯一「启动代价」证据。
        // 内容来源与常驻路径同一条接入口：上层插件提供了内容就用，没提供就退回内核纯色兜底。
        var createClock = Stopwatch.StartNew();
        using var window = ComposeWindow("selftest");
        createClock.Stop();
        window.Show();
        window.MoveToCenter();

        var checks = new List<Check>();
        string? failure = null;

        Application.DoEvents();
        Thread.Sleep(SettleMs);
        Application.DoEvents();

        // 1. DPI 感知等级（按窗口反查：进程感知等级在启动时固定，窗口继承创建线程的等级）
        bool perMonitorV2 = DpiContext.IsPerMonitorV2(window.WindowHandle);
        checks.Add(new Check("dpi-awareness", $"per-monitor-v2={(perMonitorV2 ? "true" : "false")}", perMonitorV2));
        checks.Add(new Check("dpi-window", DpiContext.DpiForWindow(window.WindowHandle).ToString(CultureInfo.InvariantCulture), true));
        checks.Add(new Check("window-create", $"{createClock.ElapsedMilliseconds}ms", true));
        checks.Add(new Check("window-create-split",
            $"dib={window.Startup.FrameMs}ms handle={window.Startup.HandleMs}ms",
            true));

        // 2. 扩展样式逐位判断
        long exStyle = Win32.GetWindowLongPtr(window.WindowHandle, Win32.GWL_EXSTYLE).ToInt64();
        bool layered = (exStyle & Win32.WS_EX_LAYERED) != 0;
        bool topmost = (exStyle & Win32.WS_EX_TOPMOST) != 0;
        bool toolWindow = (exStyle & Win32.WS_EX_TOOLWINDOW) != 0;
        bool noActivate = (exStyle & Win32.WS_EX_NOACTIVATE) != 0;
        bool transparent = (exStyle & Win32.WS_EX_TRANSPARENT) != 0;
        checks.Add(new Check("window-exstyle",
            $"layered={Flag(layered)} topmost={Flag(topmost)} toolwindow={Flag(toolWindow)} noactivate={Flag(noActivate)} transparent={Flag(transparent)}",
            layered && topmost && toolWindow && noActivate && !transparent));

        // 3/4. 命中穿透——必须先出首帧：分层窗在第一次 UpdateLayeredWindow 之前
        // 整块矩形都算命中，那时测透明角得到的失败是假象，不是穿透坏了。
        window.PresentFirstFrame();

        // 先把窗口挪到一个不压住任何非本进程窗口的位置再测。
        // 理由：命中探针问的是「这一点上、Z 序最靠上的窗口是谁」，宿主窗是 TOPMOST 但不吃焦点，
        // 任何同为 TOPMOST 的窗口（本机实测：铺满屏幕的 DSH GUI 窗、explorer 的任务切换窗）插在它上面时，
        // 探针拿到的就是那个窗口，报出来的 opaque=miss 是测量环境的产物，不是穿透坏了。
        // 用 exstyle 把真正占着位置的第三方顶层窗挑出来，只挪自己，不碰别人的窗。
        var coverers = new List<string>();
        Point? freeSpot = FindUncoveredSpot(window.WindowHandle, window.GetPhysicalSize(), coverers);
        if (freeSpot is Point spot)
        {
            window.SetWindowPosition(spot.X, spot.Y);
        }

        // 再把自己插回 topmost 层最前面：TOPMOST 是一整个层，层内的先后同样决定 WindowFromPoint 的答案，
        // 光是「有 TOPMOST 样式」并不保证在这一层里靠前。这一步只改 Z 序，不动位置、不抢焦点。
        window.BringToFrontOfTopmost();
        Application.DoEvents();
        Thread.Sleep(SettleMs);
        Application.DoEvents();

        HitReport hit;
        try
        {
            hit = ProbeUntilSelfOnTop(window);
        }
        catch (Exception error)
        {
            failure = $"hit-probe-error {error.Message}";
            hit = new HitReport(false, false, default, default, default, IntPtr.Zero, IntPtr.Zero, 0, 0, "(error)", "(error)", "(error)", "(error)");
        }

        // 屏幕上找不到空位（例如有人开着一块铺满全屏的顶层窗）时，「谁在命中点最上面」这个问题
        // 已经不是宿主窗的性质问题了。退一步：把自己最小化一瞬，让盖着它的窗露出来，照样量同一对点。
        // 但这必须在报告里标出来——发过最小化再取样的结果与常驻状态下的结果不是同一回事。
        string coverageNote = "normal";
        if (!hit.OpaqueHit || !hit.TransparentPass)
        {
            MinimizedMeasurement measurement = MeasureMinimized(window);
            if (measurement.Hit is not null)
            {
                hit = measurement.Hit;
                coverageNote = "minimized-flash: 命中点是把自己最小化后量的，屏幕已被第三方顶层窗占满";
            }
        }

        checks.Add(new Check("hit-coverers", coverers.Count == 0 ? "none" : string.Join("|", coverers), coverers.Count == 0));
        checks.Add(new Check("hit-coverage", coverageNote, true));
        checks.Add(new Check("hit", $"opaque={(hit.OpaqueHit ? "hit" : "miss")}", hit.OpaqueHit));
        checks.Add(new Check("hit", $"transparent={(hit.TransparentPass ? "pass" : "fail")}", hit.TransparentPass));
        checks.Add(new Check("hit-point",
            $"rect={hit.WindowRect.Left},{hit.WindowRect.Top},{hit.WindowRect.Right},{hit.WindowRect.Bottom} center={hit.Center.X},{hit.Center.Y} corner={hit.Corner.X},{hit.Corner.Y}",
            true));
        checks.Add(new Check("hit-who", $"center={hit.ClassAtCenter} corner={hit.ClassAtCorner}", true));
        // 取点必须按帧缓冲自己的尺寸算：写死 64,64 是「逻辑尺寸 128」时代的中心，
        // 帧缓冲变成 192x192 之后那就是个偏心的暗像素，读数会莫名其妙。
        Size dib = window.FrameSize;
        checks.Add(new Check("dib-sample",
            $"corner={Pixel(window.SampleFramePixel(2, 2))} center={Pixel(window.SampleFramePixel(dib.Width / 2, dib.Height / 2))}",
            true));
        checks.Add(new Check("pixel-on-screen", ScreenShowsPetPixel(window, hit), true));
        checks.Add(new Check("window-state", WindowStateReport(window.WindowHandle), true));
        checks.Add(new Check("z-order", ZOrderReport(window.WindowHandle), true));

        // 5. 窗口尺寸（逻辑像素）
        Size logical = window.LogicalSize;
        Size physical = window.GetPhysicalSize();
        Size actual = window.WindowSize;
        checks.Add(new Check("window-size", $"{logical.Width}x{logical.Height}", logical.Width == 128 && logical.Height == 128));
        // 断言窗口的实测矩形，而不是我们请求的尺寸：把请求值抄一遍永远为真，等于没测
        // （实测过一次请求 192x192、落地 192x128，而这条检查照样打勾）。DIB 也必须等于实测尺寸，
        // 否则 UpdateLayeredWindow 从源 DC 之外取图，整帧静默不可见。
        Size dibSize = window.FrameSize;
        checks.Add(new Check("window-size-physical",
            $"requested={physical.Width}x{physical.Height} actual={actual.Width}x{actual.Height} dib={dibSize.Width}x{dibSize.Height}",
            actual.Width == physical.Width && actual.Height == physical.Height
                && dibSize.Width == actual.Width && dibSize.Height == actual.Height));

        // 6. 帧循环（跑满 2 秒）
        TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        var pump = new FramePump();
        pump.Start(() => window.RenderFrame());
        int hitChecks = 0;
        int hitHits = 0;
        IntPtr lastOnTop = IntPtr.Zero;
        while (wall.ElapsedMilliseconds < FrameLoopMs)
        {
            Application.DoEvents();
            Thread.Sleep(10);

            // 每 10 帧问一次 OS「宿主窗中心点最上面是谁」。Z 序是动态的：我们要的是
            // 「常驻期间有没有真正占到命中点」，一次采样答不了这个问题，一段时间的命中率才答得了。
            if (++hitChecks % 10 == 0)
            {
                Rectangle own = Win32.ScreenRect(window.WindowHandle);
                POINT probe = new()
                {
                    X = own.Left + (own.Width / 2),
                    Y = own.Top + (own.Height / 2),
                };
                lastOnTop = Win32.TopLevelWindowFromPoint(probe);
                if (lastOnTop == window.WindowHandle)
                {
                    hitHits++;
                }
            }
        }

        pump.Stop();

        int pumpFrames = pump.Frames;
        double pumpMax = pump.MaxFrameMs;
        double pumpAvg = pump.AvgFrameMs;
        long pumpOver = pump.OverBudgetFrames;
        pump.Dispose();

        TimeSpan cpuAfter = Process.GetCurrentProcess().TotalProcessorTime;
        double cpuPercent = cpuAfter.TotalMilliseconds - cpuBefore.TotalMilliseconds;
        cpuPercent = cpuPercent / wall.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100.0;

        checks.Add(new Check("frame-loop",
            $"frames={pumpFrames} maxms={Fmt(pumpMax)} avgms={Fmt(pumpAvg)} overbudget={pumpOver}",
            pumpFrames >= 50 && pumpAvg <= 40));
        checks.Add(new Check("cpu-percent", Fmt(cpuPercent), cpuPercent < 1.0));
        checks.Add(new Check("hit-rate",
            $"samples={hitChecks / 10} hits={hitHits} last-on-top={Win32.WindowDescription(lastOnTop)}",
            true));

        // 7. 帧内容与帧缓冲状态（这一帧的像素来自上层内容源还是内核纯色兜底，如实报告）。
        //    判据含 HasContentSource（t6 加严，2026-10-16）：只有 window.HasFrame 时，
        //    「内容源没装上、退回纯色兜底」这类静默回归仍会打印 SELF-OK 并 exit 0，
        //    肉眼才看得出来 —— 而自检的全部意义就是让这类回归在命令行上失败。
        //    见 .dsh-code/mistakes.md 2026-10-16 两条（跨程序集重声明 / 自检不参与退出码）。
        checks.Add(new Check("frame-content",
            $"source={Flag(window.HasContentSource)} frame={Flag(window.HasFrame)}",
            window.HasFrame && window.HasContentSource));

        foreach (Check check in checks)
        {
            Console.Out.WriteLine($"{check.Key} {check.Value}");
        }

        Console.Out.Flush();

        List<Check> failed = checks.Where(c => !c.Expected).ToList();
        if (failure is not null)
        {
            Console.Out.WriteLine($"SELF-FAIL {failure}");
            Console.Out.Flush();
            return ExitFail;
        }

        if (failed.Count == 0)
        {
            Console.Out.WriteLine("SELF-OK");
            Console.Out.Flush();
            return ExitOk;
        }

        foreach (Check check in failed)
        {
            Console.Out.WriteLine($"SELF-FAIL {check.Key} {check.Value}");
        }

        Console.Out.Flush();
        return ExitFail;
    }

}
