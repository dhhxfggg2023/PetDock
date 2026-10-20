// 实现 docs/12 §6「可执行检查」的进程入口：DPI 感知设置、常驻运行与 --selftest 自检
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;
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
        bool animTest = args.Any(a => string.Equals(a, "--animtest", StringComparison.Ordinal));
        bool driveTest = args.Any(a => string.Equals(a, "--drivetest", StringComparison.Ordinal));
        bool eventTest = args.Any(a => string.Equals(a, "--eventtest", StringComparison.Ordinal));
        bool perfTest = args.Any(a => string.Equals(a, "--perftest", StringComparison.Ordinal));
        bool uiTest = args.Any(a => string.Equals(a, "--uitest", StringComparison.Ordinal));
        int goldenIdx = Array.FindIndex(args, a => string.Equals(a, "--golden-baseline", StringComparison.Ordinal));
        bool goldenTest = goldenIdx >= 0;

        // 等价于 ApplicationConfiguration.Initialize()：不调用生成版本，
        // 以免它内部的默认 DPI 模式覆盖上面刚设置的 Per-Monitor V2
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        return selfTest ? RunSelfTest()
            : hostTest ? RunHostTest(args)
            : animTest ? RunAnimTest(args)
            : driveTest ? RunDriveTest(args)
            : eventTest ? RunEventTest(args)
            : perfTest ? RunPerfTest(args)
            : uiTest ? RunUiTest(args)
            : goldenTest ? RunGoldenBaseline(args, goldenIdx)
            : RunResident();
    }

    /// <summary>常驻：内容链与 --selftest 完全相同（唯一装配入口），但消息循环的宿主是 UiHost
    /// —— 宠物窗口只是众多窗口之一，用窗体当 Application.Run 的宿主会让「关主控面板=退程序」，
    /// docs/18 §2.5 明确禁止。托盘与面板的生命周期由此统一管理。</summary>
    private static int RunResident()
    {
        // 常驻也起插件宿主（docs/13 §3：内核宿主插件是常驻行为的一部分）：
        // 根目录探测失败 → 空根空转，不影响窗口；1Hz tick 对常驻 CPU 的影响可忽略。
        // 授权端口先建：装载期要弹窗，而弹窗只能开在 UI 线程上（RFC-13 §2.4）。
        var port = new UiConsentPort();
        using PluginHost pluginHost = new(
            PluginHost.ProbePluginsRoot() ?? string.Empty,
            PluginHost.ResidentTickMs,
            autoLoad: true,
            consent: port);

        // 内容装配走**唯一**的发现路径（PluginHost 名册，t1 契约 §3.2）：
        // 名册里有视图实现（docs/11 §3）就用它出帧；否则退回 M0 的内容源工厂；
        // 再没有就退回内核纯色兜底 —— 常驻形态下兜底是合法的（窗口仍可见可命中）。
        using var window = ComposeWindow("resident", pluginHost);
        ComposeOutcome? composed = Composition;
        // 帧源要交给 UiHost：键盘钩子的事件必须走 PetFrameSource.PublishKeyStroke 才能进总线
        // （docs/16 §3 的 input.key.* 是 capability only，没有第二条合法生产路径）。
        //
        // 宿主**不在这里启动**：装载期的授权弹窗需要一个活着的消息泵，而消息泵由
        // Application.Run 起来 —— 装载因此推迟到 UiHost 的第一个滴答（RFC-13 §2.4）。
        var host = new UiHost(window, composed?.Instance, pluginHost, composed?.Content as PetFrameSource, port);
        using (host)
        {
            Application.Run(host);
        }

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

    /// <summary>动画循环自检入口（--animtest clipA clipB ...）：判据与命令形态见
    /// docs/handoff/M2-contract-frozen.md §3.3（C1 clip-frames / C2 同 now 同帧 / C3 周期绕回）。</summary>
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

    /// <summary>功耗门禁入口（--perftest，docs/41 D-14）：判据行 `CHECK perf-<name> PASS|FAIL`，
    /// 末行 PERF-OK / PERF-FAIL。采样逻辑在 test/SelfCheck/PerfGate.cs（不计入 D-03a 预算）。</summary>
    private static int RunPerfTest(string[] args)
    {
        AttachConsole();
        KernelLog.ConsoleAttached = true;
        return PerfGate.Run(args.Where(a => !string.Equals(a, "--perftest", StringComparison.Ordinal)).ToArray());
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
    /// 供 tests/PetDock.Golden.Tests 逐像素比对。走与 --animtest 完全相同的链，不另造渲染入口。</summary>
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

    /// <summary>
    /// 内容装配的唯一入口：见 FrameComposition。装配结果连同实例一起返回，
    /// 自检与常驻共用同一条链（两条链正是 M0 source=false 静默 SELF-OK 的成因）。
    /// </summary>
    private static LayeredFrameWindow ComposeWindow(string phase, PluginHost? host = null)
    {
        ComposeOutcome outcome = FrameComposition.Compose(phase, attachInstance: true);
        if (outcome.Instance is not null && host is not null)
        {
            outcome.Instance.SuspendPort = FrameComposition.SuspendPortFor(host);
        }

        Composition = outcome;
        KernelLog.Write($"frame-content phase={phase} {outcome.Describe()}");
        LayeredFrameWindow window = outcome.Content is null
            ? new LayeredFrameWindow()
            : new LayeredFrameWindow(outcome.Content);

        // 窗口句柄回填（M4-VERIFY-r2.md 的 F 号缺口）：窗口要到这一步才存在，而帧源在
        // FrameComposition 里就先建好了。不回填的话 WorldProbe 永远拿 hwnd==0 采样 ——
        // 鼠标取屏幕绝对坐标、WindowRect 恒 0、IsOccluded 恒 false，三类世界输入里两类的值都是假的。
        if (outcome.Content is PetFrameSource source)
        {
            source.WindowHandle = window.WindowHandle;
            // 窗口事件（core.window.focus / moved / dpiChanged）的生产点在这里接上总线：
            // M4 修复前 LayeredFrameWindow.WindowEvent 全仓零订阅者，这三条事件发了无人收。
            window.WindowEvent += (type, payload) => source.PublishWindowEvent(type, payload);
            KernelLog.Write($"frame-source-bound window=0x{window.WindowHandle.ToInt64():X}");
        }

        return window;
    }

    /// <summary>最近一次内容装配的结果（自检判据读它）。</summary>
    private static ComposeOutcome? Composition { get; set; }

    /// <summary>统一把自检里的布尔值写成 true / false，别处一律复用。</summary>
    private static string Flag(bool value) => value ? "true" : "false";

}
