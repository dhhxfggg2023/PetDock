// --residenttest：D-14 的「常驻形态」重钉（原规格 50 M8 的未闭合点，2026-10-23）。
//
// 为什么需要它：`--perftest` 自己合成一个窗口 + ResidentFrameLoop（受控、可重放，适合判
// 降级链与节流档），但**那不是产品跑起来的样子**——常驻形态还多着插件宿主、托盘、日志落盘、
// 审计流水、清单恢复与 UiHost 的 250ms 滴答。原规格 50:131-133 因此写着「D-14 基线未针对常驻
// 形态重测」。本探针补的就是那一段：**原样跑 Program.RunResident**，只在旁边挂一个采样线程。
//
// 与产品路径的三处、且仅三处差异（都写在 RFC-26 §3）：
//   ① 授权端口换成「全授的会话端口」——授权弹窗要人点，门禁里不能有等待；
//   ② 采完改用 UiHost.ExitForProbe 退出（托盘那条「退出」有确认框，同样要人点）；
//   ③ 进程会写自己的数据区日志/审计（那正是产品行为，不规避）。
//
// 判据（原规格 41 §6 D-14a/b/d 的常驻读数；D-14c 是启动到首帧，perftest 已覆盖，此处不重测）：
//   CHECK resident-loop-running       ticks>0 且 composed>=ticks（没在出帧的 CPU 读数不可信）
//   CHECK resident-cpu-idle           ≤0.5% 单核（M-02）
//   CHECK resident-private-ws         ≤150MB（M-04）
//   CHECK resident-handles-stable     GDI/USER/句柄不增（D-14d 的非托管半边）
//   CHECK resident-baseline-shape     基线文件 + premise + 必需数值字段（硬；RFC-40 §3.1 第 3 条）
//   CHECK-DISPLAY resident-relative-diagnostic  与旧常驻性能基线的差（永不进 failures）
//   CHECK resident-relative-gated     仅前提全等时发射（那时是硬门：CPU +5% 且过 0.05pp，或 ws 超 max(1MB,+5%)）
// 末行 RESIDENT-PERF-OK / RESIDENT-PERF-FAIL，退出码 0/1。
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Forms;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static partial class ResidentPerfProbe
{
    private const double CpuIdleMax = 0.5;
    private const double PrivateWsMaxMb = 150;
    // 以下两条是**诊断阈值**（RFC-40 §3.1 第 2 条）：值不变（5 / 0.05），
    // 只有「红了算不算失败」变了 —— 前提全等时进 resident-relative-gated，否则只进诊断行。
    private const double RegressionMaxPct = 5;
    // 与 PerfGate 同一条噪声地板理由：空闲读数在 0.03~0.07% 抖，5% 相对阈值小于分辨率。
    private const double CpuNoiseFloorPct = 0.05;
    private const double WsNoiseFloorMb = 1.0;
    private const int PollMs = 250;
    private const int WatchdogGraceMs = 20000;

    private static System.Windows.Forms.Timer? _timer;
    private static Reading? _reading;
    private static string[] _args = Array.Empty<string>();

    internal sealed record Options(int WarmupMs, int Seconds, bool WriteBaseline, string Note, bool DenyAll);

    /// <summary>末行是否判红（Program 读它决定退出码）。</summary>
    internal static bool Failed { get; private set; }

    private static string BaselinePath => Path.Combine(PerfGate.RepoRoot(), "tests", "perf.resident.json");

    /// <summary>解析 --residenttest 的参数；不带该开关时返回 null（走原常驻路径）。</summary>
    internal static Options? Parse(string[] args)
    {
        // 临时诊断：探针不改 Program.cs（那是产品代码），参数从环境里读。
        _args = Environment.GetCommandLineArgs();
        if (args is null || !args.Any(a => string.Equals(a, "--residenttest", StringComparison.Ordinal)))
        {
            return null;
        }

        return new Options(
            WarmupMs: Math.Max(1000, ArgInt(args, "--warmup", 5000)),
            Seconds: Math.Max(3, ArgInt(args, "--seconds", 20)),
            WriteBaseline: args.Any(a => string.Equals(a, "--write-baseline", StringComparison.Ordinal)),
            Note: ArgValue(args, "--note") ?? string.Empty,
            // 归因用的 A/B 开关：全拒授权 = 插件拿不到任何能力（钩子/鼠标采样都不装）。
            // 只用于把「哪个能力带来的开销」拆开，不作为门禁读数。
            DenyAll: args.Any(a => string.Equals(a, "--deny-capabilities", StringComparison.Ordinal)));
    }

    /// <summary>装配完成后、Application.Run 之前调用：起采样线程 + UI 侧轮询定时器。</summary>
    internal static void Start(UiHost host, Options options, string[]? args = null)
    {
        // 临时诊断：探针不改 Program.cs（那是产品代码），参数从环境里读。
        _ = args;
        _args = Environment.GetCommandLineArgs();
        Emit($"CHECK-DISPLAY resident-probe warmup={options.WarmupMs}ms seconds={options.Seconds}s"
            + $" uid={GetCurrentThreadId()} baseline={BaselinePath}");

        var worker = new Thread(() => Measure(host, options)) { IsBackground = true, Name = "resident-perf-probe" };
        worker.Start();

        int ticks = 0;
        _timer = new System.Windows.Forms.Timer { Interval = PollMs };
        _timer.Tick += (_, _) =>
        {
            ticks++;
            if (_reading is { } done)
            {
                Stop();
                Judge(host, done, options);
                host.ExitForProbe();
                return;
            }

            if (ticks * PollMs < options.WarmupMs + options.Seconds * 1000 + WatchdogGraceMs)
            {
                return;
            }

            // 卡住时**必须判红并退出**，不能挂在那里等：授权弹窗、模态框都会让消息泵继续转，
            // 而采样线程等的是墙钟，两者一旦错位，门禁就成了「跑不完也不报错」的空转。
            Stop();
            Emit($"CHECK resident-probe-timeout FAIL waited={(ticks * PollMs)}ms"
                + " (采样未在预期时间内完成：是否有模态窗挡住了常驻装配？)");
            Failed = true;
            Emit("RESIDENT-PERF-FAIL");
            host.ExitForProbe();
        };
        _timer.Start();
    }

    private static void Stop()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>采样：预热 → 取空窗读数 → 空转 N 秒 → 取结束读数。全程不碰 UI 线程。</summary>
    private static void Measure(UiHost host, Options options)
    {
        try
        {
            Thread.Sleep(options.WarmupMs);
            using Process self = Process.GetCurrentProcess();

            // 与 --perftest 同口径：先强制回收再读私有工作集（PerfGate.Soak.cs 的注释说明了为什么
            // 不 EmptyWorkingSet）。这一步会让应用停顿一下，但发生在采样窗**之前**。
            long wsStart = PerfGate.RetainedWorkingSetBytes(self);
            (int gdi0, int user0) = PerfGate.GuiResources(self);
            int handles0 = self.HandleCount;
            PerfGate.LoopReadings loop0 = PerfGate.LoopReadings.Of(host.Loop);
            Dictionary<int, TimeSpan> threads0 = ThreadCpu(self);

            // 临时诊断：把采样线程单独拎出来（它自己也在枚举/读线程表，会占一点 CPU）。
            bool census = ThreadCensusWanted(_args);
            int windowMs = options.Seconds * 1000;
            if (census)
            {
                // 覆盖整个采样窗 + 窗外 1 秒，让「窗口起止」在时间轴上可对齐。
                var censusThread = new Thread(() => CensusLoop(self, CensusIntervalMs(_args), options.Seconds + 1))
                {
                    IsBackground = true,
                    Name = "resident-thread-census",
                };
                censusThread.Start();
            }

            self.Refresh();
            TimeSpan cpu0 = self.TotalProcessorTime;
            // 整机负载与进程 CPU **同一窗**取差（RFC-40 §3.2.1）：两个快照把 Thread.Sleep 夹在中间。
            PerfGate.SystemAmbient ambient0 = PerfGate.SystemAmbient.Of();
            var wall = Stopwatch.StartNew();
            Thread.Sleep(options.Seconds * 1000);
            wall.Stop();
            double idleTotalPct = PerfGate.SystemAmbient.Of().BusyPct(ambient0);

            self.Refresh();
            double cpuMs = (self.TotalProcessorTime - cpu0).TotalMilliseconds;
            double wallMs = Math.Max(1.0, wall.Elapsed.TotalMilliseconds);
            double cpuPct = cpuMs / wallMs / Environment.ProcessorCount * 100.0;

            long wsEnd = PerfGate.RetainedWorkingSetBytes(self);
            (int gdi1, int user1) = PerfGate.GuiResources(self);
            int handles1 = self.HandleCount;

            _reading = new Reading(
                wsStart, wsEnd, handles0, handles1, gdi0, gdi1, user0, user1,
                cpuPct, wall.Elapsed.TotalSeconds, PerfGate.LoopReadings.Of(host.Loop).Minus(loop0),
                TopThreads(threads0, ThreadCpu(self)), null, idleTotalPct);
        }
        catch (Exception ex)
        {
            _reading = new Reading(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, default, string.Empty,
                ex.GetType().Name + ": " + ex.Message, 0);
        }
    }

    /// <summary>判据 + 基线比对（全部在 UI 线程上跑，这样退出动作与判据在同一处发生）。</summary>
    private static void Judge(UiHost host, Reading r, Options options)
    {
        if (r.Error is not null)
        {
            Emit($"CHECK resident-probe-failed FAIL {r.Error}");
            Failed = true;
            Emit("RESIDENT-PERF-FAIL");
            return;
        }

        double wsMb = r.WsEnd / (1024.0 * 1024.0);
        int gdiDelta = r.Gdi1 - r.Gdi0;
        int userDelta = r.User1 - r.User0;
        int handleDelta = r.Handles1 - r.Handles0;

        Emit($"CHECK-DISPLAY resident-env os={Environment.OSVersion} cpu={PerfGate.CpuModel()}"
            + $" cores={Environment.ProcessorCount} pid={Environment.ProcessId}"
            + $" ambient={Fmt(r.IdleTotalPct)}%");
        Emit($"CHECK-DISPLAY resident-raw cpu={Fmt(r.CpuPct)}% over={Fmt(r.Seconds)}s"
            + $" ws={Fmt(r.WsStart / (1024.0 * 1024.0))}->{Fmt(wsMb)}MB"
            + $" handles={r.Handles0}->{r.Handles1}"
            + $" gdi={r.Gdi0}->{r.Gdi1} user={r.User0}->{r.User1}");
        Emit("CHECK-DISPLAY resident-loop " + r.Delta.Detail(r.Seconds));
        // 线程级的 CPU 分摊：判红时必须能说出「哪个线程在烧」，否则只能靠猜（本机 2026-10-23
        // 就出现过「同样的帧读数、两个入口差 50 倍 CPU」，没有这一行无法归因）。
        Emit("CHECK-DISPLAY resident-threads top=" + r.Threads + " count=" + r.ThreadCount.ToString(CultureInfo.InvariantCulture));
        // 临时诊断：采样窗的终点与「谁在窗口里最热」的全量前 8（与 resident-threads 的 top3 并存）。
        if (ThreadCensusWanted(_args))
        {
            try
            {
                using Process selfNow = Process.GetCurrentProcess();
                Emit("CHECK-DISPLAY resident-census-window-end " + DumpThreads(selfNow, new Dictionary<int, TimeSpan>(), (int)GetCurrentThreadId()));
            }
            catch (Exception error)
            {
                Emit("CHECK-DISPLAY resident-census-window-end FAILED " + error.GetType().Name + ": " + error.Message);
            }
        }

        int failures = 0;
        failures += Check("resident-loop-running", r.Delta.Ticks > 0 && r.Delta.Composed >= r.Delta.Ticks,
            "ticks=" + r.Delta.Ticks.ToString(CultureInfo.InvariantCulture)
            + " composed=" + r.Delta.Composed.ToString(CultureInfo.InvariantCulture)
            + " (0 tick = 常驻泵没跑，下面的 CPU 读数不可信)");
        failures += Check("resident-cpu-idle", r.CpuPct <= CpuIdleMax,
            $"{Fmt(r.CpuPct)}% <= {Fmt(CpuIdleMax)}% (M-02, {Fmt(r.Seconds)}s avg)");
        failures += Check("resident-private-ws", wsMb <= PrivateWsMaxMb,
            $"{Fmt(wsMb)}MB <= {Fmt(PrivateWsMaxMb)}MB (M-04)");
        failures += Check("resident-handles-stable", gdiDelta <= 0 && userDelta <= 0 && handleDelta <= 0,
            $"gdi={gdiDelta:+0;-0;0} user={userDelta:+0;-0;0} handles={handleDelta:+0;-0;0} (D-14d 非托管半边)");
        failures += Baseline(r, wsMb, options, r.IdleTotalPct);

        Failed = failures > 0;
        Emit(Failed ? $"RESIDENT-PERF-FAIL n={failures}" : "RESIDENT-PERF-OK");
    }

    private static int Check(string name, bool ok, string detail)
    {
        Emit($"CHECK {name} {(ok ? "PASS" : "FAIL")} {detail}");
        return ok ? 0 : 1;
    }

    private static string Fmt(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static double Round(double v) => Math.Round(v, 3);

    private static int ArgInt(string[] args, string flag, int fallback)
    {
        string? raw = ArgValue(args, flag);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private static string? ArgValue(string[] args, string flag)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.Ordinal));
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[i + 1]
            : null;
    }

    private static void Emit(string line)
    {
        try
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
        catch (IOException)
        {
            // 输出端关了（重定向到已删文件等）时不能因为「打不出日志」把门禁判红。
        }
    }

    private readonly record struct Reading(
        long WsStart, long WsEnd,
        int Handles0, int Handles1,
        int Gdi0, int Gdi1,
        int User0, int User1,
        double CpuPct, double Seconds,
        PerfGate.LoopReadings Delta, string Threads, string? Error,
        double IdleTotalPct)
    {
        public int ThreadCount => Threads.Length == 0 ? 0 : Threads.Split(',').Length;
    }

}
