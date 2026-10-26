// 实现 docs/12 §6「可执行检查」的进程入口：DPI 感知设置、常驻运行与 --selftest 自检
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
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
        // 自检的 tier-2 客体（test/SelfCheck/Tier2StateChecks.Guest.cs）：不建窗口，故在 DPI 之前分流。
        int guestAt = Array.IndexOf(args, Tier2Guest.Flag);
        if (guestAt >= 0)
        {
            return Tier2Guest.Run(guestAt + 1 < args.Length ? args[guestAt + 1] : string.Empty);
        }

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
        bool residentTest = args.Any(a => string.Equals(a, "--residenttest", StringComparison.Ordinal));
        bool composeTest = args.Any(a => string.Equals(a, "--composetest", StringComparison.Ordinal));
        int goldenIdx = Array.FindIndex(args, a => string.Equals(a, "--golden-baseline", StringComparison.Ordinal));
        bool goldenTest = goldenIdx >= 0;

        // 自检形态也装上插件日志收口（RFC-23）：没有审计流水（trail 传 null），
        // 但「插件 → SDK 转发的门面 → 内核静态门面 → 收口」这条链因此每次门禁都真的被走一遍，
        // 而不是只靠 --drivetest 里的桩件。常驻形态的接法见 RunResident（那里有流水）。
        if (selfTest || hostTest || animTest || driveTest || eventTest || perfTest || uiTest || composeTest || goldenTest)
        {
            PluginLog.Sink = new HostPluginLog(trail: null);
        }

        // 等价于 ApplicationConfiguration.Initialize()：不调用生成版本，
        // 以免它内部的默认 DPI 模式覆盖上面刚设置的 Per-Monitor V2
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 静止档的确定性探针（先例 tests/m7 的 --timing-probe）：只跑那一层。放在单实例之前：
        // 它要真窗口，也不该抢用户形态的锁。
        int throttleProbeAt = Array.IndexOf(args, "--throttle-probe");
        if (throttleProbeAt >= 0)
        {
            AttachConsole();
            return ThrottleChecks.Probe(throttleProbeAt + 1 < args.Length ? args[(throttleProbeAt + 1)..] : Array.Empty<string>());
        }

        // 单实例（RFC-32 §3）：只在用户形态抢锁。诊断旗标（--uitest/--selftest/--hosttest/…）
        // 不抢，否则一条卡住的常驻实例会让下一条判据命令静默变成「已有实例，退出 0」，
        // 门禁就从此测不到东西了 —— 那不是约束，那是给门禁造假。
        bool userMode = !(selfTest || hostTest || animTest || driveTest || eventTest || perfTest
            || uiTest || composeTest || goldenTest || residentTest);
        if (userMode)
        {
            using var instance = new SingleInstance();
            if (!instance.IsFirst)
            {
                KernelLog.Write($"single-instance-refused name={instance.Name} — 已有实例在跑，本次不再打开第二只");
                return 0;
            }

            return RunResident(null);
        }

        return selfTest ? RunSelfTest()
            : hostTest ? RunHostTest(args)
            : animTest ? RunAnimTest(args)
            : driveTest ? RunDriveTest(args)
            : eventTest ? RunEventTest(args)
            : perfTest ? RunPerfTest(args)
            : uiTest ? RunUiTest(args)
            : composeTest ? RunComposeTest(args)
            : goldenTest ? RunGoldenBaseline(args, goldenIdx)
            : RunResident(ResidentPerfProbe.Parse(args));
    }

    /// <summary>常驻：内容链与 --selftest 完全相同（唯一装配入口），但消息循环的宿主是 UiHost
    /// —— 宠物窗口只是众多窗口之一，用窗体当 Application.Run 的宿主会让「关主控面板=退程序」，
    /// docs/18 §2.5 明确禁止。托盘与面板的生命周期由此统一管理。</summary>
    /// <param name="probe">--residenttest 的探针参数（null = 正常常驻，见 ResidentPerfProbe）。</param>
    private static int RunResident(ResidentPerfProbe.Options? probe = null)
    {
        // 探针模式要往控制台打判据行（WinExe 默认没有控制台）：只接控制台，**不**打开
        // KernelLog.ConsoleAttached —— 那个开关会让每一条内核日志都同步写到控制台，
        // 实测同一段常驻跑 3.03% 单核（真实常驻走的是文件里的 ResidentLog，见 RFC-26 §3 差异③）。
        // 门禁绝不能给它测的东西加成本：加了就等于在测「带调试输出的那个进程」。
        if (probe is not null)
        {
            AttachConsole();
        }

        // 常驻也起插件宿主（docs/13 §3：内核宿主插件是常驻行为的一部分）：
        // 根目录探测失败 → 空根空转，不影响窗口；1Hz tick 对常驻 CPU 的影响可忽略。
        // 授权端口先建：装载期要弹窗，而弹窗只能开在 UI 线程上（RFC-13 §2.4）。
        var port = new UiConsentPort();
        // 探针模式换掉插件侧的授权端口（RFC-26 §3 差异①）：弹窗要人点，而门禁里没有人。
        // UiHost 的 consented 端口仍是真端口 —— 面板那侧的行为不变，探针不打开面板。
        ICapabilityConsentPort pluginPort = probe is null
            ? port
            : probe.DenyAll
                ? new RecordingConsentPort(_ => ConsentAnswer.Reject)
                : new RecordingConsentPort(r => new ConsentAnswer(ConsentScope.Session, r.AskableIds));
        // 用户关掉的插件（2026-10-26 用户要求「插件可以选择关闭，重启生效」）：
        // 必须在装载之前读 —— 「重启生效」的事实源就是这份设置。
        PetDataStore dataStore = PetDataStore.Resolve();
        UserSettings startupSettings = new UserSettingsStore(dataStore.Root).Load();
        if (startupSettings.DisabledPlugins is { Count: > 0 } disabledIds)
        {
            KernelLog.Write($"startup-plugins-disabled count={disabledIds.Count}");
        }

        using PluginHost pluginHost = new(
            PluginHost.ProbePluginsRoot() ?? string.Empty,
            PluginHost.ResidentTickMs,
            autoLoad: true,
            consent: pluginPort,
            disabledPlugins: startupSettings.DisabledPlugins);

        // 跨进程授权记忆（RFC-19 §2.3 第 1 点）：把上次进程记住的授予灌进会话记忆，
        // 于是「允许（记住这个决定）」过的插件这次启动不再被问。
        // 必须在 Start() 之前接：装载发生在 Start() 之后，那时再播种就晚了。
        pluginHost.AttachConsentStore(new ConsentStore(PetDataStore.Resolve().Root));

        // 跨进程审计流水（RFC-20 §3.3 第 2 点）：同样必须在 Start() 之前接——
        // 装载期就会发 grant 事件，那时流水还没接上，那批授权证据就永远丢了
        // （与 RFC-19 同一纪律：一个道理）。
        var auditTrail = new AuditTrail(
            Path.Combine(dataStore.Root, AuditTrail.AuditFolder),
            RedactRules.ForCurrentMachine(dataStore.Root, PluginHost.ProbePluginsRoot()));
        auditTrail.Start();
        pluginHost.AttachAuditTrail(auditTrail);
        // 内容装配走**唯一**的发现路径（PluginHost 名册，t1 契约 §3.2）：
        // 名册里有视图实现（docs/11 §3）就用它出帧；否则退回 M0 的内容源工厂；
        // 再没有就退回内核纯色兜底 —— 常驻形态下兜底是合法的（窗口仍可见可命中）。
        //
        // 窗口持有的是**可替换内容源代理**（RFC-15 §3.3）：换宠物时换的是代理里面那一份，
        // 合成器层一行不改。代理在这里创建，导入路径通过它换源。
        using var window = ComposeWindow("resident", pluginHost);
        ComposeOutcome? composed = Composition;
        var frames = composed?.Content is null
            ? null
            : new SwappableFrameSource(composed.Content, composed.Roster);
        if (frames is not null)
        {
            frames.WindowHandle = window.WindowHandle;
            // 窗口事件（core.window.focus / moved / dpiChanged）的生产点接上总线：
            // M4 修复前 LayeredFrameWindow.WindowEvent 全仓零订阅者，这三条事件发了无人收。
            window.WindowEvent += (type, payload) => frames.PublishWindowEvent(type, payload);
        }

        // 恢复上次导入的宠物（RFC-15 §3.4 的清单里记着「当前选中哪一只」）。
        // 清单里没有、或素材目录不见了、或这个包加载不了 ⇒ 保留默认那只，不报错、不空桌。
        PetInstance? pet = composed?.Instance;
        if (frames?.Roster is not null)
        {
            PetInstance? restored = PetImportService.RestoreFromCatalog(PetDataStore.Resolve(), frames.Roster);
            if (restored is not null)
            {
                frames.Replace(new PetFrameSource(restored));
                PetImportService.ReleaseInstance(pet, restored);
                pet = restored;
            }
        }

        // 帧源要交给 UiHost：键盘钩子的事件必须走帧源才能进总线
        // （docs/16 §3 的 input.key.* 是 capability only，没有第二条合法生产路径）。
        //
        // 宿主**不在这里启动**：装载期的授权弹窗需要一个活着的消息泵，而消息泵由
        // Application.Run 起来 —— 装载因此推迟到 UiHost 的第一个滴答（RFC-13 §2.4）。
        //
        // 传的是 pet（可能被恢复逻辑换过），不是 composed?.Instance —— 传错就等于
        // 「恢复了清单里的宠物，但桌面上还是原来那只」。
        var settingsStore = new UserSettingsStore(PetDataStore.Resolve().Root);

        // 常驻形态的运行期日志落盘 + 诊断服务（RFC-17 §3.1 / §3.4）。
        // 装配顺序有讲究：落盘层挂到 KernelLog.Sink 之后，这一行往后每一个
        // KernelLog.Write 都会留痕；日志目录不存在就建一个空的（空目录也是成功）。
        var residentLog = new ResidentLog(
            Path.Combine(dataStore.Root, DiagnosticService.LogsFolder),
            RedactRules.ForCurrentMachine(dataStore.Root, PluginHost.ProbePluginsRoot()));
        residentLog.Start();
        KernelLog.Sink = residentLog;
        KernelLog.Write("resident-log-started");

        // 插件日志面（RFC-23 / docs/20 §3）：装上收口，于是插件写 PluginLog.* 有地方去。
        // 必须在窗口装好内容之后、任何插件代码跑起来之前装（init 阶段就打日志的插件要留得下痕）。
        var pluginLog = new HostPluginLog(auditTrail);
        PluginLog.Sink = pluginLog;

        var diagnostics = new DiagnosticService(dataStore, pluginHost, pet, residentLog);
        diagnostics.PluginLog = pluginLog;

        // 诊断包的审计一节读跨进程流水（RFC-20 §3.4）。流水不可用时诊断包自己回退
        // 到环形缓冲，并在摘要里写明「不是全量」。
        diagnostics.Trail = auditTrail;

        var host = new UiHost(window, pet, pluginHost, frames, port, settingsStore, diagnostics);

        // 帧循环是 UiHost 建的，诊断服务比它先存在 —— 帧读数只能事后回填（RFC-17 §3.2）。
        diagnostics.FrameLoop = host.Loop;

        // RFC-37 §3.2（片 1b）：清单里其余几只也上桌。主窗仍是上面那只（归 UiHost 管可见性与位置），
        // 其余每一只各一套窗口 + 帧源 + 帧循环，装进 PetWindowSet，在首滴答随主窗一起摆出来。
        // 摆位取主窗的**实时**位置，所以必须等 UiHost 建好（位置在首滴答才恢复）。
        PetWindowSet petSet = PetWindowSet.FromCatalog(
            frames?.Roster,
            pet,
            dataStore.ReadActive()?.InstanceId,
            dataStore,
            window,
            host.Loop,
            settingsStore);
        host.AttachPetSet(petSet);

        // 探针要在 Application.Run 之前挂上：它靠 UI 定时器判「采样完了没有」，
        // 泵一停它自己也会停（那时靠采样线程的墙钟，所以另有看门狗判红，见 ResidentPerfProbe）。
        if (probe is not null)
        {
            ResidentPerfProbe.Start(host, probe);
        }

        using (host)
        {
            Application.Run(host);
        }

        // 进程正常退出：落盘剩余的行、关掉活动文件、去掉 .part 后缀
        // （写完才算硬证据，docs/17 §3）。
        KernelLog.Sink = null;
        residentLog.Complete();
        residentLog.Dispose();

        // 审计流水同样收尾：落盘剩余的行、去掉 .part（RFC-20 §3.3 第 4 点）。
        auditTrail.Complete();
        auditTrail.Dispose();

        // 探针模式的退出码由判据决定（RESIDENT-PERF-OK / FAIL）；正常常驻仍是 0。
        return probe is null || !ResidentPerfProbe.Failed ? ExitOk : ExitFail;
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
        ComposeOutcome outcome = FrameComposition.Compose(phase, attachInstance: true, host);
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

        // RFC-35 §3.2：窗口鼠标消息 → 实例的交互会话（docs/11 §5）。接线只在 FrameComposition
        // 一处（自检也调它），免得「生产接上了、判据测的是另一份接线」。
        if (outcome.Instance is PetInstance pet)
        {
            FrameComposition.WirePointer(window, pet);
            KernelLog.Write($"pointer-wiring instance={pet.Id} surfaces={pet.SurfacesByPriority.Count}");
        }

        return window;
    }

    /// <summary>最近一次内容装配的结果（自检判据读它）。</summary>
    private static ComposeOutcome? Composition { get; set; }

    /// <summary>统一把自检里的布尔值写成 true / false，别处一律复用。</summary>
    private static string Flag(bool value) => value ? "true" : "false";

}
