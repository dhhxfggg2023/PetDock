// 实现 原规格 12 §6「可执行检查」的进程入口：DPI 感知设置、常驻运行与 --selftest 自检
using System.Collections.Generic;
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

        // 开机第一件事，在任何窗口创建之前把进程钉成 Per-Monitor V2（原规格 12 §3）。
        // 这是进程级设置，窗口一被创建就锁死，之后再设也无效——所以必须在 Main 最前面。
        if (!Win32.SetProcessDpiAwarenessContext(Win32.DpiPerMonitorAwareV2))
        {
            // 已被别的机制设过感知等级时这里会失败；感知不足的后果由自检的 dpi-awareness 项如实报告
            Console.Error.WriteLine($"kernel set-dpi-awareness-context-failed err={Marshal.GetLastWin32Error()}");
        }

        // 重启时新进程带的两个旗标（2026-10-31）：--wait-pid 先等老进程退出再启动
        // （否则单实例锁让新进程立刻自杀，用户看到的是「点了重启什么也没发生」）；
        // --open-panel 启动完把面板打开（用户点的是托盘菜单，不该被丢回看不见的托盘）。
        // 两个都只在**用户形态**下有意义，诊断旗标不走这条路径。
        int waitPidAt = Array.IndexOf(args, "--wait-pid");
        int waitPid = -1;
        if (waitPidAt >= 0 && waitPidAt + 1 < args.Length)
        {
            _ = int.TryParse(args[waitPidAt + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out waitPid);
        }

        bool openPanelOnStart = args.Any(a => string.Equals(a, "--open-panel", StringComparison.Ordinal));

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

        // 清单静态校验（2026-10-28）：门禁与 petdock CLI 共用**内核那份** ManifestValidator，
        // 不在 tools/ 里再维持第二份 schema（原规格 21 删除后，第二份已经没有权威可依）。
        int validateAt = Array.IndexOf(args, "--validate-plugins");
        if (validateAt >= 0)
        {
            AttachConsole();
            return RunManifestValidate(args[(validateAt + 1)..]);
        }

        // 静止档的确定性探针（先例：早先的 --timing-probe）：只跑那一层。放在单实例之前：
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
        if (userMode && waitPid > 0)
        {
            // 重启路径（见 UiHost.Restart.cs）：等上一个实例真的退出再抢锁。
            // 超时就照常往下走 —— 抢不到锁会由下面那一条如实告诉用户「已经在运行」，
            // 比在这里无限等下去好（那时用户看到的是一个永远不出现的窗口）。
            WaitForPredecessor(waitPid);
        }

        if (userMode)
        {
            using var instance = new SingleInstance();
            if (!instance.IsFirst)
            {
                KernelLog.Write($"single-instance-refused name={instance.Name} — 已有实例在跑，本次不再打开第二只");
                ShowAlreadyRunningNotice();
                return 0;
            }

            // 双轨期（R1–R7）：--ui next 走新界面层，不带这个旗标一律走旧层。
            // 两条路都在 Main 里分叉、都抢同一把单实例锁 —— 于是「新层跑着的同时旧层也能起」
            // 这种最容易被忽略的双开形态在这里就被挡住了。
            return WantsNewUi(args) ? RunNewUi() : RunResident(null, openPanelOnStart);
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

    /// <summary>
    /// 第二个实例只做一件事：告诉用户「不是没反应，是它已经在托盘里」。
    /// 文案表按用户设置加载（这条路径不经过 UiHost）；读不到设置就退回默认语言。
    /// </summary>
    private static void ShowAlreadyRunningNotice()
    {
        try
        {
            UserSettings settings = new UserSettingsStore(PetDataStore.Resolve().Root).Load();
            I18n.Load(settings.Language);
        }
        catch (Exception)
        {
            // 读设置失败也照样要说话：I18n 会退回默认语言。
        }

        MessageBox.Show(
            I18n.Get("ui.singleInstance.running"),
            I18n.Get("ui.singleInstance.title"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    /// <summary>常驻：内容链与 --selftest 完全相同（唯一装配入口），但消息循环的宿主是 UiHost
    /// —— 宠物窗口只是众多窗口之一，用窗体当 Application.Run 的宿主会让「关主控面板=退程序」，
    /// 原规格 18 §2.5 明确禁止。托盘与面板的生命周期由此统一管理。</summary>
    /// <param name="probe">--residenttest 的探针参数（null = 正常常驻，见 ResidentPerfProbe）。</param>
    private static int RunResident(ResidentPerfProbe.Options? probe = null, bool openPanelOnStart = false)
    {
        // 探针模式要往控制台打判据行（WinExe 默认没有控制台）：只接控制台，**不**打开
        // KernelLog.ConsoleAttached —— 那个开关会让每一条内核日志都同步写到控制台，
        // 实测同一段常驻跑 3.03% 单核（真实常驻走的是文件里的 ResidentLog，见 RFC-26 §3 差异③）。
        // 门禁绝不能给它测的东西加成本：加了就等于在测「带调试输出的那个进程」。
        if (probe is not null)
        {
            AttachConsole();
        }

        // 常驻也起插件宿主（原规格 13 §3：内核宿主插件是常驻行为的一部分）：
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

        // 首次运行：把自带的会动示例宠物装进清单。装进去之后它和导入的宠物同一条路：
        // 有 id、能多开、能删除；不再是「每次启动都重建、删不掉」的内置窗口。
        I18n.Load(startupSettings.Language);

        ResidentLog residentLog = StartResidentLog(dataStore, probe, out string? updateReport, out bool updateHandedOff);
        if (updateHandedOff)
        {
            // 交接已经完成，搬运工在等本进程退出：这一次启动到此为止（不建任何窗口）。
            return ExitOk;
        }

        // 旧清单条目没有 name：从素材包清单补一次（只补空值）。必须在 EnsurePetCatalog
        // 之前跑 —— 已经初始化过的清单不会再进那条分支，迁移不能挂在那儿。
        if (probe is null)
        {
            try
            {
                dataStore.MigrateMissingNames();
            }
            catch (Exception error)
            {
                KernelLog.Write($"pet-catalog-name-migration-failed {error.GetType().Name}: {error.Message}");
            }
        }

        bool catalogReady = startupSettings.PetCatalogInitialized == true;
        if (probe is null && !catalogReady)
        {
            catalogReady = EnsurePetCatalog(dataStore, startupSettings);
        }

        // 清单初始化过、且现在一只不剩 = 用户真的把宠物删光了：不要再重建内置默认宠物。
        bool noDefaultPet = probe is null && catalogReady && dataStore.ReadPets().Count == 0;
        if (startupSettings.DisabledPlugins is { Count: > 0 } disabledIds)
        {
            KernelLog.Write($"startup-plugins-disabled count={disabledIds.Count}");
        }

        // 用户插件根（2026-10-28）：插件装进数据区而不是发行目录。装载顺序里它排在
        // packages/plugins 之后 —— 同 id 时内置那份先被扫到，用户改的那份不悄悄顶掉内置实现。
        string userPlugins = Path.Combine(dataStore.Root, PetDataStore.PluginsFolder);
        try
        {
            Directory.CreateDirectory(userPlugins);
        }
        catch (Exception error)
        {
            KernelLog.Write($"plugin-user-root-failed {error.GetType().Name}: {error.Message}");
        }

        // 界面材质（原规格 18 §2.7）：必须在任何窗口构造之前设好 —— Theme.Apply 在窗口
        // 建句柄时就把材质落上去，晚一步就会先闪一帧不透明窗。
        PetDock.Core.Ui.Design.Theme.Material = PetDock.Core.Ui.Design.Theme.ParseMaterial(startupSettings.Material);
        KernelLog.Write($"startup-material {PetDock.Core.Ui.Design.Theme.MaterialToken(PetDock.Core.Ui.Design.Theme.Material)}");

        PetDock.Core.Ui.Design.Theme.Mode = PetDock.Core.Ui.Design.Theme.ParseMode(startupSettings.ThemeMode);
        KernelLog.Write($"startup-theme {PetDock.Core.Ui.Design.Theme.ModeToken(PetDock.Core.Ui.Design.Theme.Mode)} light={PetDock.Core.Ui.Design.Theme.IsLight}");

        using PluginHost pluginHost = new(
            PluginHost.ProbePluginsRoot() ?? string.Empty,
            PluginHost.ResidentTickMs,
            autoLoad: true,
            consent: pluginPort,
            disabledPlugins: startupSettings.DisabledPlugins,
            enabledPlugins: startupSettings.EnabledPlugins,
            extraRoots: new[] { userPlugins },
            trustBuiltins: true);

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
        // 名册里有视图实现（原规格 11 §3）就用它出帧；否则退回 M0 的内容源工厂；
        // 再没有就退回内核纯色兜底 —— 常驻形态下兜底是合法的（窗口仍可见可命中）。
        //
        // 窗口持有的是**可替换内容源代理**（RFC-15 §3.3）：换宠物时换的是代理里面那一份，
        // 合成器层一行不改。代理在这里创建，导入路径通过它换源。
        // 窗口必须直接持有**可换源代理**，不是装配时那一份原始帧源。
        // 2026-10-28 实锤：以前窗口拿的是原始 PetFrameSource，导入/恢复只换了代理里那一份，
        // 窗口根本看不见；随后旧实例被释放，帧泵每帧报 "attach 需要 Imported（当前 Disposed）"。
        using var window = ComposeWindow(
            "resident",
            pluginHost,
            out SwappableFrameSource? frames,
            startupSettings.DisabledPlugins,
            startupSettings.EnabledPlugins,
            respectStartupPolicy: true,
            allowDefaultPet: !noDefaultPet);
        ComposeOutcome? composed = Composition;

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
        // （原规格 16 §3 的 input.key.* 是 capability only，没有第二条合法生产路径）。
        //
        // 宿主**不在这里启动**：装载期的授权弹窗需要一个活着的消息泵，而消息泵由
        // Application.Run 起来 —— 装载因此推迟到 UiHost 的第一个滴答（RFC-13 §2.4）。
        //
        // 传的是 pet（可能被恢复逻辑换过），不是 composed?.Instance —— 传错就等于
        // 「恢复了清单里的宠物，但桌面上还是原来那只」。
        var settingsStore = new UserSettingsStore(PetDataStore.Resolve().Root);


        // 插件日志面（RFC-23 / 原规格 20 §3）：装上收口，于是插件写 PluginLog.* 有地方去。
        // 必须在窗口装好内容之后、任何插件代码跑起来之前装（init 阶段就打日志的插件要留得下痕）。
        var pluginLog = new HostPluginLog(auditTrail);
        PluginLog.Sink = pluginLog;

        var diagnostics = new DiagnosticService(dataStore, pluginHost, pet, residentLog);
        diagnostics.PluginLog = pluginLog;

        // 诊断包的审计一节读跨进程流水（RFC-20 §3.4）。流水不可用时诊断包自己回退
        // 到环形缓冲，并在摘要里写明「不是全量」。
        diagnostics.Trail = auditTrail;

        var host = new UiHost(window, pet, pluginHost, frames, port, settingsStore, diagnostics, startHidden: noDefaultPet);

        // 「上一次启动装没装上」交给面板显示一次（读一次就删，见 UpdateStartup.ConsumeResult）。
        host.SetUpdateReport(updateReport);

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

        // --open-panel（重启与新版本安装之后）：首滴答摆好宠物之后再打开面板。
        // 不在构造期 Show —— 那时还没有消息泵（RFC-14 §2.2）。
        if (openPanelOnStart)
        {
            host.OpenPanelOnFirstTick();
        }

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
        // （写完才算硬证据，原规格 17 §3）。
        KernelLog.Sink = null;
        residentLog.Complete();
        residentLog.Dispose();

        // 审计流水同样收尾：落盘剩余的行、去掉 .part（RFC-20 §3.3 第 4 点）。
        auditTrail.Complete();
        auditTrail.Dispose();

        // 探针模式的退出码由判据决定（RESIDENT-PERF-OK / FAIL）；正常常驻仍是 0。
        return probe is null || !ResidentPerfProbe.Failed ? ExitOk : ExitFail;
    }

}
