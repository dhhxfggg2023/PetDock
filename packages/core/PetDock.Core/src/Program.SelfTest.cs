// RunSelfTest 从 Program.cs 拆出（D-03c：单文件 400 行上限）。逻辑一字未改，
// 只是把自检主体放进独立的 partial 文件，主文件留给入口分派。
using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core;

internal static partial class Program
{
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
        //    判据三层加严（M0 教训 → t6 → t1 契约 §4.3）：
        //      (a) HasFrame：帧缓冲真的就绪；
        //      (b) 内容源**具体且非兜底**：source= 后面是一个真类型全名，不是 SolidFrameContent；
        //      (c) 内容源**真的在出帧**（IFrameSourceStats.FramesServed > 0）——
        //          只判前两条时，「装上了但一帧都取不出来」仍会静默通过。
        //    这三条任一不成立即 SELF-FAIL + exit 1：M0 曾出现 source=false 却打 SELF-OK 并 exit 0。
        //    见 .dsh-code/mistakes.md（2026-10-16 两条）与 M2 契约 §4.3 的负向用例要求。
        ComposeOutcome? composition = Composition;
        string sourceName = composition?.SourceType ?? string.Empty;
        long servedFrames = window.ContentFramesServed;
        bool sourceConcrete = sourceName.Length > 0
            && !string.Equals(sourceName, FrameComposition.FallbackTypeName, StringComparison.Ordinal)
            && !string.Equals(sourceName, nameof(SolidFrameContent), StringComparison.Ordinal);
        checks.Add(new Check("frame-content",
            $"source={(sourceName.Length == 0 ? "false" : sourceName)}"
            + $" frame={Flag(window.HasFrame)} served={servedFrames}"
            + $" note={composition?.Note ?? "uncomposed"}",
            window.HasFrame && window.HasContentSource && sourceConcrete && servedFrames > 0));
        checks.Add(new Check("frame-source-kind",
            composition?.Instance is null ? "content-provider" : "abi-view",
            true));

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

    /// <summary>插件宿主自检入口（--hosttest [expectedPluginId]）：
    /// 期望插件 id 由命令行传入 —— core 源码不写插件名特判（原规格 01 P-02 第 4 条）。</summary>
    private static int RunHostTest(string[] args)
    {
        AttachConsole();
        KernelLog.ConsoleAttached = true;
        return PluginHostSelfTest.Run(args.Where(a => !string.Equals(a, "--hosttest", StringComparison.Ordinal)).ToArray());
    }

    /// <summary>动画循环自检入口（--animtest clipA clipB ...）：判据与命令形态见
    /// 交接档案 §3.3（C1 clip-frames / C2 同 now 同帧 / C3 周期绕回）。</summary>
    private static int RunAnimTest(string[] args)
    {
        AttachConsole();
        KernelLog.ConsoleAttached = true;
        return PetAnimTest.Run(args.Where(a => !string.Equals(a, "--animtest", StringComparison.Ordinal)).ToArray());
    }

    /// <summary>宿主帧驱动自检入口（--drivetest）：生命周期 §10、降级链 §9、帧预算 §7 的判据面。</summary>
    private static int RunDriveTest(string[] args)
    {
        AttachConsole();
        KernelLog.ConsoleAttached = true;
        return PetDriveTest.Run(args.Where(a => !string.Equals(a, "--drivetest", StringComparison.Ordinal)).ToArray());
    }

    /// <summary>事件总线自检入口（--eventtest，M4 契约 §7.5）：判据行 `CHECK <key> PASS|FAIL <detail>`，
    /// 末行 EVENT-OK / EVENT-FAIL n=<k>，退出码 0/n；SKIP 行不算 PASS（契约 §7.5）。</summary>
    private static int RunEventTest(string[] args)
    {
        AttachConsole();
        KernelLog.ConsoleAttached = true;
        return EventSelfTest.Run(args.Where(a => !string.Equals(a, "--eventtest", StringComparison.Ordinal)).ToArray());
    }

    /// <summary>功耗门禁入口（--perftest，原规格 41 D-14）：判据行 `CHECK perf-<name> PASS|FAIL`，
    /// 末行 PERF-OK / PERF-FAIL。采样逻辑在 test/SelfCheck/PerfGate.cs（不计入 D-03a 预算）。</summary>
    private static int RunPerfTest(string[] args)
    {
        AttachConsole();
        KernelLog.ConsoleAttached = true;
        return PerfGate.Run(args.Where(a => !string.Equals(a, "--perftest", StringComparison.Ordinal)).ToArray());
    }

    /// <summary>装配顺序探针入口（--composetest [pluginsRoot]，RFC-29 §3.6）：
    /// 打 `COMPOSE …` 与 `COMPOSE-OK|FAIL`，退出码 0/1。判据体在 Host/ComposeProbe.cs。</summary>
    private static int RunComposeTest(string[] args)
    {
        AttachConsole();
        KernelLog.ConsoleAttached = true;
        return ComposeProbe.Run(args.Where(a => !string.Equals(a, "--composetest", StringComparison.Ordinal)).ToArray());
    }

    /// <summary>UI 母体自检入口（--uitest，RFC-12 §3）：判据行 `CHECK ui-<name> PASS|FAIL`，
    /// 末行 UI-OK / UI-FAIL n=&lt;k&gt;。判据体在 test/SelfCheck/UiSelfTest.cs（不计入 D-03a 预算）。</summary>
    private static int RunUiTest(string[] args)
    {
        AttachConsole();
        KernelLog.ConsoleAttached = true;
        return UiSelfTest.Run(args.Where(a => !string.Equals(a, "--uitest", StringComparison.Ordinal)).ToArray());
    }

    /// <summary>金图基准生成入口（--golden-baseline outdir）：渲染示例宠物三 clip 首帧到基准 PNG，
    private static int RunGoldenBaseline(string[] args, int flagIdx)
    {
        AttachConsole();
        KernelLog.ConsoleAttached = true;
        string outdir = args.Length > flagIdx + 1 ? args[flagIdx + 1] : string.Empty;
        if (string.IsNullOrWhiteSpace(outdir))
        {
            Console.Error.WriteLine("GOLDEN-FAIL usage: --golden-baseline <outdir>");
            return ExitFail;
        }

        return GoldenBaselineGen.Run(outdir);
    }

}
