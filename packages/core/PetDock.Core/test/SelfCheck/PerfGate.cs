// --perftest：把 原规格 41 §6 的 D-14 功耗门禁从「判据写在文档里」变成「一条命令能测、
// 能量、能拦」。判定依据与阈值逐条对照 原规格 41:122-128：
//   D-14a 空闲 CPU ≤0.5% 单核（M-02）        → CHECK perf-cpu-idle
//   D-14b 常驻内存 ≤150MB 私有工作集（M-04） → CHECK perf-private-ws
//   D-14c 唤醒首帧 ≤1.5s（M-03）             → CHECK perf-first-frame
//   D-14d 8h 泄漏 ≤5%（M-09）                → --soak 长跑模式 + 句柄计数
//   D-14e 相对基线（RFC-40 改语义）           → perf-relative-diagnostic（永远打印、永不失败）
//                                              + perf-relative-gated（仅前提全等时是硬门）
//   D-14f 基线钉死旧性能基线   → --write-baseline --note "<为什么变了>"，连同 premise
//
// 与 原规格 50:93「仪表盘读数与 D-14 门禁命令一致」的关系：**本文件就是那个「门禁命令」**。
// M8 的内置性能面板（原规格 18 未定义，需先走 RFC）今后的读数必须由同一套采样函数产出，
// 而不是各算各的——所以采样逻辑集中在这里，面板只做展示。
//
// 单机单环境（原规格 41:129）：基线里记 premise（机器三段 + 整机负载 + 帧率档 + 花名册 + 采样窗），
// 换了机器**或**换了安静程度/花名册/采样窗，相对比较就不可比 —— 那时只打印，不判红（RFC-40 §3.2）。
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static partial class PerfGate
{
    private const double CpuIdleMax = 0.5;        // D-14a（单核百分比）
    private const double PrivateWsMaxMb = 150;    // D-14b
    private const double FirstFrameMaxMs = 1500;  // D-14c
    private const double RetainedGrowthMaxMb = 0.5;  // D-14d 短形态（回收后仍驻留的增量）
    // 短窗的句柄噪声：+1 常见于线程池/计时器首次惰性创建，不能当成「每窗都在长」的泄漏。
    // 8h 长跑（--soak）仍按 0 容忍累计增长；这里只放过单个窗口的一次性抖动。
    private const int HandleNoise = 1;

    // 以下两条是**诊断阈值**，不是硬门（RFC-40 §3.1 第 2 条）：值一个没改（5.0 / 0.05），
    // 只有「红了算不算失败」变了 —— 前提全等时它们进 perf-relative-gated（那时是硬门），
    // 前提不等时它们只出现在 perf-relative-diagnostic 那一行里。
    private const double RegressionMaxPct = 5;
    // CPU 噪声地板（百分点）：0.01pp 在 0.05% 的基线上就是 20%，5% 的相对阈值小于读数分辨率
    // （本机 32 核一窗 10s，一个调度时间片折 0.0049pp）。地板取 M-02 上限 0.50% 的 1/5。
    // 2026-10-28 实测：同一台机、同一基线（0.039%），紧跟在 uitest 之后跑 perftest 读到 0.13%，
    // 0.05 的地板把 0.09pp 的整机抖动判成 +237% 回归。相对门只在前提匹配时生效，噪声地板必须
    // 高于「换一个窗口就重现不了」的量级；0.10pp 仍能拦住从 0.04% 涨到 0.20% 的真回归。
    private const double CpuNoiseFloorPct = 0.10;

    /// <summary>
    /// 整机负载允许的上浮幅度（百分点，RFC-40 §3.2 的 ambient_band_pp）。
    /// 它是判据的阈值而不是采集参数：写基线时由代码填进 premise，读基线时按它判「此刻是否可比」。
    /// 3.0 是初值（RFC-40 §6.2 明说没有分布依据）；重钉时若本机常态变了，改这里一处。
    /// </summary>
    private const double AmbientBandPp = 3.0;
    private const int WarmupMs = 1500;            // 预热窗口：JIT/首次分配不算进 D-14d 基线
    private const int StabilityWindowMs = 5000;   // D-14d 短形态的第二个采样窗（稳态增长）

    // 基线属于门禁数据，不是测试工程：放在 tools/ 与 core-loc-baseline.json 同处。
    // 2026-10-28 退役 tests/ 后这里曾是唯一还在找 tests/ 的活引用。
    private static string BaselinePath => Path.Combine(RepoRoot(), "tools", "perf.baseline.json");

    public static int Run(string[] args)
    {
        int seconds = ArgInt(args, "--seconds", 10);
        int soakSeconds = ArgInt(args, "--soak", 0);
        bool writeBaseline = Has(args, "--write-baseline");
        string note = ArgValue(args, "--note") ?? string.Empty;
        _logPath = ArgValue(args, "--log");

        if (writeBaseline && string.IsNullOrWhiteSpace(note))
        {
            Emit("PERF-FAIL usage: --write-baseline 必须带 --note \"为什么基线变了\"（D-14f）");
            return 1;
        }

        using var window = Compose();
        // 位置由本方法定（判据要确定几何）；**可见性与泵归 ResidentFrameLoop**（t15 硬约束 2）：
        // Start() 自己 Show + BringToFrontOfTopmost + PresentFirstFrame + 起泵，本文件不碰这三件事。
        window.MoveToCenter();
        using var loop = new ResidentFrameLoop(window);
        loop.Start();
        Application.DoEvents();

        // D-14c：进程启动 → 首帧上屏。StartTime 读不到时（权限/时钟）如实报 -1 而不是编一个数。
        double firstFrameMs = MeasureFirstFrameMs(window);

        using Process self = Process.GetCurrentProcess();

        // 采样窗内每 250ms 调一次 Reevaluate —— 与 UiHost 的 UI 滴答同频，也是泵停着时唯一的驱动源。
        var cpu = new CpuSampler(self, loop);

        // 预热窗口必须落在「内存基线」之前：JIT 与首次分配发生在启动后头一两秒，
        // 把它们算进 D-14d 会让每条判据都报 20% 的假增长（本机实录：8.70→10.46MB =
        // 20.26% FAIL，而整段时间里根本没有泄漏）。D-14d 问的是稳态之后是否持续增长，
        // 所以基线取在预热之后、测量窗口之前。
        cpu.Warmup(WarmupMs);
        long wsStart = RetainedWorkingSetBytes(self);
        int handlesStart = self.HandleCount;

        // 空闲窗的起止快照：相减得到「这一窗真的跑了多少 tick / 提交了几帧」。
        // 为什么必须有它：驱动面换成 ResidentFrameLoop 之后，CPU 低可能是「在静止档跳过提交」，
        // 也可能是「泵根本没跑」—— 只有 tick 计数能把这两件事分开（RFC-21 t15）。
        LoopReadings idleStart = LoopReadings.Of(loop);
        // 整机负载与进程 CPU **同一窗**取差（RFC-40 §3.2.1）：两个快照把 cpu.Sample 夹在中间，
        // 于是 ambient 与 cpuPct 覆盖的是同一段时间。分开取会得到两个不可比的数（窗错位时差可达数 pp）。
        SystemAmbient ambientStart = SystemAmbient.Of();
        TimeSpan cpuSpan;
        double cpuPct = cpu.Sample(seconds * 1000, out cpuSpan);
        double idleTotalPct = SystemAmbient.Of().BusyPct(ambientStart);
        LoopReadings idle = LoopReadings.Of(loop).Minus(idleStart);
        double idleRate = cpuSpan.TotalSeconds > 0 ? idle.Ticks / cpuSpan.TotalSeconds : 0;

        // D-14d 的短形态：**稳态**是否还在长。不能拿「启动前 → 跑完」比——那个差值里全是
        // JIT/GC 堆收敛（本机实录：预热后 8s 内仍长 0.57MB = 5.75%，判 FAIL 而根本没有泄漏）。
        // 所以比的是两个相邻采样窗之间的增长：第一个窗口结束时取基线，再跑一个窗口看还长多少。
        long wsMid = RetainedWorkingSetBytes(self);
        long heapWinMid = GC.GetTotalMemory(true);
        var guiWinMid = GuiResources(self);
        int handlesMid = self.HandleCount;
        double stabilityPct = 0;
        long wsEnd = wsMid;
        long heapWinEnd = heapWinMid;
        var guiWinEnd = guiWinMid;
        int handlesEnd = handlesMid;
        if (seconds >= 4)
        {
            cpu.Sample(StabilityWindowMs, out _);
            wsEnd = RetainedWorkingSetBytes(self);
            heapWinEnd = GC.GetTotalMemory(true);
            guiWinEnd = GuiResources(self);
            handlesEnd = self.HandleCount;
            stabilityPct = wsMid > 0 ? (wsEnd - wsMid) * 100.0 / wsMid : 0;
        }

        if (soakSeconds > 0)
        {
            SoakReadings soak = RunSoak(cpu, self, soakSeconds, wsMid, handlesMid);
            wsEnd = soak.WsEnd;
            heapWinEnd = soak.HeapEnd;
            guiWinEnd = (soak.GdiEnd, soak.UserEnd);
            handlesEnd = soak.HandlesEnd;
            stabilityPct = soak.GrowthPct;
            // 长跑窗的起点读数也换成它的（否则「驻留增量」会拿 10s 窗的起点比长跑窗的终点）。
            heapWinMid = soak.HeapMid;
            guiWinMid = (soak.GdiMid, soak.UserMid);
            handlesMid = soak.HandlesMid;
        }

        // 遮挡态（RFC-21 §4）：造真 topmost 覆盖窗压住宠物窗 5 秒，采 CPU + 档位 + 计数。
        // 放在泄漏窗之后：D-14d 要的是相邻两窗的稳态增长，中间插进建窗/关窗会污染它。
        OccludedSample occluded = MeasureOccluded(loop, window, cpu, idleRate);
        Application.DoEvents();

        double wsMb = wsEnd / (1024.0 * 1024.0);
        double leakPct = stabilityPct;

        Emit($"CHECK-DISPLAY perf-env os={Environment.OSVersion} cpu={CpuModel()} cores={Environment.ProcessorCount}"
            + $" ambient={Fmt(idleTotalPct)}%");
        Emit($"CHECK-DISPLAY perf-raw cpu={Fmt(cpuPct)}% over={cpuSpan.TotalSeconds:0.0}s"
            + $" ws-start={Mb(wsStart)} ws-mid={Mb(wsMid)} ws-end={Mb(wsEnd)} first-frame={Fmt(firstFrameMs)}ms"
            + $" handles={handlesStart}->{handlesMid}->{handlesEnd}");
        Emit("CHECK-DISPLAY perf-throttle-idle " + idle.Detail(cpuSpan.TotalSeconds));

        int failures = 0;
        // 「门禁测到了产品那条路」本身要能证伪：泵若根本没跑（全屏档把泵停了、或 loop 没起来），
        // 下面每条 CPU 读数都会漂亮得离谱。ticks>0 且 composed>=ticks 才说明真的在出帧。
        Check(ref failures, "perf-loop-running", idle.Ticks > 0 && idle.Composed >= idle.Ticks,
            "ticks=" + idle.Ticks.ToString(CultureInfo.InvariantCulture)
            + " composed=" + idle.Composed.ToString(CultureInfo.InvariantCulture)
            + " tier=" + idle.Tier.ToString().ToLowerInvariant()
            + " (0 tick = 驱动面没跑，CPU 读数不可信)");
        Check(ref failures, "perf-cpu-idle", cpuPct <= CpuIdleMax,
            $"{Fmt(cpuPct)}% <= {Fmt(CpuIdleMax)}% (M-02, {cpuSpan.TotalSeconds:0.0}s avg)");
        Check(ref failures, "perf-private-ws", wsMb <= PrivateWsMaxMb,
            $"{Fmt(wsMb)}MB <= {Fmt(PrivateWsMaxMb)}MB (M-04)");
        Check(ref failures, "perf-first-frame", firstFrameMs >= 0 && firstFrameMs <= FirstFrameMaxMs,
            $"{Fmt(firstFrameMs)}ms <= {Fmt(FirstFrameMaxMs)}ms (M-03)");
        // D-14d 每 8h ≤5%：短跑测的是「稳态窗内**回收后仍驻留**的东西是否还在长」。
        //
        // 2026-10-22 改口径（原因与三次实测见 PerfGate.Soak.cs 文件头）：毛工作集在有分配压力
        // 但没有自然 GC 的窗口里会跟着「GC 已提交、尚未回收的段」一起涨（实测 180s +43~49%、
        // 300s +76%，而同期 retained-heap 是 0.63→0.62MB 平的），判它会把「GC 还没跑」当成泄漏。
        // 所以这里判两件**回收后仍然存在**的东西：① 托管堆驻留增量 ≤0.5MB；② 非托管对象不增
        // （GDI/USER/句柄，泄漏时只能靠它们看出来）。增长方向才算缺陷，内存回落不是缺陷。
        double retainedMb = (heapWinEnd - heapWinMid) / (1024.0 * 1024.0);
        int gdiDelta = guiWinEnd.Gdi - guiWinMid.Gdi;
        int userDelta = guiWinEnd.User - guiWinMid.User;
        int handleDelta = handlesEnd - handlesMid;
        bool nativeOk = gdiDelta <= 0 && userDelta <= 0 && handleDelta <= HandleNoise;
        Check(ref failures, "perf-leak-short", retainedMb <= RetainedGrowthMaxMb && nativeOk,
            $"retained {Fmt(retainedMb)}MB <= {Fmt(RetainedGrowthMaxMb)}MB"
            + $" gdi={gdiDelta:+0;-0;0} user={userDelta:+0;-0;0} handles={handleDelta:+0;-0;0}"
            + $" (毛工作集 {Fmt(leakPct)}% 只作展示：见 INFO perf-soak)");
        // RFC-40 §4.3：调用参数从六个减为七个里的「五个」——retainedMb 不再进硬门（它的「与基线比」
        // 那一半随相对比较一并移出，留在 perf-relative-diagnostic）。ws/first 保留：诊断行要打印它们。
        // 同一趟的 idleTotalPct 与 idle 档位也带过去：它们是前提（ambient / fps_tier）的来源。
        failures += Regression(cpuPct, wsMb, firstFrameMs, retainedMb, idleTotalPct, idle,
            (int)Math.Round(cpuSpan.TotalSeconds), writeBaseline, note);
        JudgeOccluded(occluded, ref failures);

        Emit(failures == 0 ? "PERF-OK" : "PERF-FAIL");
        return failures == 0 ? 0 : 1;
    }

    private static LayeredFrameWindow Compose()
    {
        ComposeOutcome outcome = FrameComposition.Compose("perftest", attachInstance: true);
        return outcome.Content is null ? new LayeredFrameWindow() : new LayeredFrameWindow(outcome.Content);
    }

    private static double MeasureFirstFrameMs(LayeredFrameWindow window)
    {
        try
        {
            DateTime start = Process.GetCurrentProcess().StartTime;
            return (DateTime.Now - start).TotalMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    internal static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PetDock.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Environment.CurrentDirectory;
    }

    internal static string CpuModel()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim()
                ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>私有工作集（原规格 41:123 的字面量）。Process 类没有这个读数，走 psapi EX2。</summary>
    internal static long PrivateWorkingSetBytes(Process p)
    {
        var counters = new PROCESS_MEMORY_COUNTERS_EX2();
        counters.cb = Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>();
        if (GetProcessMemoryInfo(p.Handle, ref counters, counters.cb))
        {
            long privateWs = (long)counters.PrivateWorkingSetSize;
            // EX2 字段拿不到时退回私有提交量（PrivateUsage），都不行才退回工作集；
            // 任何一次回退都必须在读数里看得出来，不能让判据静默变成 0。
            return privateWs > 0 ? privateWs : (long)counters.PrivateUsage > 0 ? (long)counters.PrivateUsage : p.WorkingSet64;
        }

        return p.WorkingSet64;
    }

    private static void Check(ref int failures, string name, bool ok, string detail)
    {
        if (!ok)
        {
            failures++;
        }

        Emit($"CHECK {name} {(ok ? "PASS" : "FAIL")} {detail}");
    }

    /// <summary>
    /// 一行诊断同时写控制台与 --log 文件。为什么需要文件：这些入口是 WinExe，
    /// AttachConsole(ATTACH_PARENT_PROCESS) 会把 Console.Out 绑到父进程的**控制台**而不是
    /// 被重定向的管道，所以 `| Out-File` / -RedirectStandardOutput 拿到的永远是 0 字节
    /// （本机实录：--perftest 与 --selftest 都一样）。门禁证据必须可留存，所以落盘走自己的文件。
    /// </summary>
    private static void Emit(string line)
    {
        Console.WriteLine(line);
        if (string.IsNullOrWhiteSpace(_logPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_logPath))!);
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Emit($"WARN perf-log-write-failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? _logPath;

    private static bool Has(string[] args, string flag)
        => Array.Exists(args, a => string.Equals(a, flag, StringComparison.Ordinal));

    private static string? ArgValue(string[] args, string flag)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.Ordinal));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int ArgInt(string[] args, string flag, int fallback)
        => int.TryParse(ArgValue(args, flag), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            : fallback;

    private static string Mb(long bytes) => Fmt(bytes / (1024.0 * 1024.0));
    private static double Round(double v) => Math.Round(v, 3);
    private static string Fmt(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}
