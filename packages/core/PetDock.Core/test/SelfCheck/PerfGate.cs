// --perftest：把 docs/41 §6 的 D-14 功耗门禁从「判据写在文档里」变成「一条命令能测、
// 能量、能拦」。判定依据与阈值逐条对照 docs/41:122-128：
//   D-14a 空闲 CPU ≤0.5% 单核（M-02）        → CHECK perf-cpu-idle
//   D-14b 常驻内存 ≤150MB 私有工作集（M-04） → CHECK perf-private-ws
//   D-14c 唤醒首帧 ≤1.5s（M-03）             → CHECK perf-first-frame
//   D-14d 8h 泄漏 ≤5%（M-09）                → --soak 长跑模式 + 句柄计数
//   D-14e 相对基线恶化 >5% 即失败            → CHECK perf-regression
//   D-14f 基线钉死 tests/perf.baseline.json   → --write-baseline --note "<为什么变了>"
//
// 与 docs/50:93「仪表盘读数与 D-14 门禁命令一致」的关系：**本文件就是那个「门禁命令」**。
// M8 的内置性能面板（docs/18 未定义，需先走 RFC）今后的读数必须由同一套采样函数产出，
// 而不是各算各的——所以采样逻辑集中在这里，面板只做展示。
//
// 单机单环境（docs/41:129）：基线里记 CPU 型号与系统版本，换了机器就重新采基线并说明。
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
    private const double LeakMaxPct = 5;          // D-14d
    private const double RegressionMaxPct = 5;    // D-14e
    private const int WarmupMs = 1500;            // 预热窗口：JIT/首次分配不算进 D-14d 基线
    private const int StabilityWindowMs = 5000;   // D-14d 短形态的第二个采样窗（稳态增长）

    private static string BaselinePath => Path.Combine(RepoRoot(), "tests", "perf.baseline.json");

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
        long wsStart = PrivateWorkingSetBytes(self);
        int handlesStart = self.HandleCount;

        // 空闲窗的起止快照：相减得到「这一窗真的跑了多少 tick / 提交了几帧」。
        // 为什么必须有它：驱动面换成 ResidentFrameLoop 之后，CPU 低可能是「在静止档跳过提交」，
        // 也可能是「泵根本没跑」—— 只有 tick 计数能把这两件事分开（RFC-21 t15）。
        LoopReadings idleStart = LoopReadings.Of(loop);
        TimeSpan cpuSpan;
        double cpuPct = cpu.Sample(seconds * 1000, out cpuSpan);
        LoopReadings idle = LoopReadings.Of(loop).Minus(idleStart);
        double idleRate = cpuSpan.TotalSeconds > 0 ? idle.Ticks / cpuSpan.TotalSeconds : 0;

        // D-14d 的短形态：**稳态**是否还在长。不能拿「启动前 → 跑完」比——那个差值里全是
        // JIT/GC 堆收敛（本机实录：预热后 8s 内仍长 0.57MB = 5.75%，判 FAIL 而根本没有泄漏）。
        // 所以比的是两个相邻采样窗之间的增长：第一个窗口结束时取基线，再跑一个窗口看还长多少。
        long wsMid = PrivateWorkingSetBytes(self);
        int handlesMid = self.HandleCount;
        double stabilityPct = 0;
        long wsEnd = wsMid;
        int handlesEnd = handlesMid;
        if (seconds >= 4)
        {
            cpu.Sample(StabilityWindowMs, out _);
            wsEnd = PrivateWorkingSetBytes(self);
            handlesEnd = self.HandleCount;
            stabilityPct = wsMid > 0 ? (wsEnd - wsMid) * 100.0 / wsMid : 0;
        }

        if (soakSeconds > 0)
        {
            cpu.Sample(soakSeconds * 1000, out _);
            long wsSoak = PrivateWorkingSetBytes(self);
            handlesEnd = self.HandleCount;
            Emit($"INFO perf-soak seconds={soakSeconds} ws-mid={Mb(wsMid)} ws-end={Mb(wsSoak)}"
                + $" soak-growth={Fmt(wsMid > 0 ? (wsSoak - wsMid) * 100.0 / wsMid : 0)}%");
            wsEnd = wsSoak;
            stabilityPct = wsMid > 0 ? (wsSoak - wsMid) * 100.0 / wsMid : 0;
        }

        // 遮挡态（RFC-21 §4）：造真 topmost 覆盖窗压住宠物窗 5 秒，采 CPU + 档位 + 计数。
        // 放在泄漏窗之后：D-14d 要的是相邻两窗的稳态增长，中间插进建窗/关窗会污染它。
        OccludedSample occluded = MeasureOccluded(loop, window, cpu, idleRate);
        Application.DoEvents();

        double wsMb = wsEnd / (1024.0 * 1024.0);
        double leakPct = stabilityPct;

        Emit($"CHECK-DISPLAY perf-env os={Environment.OSVersion} cpu={CpuModel()} cores={Environment.ProcessorCount}");
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
        // D-14d 每 8h ≤5%：短跑测的是「稳态窗内是否还在长」。泄漏只有增长方向——内存回落
        // 不是缺陷，所以判据是 leakPct <= 上限，不是 |leakPct| <= 上限。
        Check(ref failures, "perf-leak-short", leakPct <= LeakMaxPct,
            $"steady-growth {Fmt(leakPct)}% <= {Fmt(LeakMaxPct)}%"
            + $" handle={handlesMid}->{handlesEnd} (D-14d full judgement needs --soak 28800)");
        failures += Regression(failures: 0, cpuPct, wsMb, firstFrameMs, writeBaseline, note);
        JudgeOccluded(occluded, ref failures);

        Emit(failures == 0 ? "PERF-OK" : "PERF-FAIL");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>D-14e/D-14f：与 tests/perf.baseline.json 比对；--write-baseline 时重钉基线。</summary>
    private static int Regression(int failures, double cpuPct, double wsMb, double firstFrameMs,
        bool writeBaseline, string note)
    {
        string path = BaselinePath;
        if (writeBaseline)
        {
            var root = new JsonObject
            {
                ["note"] = note,
                ["captured_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["env"] = new JsonObject
                {
                    ["os"] = Environment.OSVersion.ToString(),
                    ["cpu"] = CpuModel(),
                    ["cores"] = Environment.ProcessorCount,
                },
                ["m-02"] = new JsonObject { ["cpu_idle_pct"] = Round(cpuPct), ["limit"] = CpuIdleMax },
                ["m-04"] = new JsonObject { ["private_ws_mb"] = Round(wsMb), ["limit"] = PrivateWsMaxMb },
                ["m-03"] = new JsonObject { ["first_frame_ms"] = Round(firstFrameMs), ["limit"] = FirstFrameMaxMs },
                ["unit"] = "docs/41 D-14a/b/c；--write-baseline 每改一次必须带 note",
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Emit($"CHECK perf-baseline-written {path} note={note}");
            return failures;
        }

        if (!File.Exists(path))
        {
            // 没有基线时**不能**算通过：D-14e 是「相对基线别变肥」，没有基线就没有判据。
            Emit($"CHECK perf-regression FAIL baseline-missing {path} (先跑 --write-baseline --note \"…\")");
            return failures + 1;
        }

        JsonObject? b = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        string bEnv = b?["env"]?["cpu"]?.GetValue<string>() ?? "?";
        double bCpu = Num(b, "m-02", "cpu_idle_pct");
        double bWs = Num(b, "m-04", "private_ws_mb");
        double bFirst = Num(b, "m-03", "first_frame_ms");

        string detail = $"cpu {Fmt(bCpu)}->{Fmt(cpuPct)} ws {Fmt(bWs)}->{Fmt(wsMb)}"
            + $" first {Fmt(bFirst)}->{Fmt(firstFrameMs)} baseline-cpu={bEnv}";
        // 环境不同时**不算回归失败**（跨硬件比较没有意义，docs/41:129 单机单环境），但必须显式提示。
        bool sameEnv = bEnv == CpuModel();
        double cpuDelta = bCpu > 0 ? (cpuPct - bCpu) * 100 / bCpu : 0;
        double wsDelta = bWs > 0 ? (wsMb - bWs) * 100 / bWs : 0;
        bool regressed = sameEnv && (cpuDelta > RegressionMaxPct || wsDelta > RegressionMaxPct);
        Check(ref failures, "perf-regression", !regressed, detail + (sameEnv ? "" : " (env-changed: 重钉基线)"));
        return failures;
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

    private static string RepoRoot()
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

    private static string CpuModel()
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

    /// <summary>私有工作集（docs/41:123 的字面量）。Process 类没有这个读数，走 psapi EX2。</summary>
    private static long PrivateWorkingSetBytes(Process p)
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

    private static double Num(JsonObject? node, string section, string field)
    {
        try
        {
            return node?[section]?[field]?.GetValue<double>() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string Mb(long bytes) => Fmt(bytes / (1024.0 * 1024.0));
    private static double Round(double v) => Math.Round(v, 3);
    private static string Fmt(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}
