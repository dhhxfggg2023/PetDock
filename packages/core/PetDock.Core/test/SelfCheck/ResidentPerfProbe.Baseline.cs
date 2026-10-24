// --residenttest 的基线半边（RFC-40 §4.3：与 PerfGate 同形改造）。
//
// 与 `--perftest` 的差别只有三处，且都不是语义差别：
//   ① 采样窗与预热窗**是命令行参数**（--seconds / --warmup），所以它们必须入 premise ——
//      常驻侧的那次假红（ws 23.7~30.6MB vs 基线 19.6MB）就是「拿默认 20s + 8 目录花名册
//      去比 10s + 5 插件基线」造成的接线错，不是内存变胖（docs/handoff/VERIFY-2026-10-24.md 红一）。
//   ② 内存一侧比的字段叫 private_ws_mb（毛私有工作集），不是 perftest 的 retained_heap_mb。
//   ③ 花名册的取值同样是「插件根 + 目录数」，但常驻侧的装载由 PluginHost 自己做；
//      这里读的是同一个探针口径（PluginHost.ProbePluginsRoot + 带清单的目录数），不另算一份。
//
// 纯计算（MachineTag/FpsTier/RosterTag/ShapeVerdict/PremiseMismatches/FieldNum）复用 PerfGate.Baseline.cs：
// 两份基线若是两套写法，下一次假红只需要换一个入口就能绕过。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static partial class ResidentPerfProbe
{
    /// <summary>常驻基线里必需存在的数值字段（形状门缺一条即红）。</summary>
    private static readonly (string Section, string Field)[] ResidentRequiredNumbers =
    {
        ("m-02", "cpu_idle_pct"),
        ("m-04", "private_ws_mb"),
    };

    /// <summary>
    /// D-14e 的常驻半边（RFC-40 语义）：--write-baseline 时重钉基线（连同前提），否则判三条基线判据。
    /// 返回**本方法自己发现的失败数**（调用方累加）。
    /// </summary>
    private static int Baseline(Reading r, double wsMb, Options options, double idleTotalPct)
    {
        string path = BaselinePath;
        if (options.WriteBaseline)
        {
            if (string.IsNullOrWhiteSpace(options.Note))
            {
                // 判据名改对了（RFC-40 §3.1 第 3 条）：这是「用法错」，不是「产品变肥」。
                Emit("CHECK resident-baseline-shape FAIL reason=note-missing"
                    + " usage: --write-baseline 必须带 --note \"为什么基线变了\"");
                return 1;
            }

            var root = new JsonObject
            {
                ["note"] = options.Note,
                ["captured_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["env"] = new JsonObject
                {
                    ["os"] = Environment.OSVersion.ToString(),
                    ["cpu"] = PerfGate.CpuModel(),
                    ["cores"] = Environment.ProcessorCount,
                },
                ["premise"] = PerfGate.BuildPremise(idleTotalPct, r.Delta, options.Seconds, options.WarmupMs),
                ["m-02"] = new JsonObject { ["cpu_idle_pct"] = Round(r.CpuPct), ["limit"] = CpuIdleMax },
                ["m-04"] = new JsonObject { ["private_ws_mb"] = Round(wsMb), ["limit"] = PrivateWsMaxMb },
                ["d-14d"] = new JsonObject
                {
                    ["seconds"] = r.Seconds,
                    ["ticks"] = r.Delta.Ticks,
                    ["composed"] = r.Delta.Composed,
                    ["handles"] = r.Handles1,
                    ["gdi"] = r.Gdi1,
                    ["user"] = r.User1,
                },
                ["unit"] = "RFC-40：绝对门限硬、相对诊断软、前提全等时相对才硬；--write-baseline 每次必须带 note",
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Emit($"CHECK resident-baseline-written PASS {PerfGate.DisplayPath(path)} note={options.Note}");
            return 0;
        }

        int failures = 0;
        JsonObject? baseline = PerfGate.ReadBaseline(path, out string problem);

        // ① 形状（硬）：仓库状态坏了永远是红，但与负载无关。
        (bool shapeOk, string shapeDetail) =
            PerfGate.ShapeVerdict(baseline, problem, path, ResidentRequiredNumbers);
        failures += Check("resident-baseline-shape", shapeOk, shapeDetail);

        // ② 相对诊断（软）：永远打印，永不进 failures。
        RelativeDiagnostic(baseline, r.CpuPct, wsMb);

        // ③ 前提门（前提全等时是硬门）。
        JsonObject? premise = baseline?["premise"] as JsonObject;
        if (premise is null)
        {
            Emit("CHECK-DISPLAY resident-relative-gated skipped reason=premise-missing");
            return failures;
        }

        List<string> mismatches = PerfGate.PremiseMismatches(premise, idleTotalPct, r.Delta,
            options.Seconds, options.WarmupMs);
        if (mismatches.Count > 0)
        {
            Emit("CHECK-DISPLAY resident-relative-gated skipped reason=premise-mismatch "
                + string.Join(" ", mismatches));
            return failures;
        }

        double bCpu = PerfGate.FieldNum(baseline, "m-02", "cpu_idle_pct");
        double bWs = PerfGate.FieldNum(baseline, "m-04", "private_ws_mb");
        double cpuDeltaPp = r.CpuPct - bCpu;
        double cpuDeltaPct = bCpu > 0 ? cpuDeltaPp * 100 / bCpu : 0;
        double wsDelta = wsMb - bWs;
        // 阈值一个都没改：CPU 过相对 5% 且过 0.05pp 地板；ws 超 max(1MB, +5%)（绝对优先，判据原样）。
        bool cpuRegressed = cpuDeltaPct > RegressionMaxPct && cpuDeltaPp > CpuNoiseFloorPct;
        bool wsRegressed = wsDelta > Math.Max(WsNoiseFloorMb, bWs * RegressionMaxPct / 100);
        failures += Check("resident-relative-gated", !(cpuRegressed || wsRegressed),
            $"cpu delta={PerfGate.Delta(cpuDeltaPp, "0.000")}pp ({PerfGate.Delta(cpuDeltaPct, "0.0")}%)"
            + $" ws delta={PerfGate.Delta(wsDelta, "0.000")}MB premise=matched");
        return failures;
    }

    /// <summary>相对诊断行：基线值、本次值、差值、相对百分比、以及「若按旧语义会不会判红」。</summary>
    private static void RelativeDiagnostic(JsonObject? baseline, double cpuPct, double wsMb)
    {
        if (baseline is null)
        {
            Emit("CHECK-DISPLAY resident-relative-diagnostic skipped reason=baseline-missing");
            return;
        }

        double bCpu = PerfGate.FieldNum(baseline, "m-02", "cpu_idle_pct");
        double bWs = PerfGate.FieldNum(baseline, "m-04", "private_ws_mb");
        double cpuDeltaPp = cpuPct - bCpu;
        double cpuDeltaPct = bCpu > 0 ? cpuDeltaPp * 100 / bCpu : 0;
        double wsDelta = wsMb - bWs;
        // 「旧语义」= sameEnv（只比 CPU 型号）+ CPU 相对+5% 且过地板，或 ws 超 max(1MB, +5%)。
        bool sameMachine = PerfGate.MachineTag() == PerfGate.Text(baseline["premise"] as JsonObject, "machine");
        bool wouldFail = sameMachine
            && ((cpuDeltaPct > RegressionMaxPct && cpuDeltaPp > CpuNoiseFloorPct)
                || wsDelta > Math.Max(WsNoiseFloorMb, bWs * RegressionMaxPct / 100));
        Emit("CHECK-DISPLAY resident-relative-diagnostic"
            + $" cpu baseline={Fmt(bCpu)} now={Fmt(cpuPct)}"
            + $" delta={PerfGate.Delta(cpuDeltaPp, "0.000")}pp ({PerfGate.Delta(cpuDeltaPct, "0.0")}%)"
            + $" floor={Fmt(CpuNoiseFloorPct)}pp"
            + $" ws baseline={Fmt(bWs)} now={Fmt(wsMb)} delta={PerfGate.Delta(wsDelta, "0.000")}MB"
            + $" ws-floor={Fmt(Math.Max(WsNoiseFloorMb, bWs * RegressionMaxPct / 100))}MB"
            + $" machine={(sameMachine ? "same" : "changed")}"
            + $" verdict={(wouldFail ? "would-fail-if-gated" : "would-pass-if-gated")}");
    }
}
