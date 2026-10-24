// --perftest 的基线判据半边（RFC-40 §4.3：从 PerfGate.cs 的 Regression() 拆出并改语义）。
// 前提（怎么入档、从哪来、逐字段怎么比）在 PerfGate.Premise.cs，本文件只放判定。
//
// 三条判据，回答三个不同的问题，因此**分成三处**而不是一条 failures：
//   perf-baseline-shape        基线文件在不在、premise 在不在且 schema 对、必需数值字段全不全 —— 硬门。
//                              它判的是**仓库状态**，与机器负载无关，所以不会因为换了个忙碌时刻就红。
//   perf-relative-diagnostic   与基线的差：永远打印、永远不进 failures。「若按旧语义会不会判红」也打出来，
//                              这样「绿」就无法靠删掉诊断换来（RFC-40 §3.4 第 3 条）。
//   perf-relative-gated        前提（§3.2 的七个比较字段）全等时才发射；发射时是硬门，不等时只打印
//                              skipped 与每个不符字段的两个值。
//
// 为什么相对比较不能无条件硬（RFC-40 §1.3 的读数）：本机 32 核、10s 窗，一个 Windows 调度时间片
// 折 0.0049pp，噪声地板 0.05pp 只有 10.2 个时间片；同一份字节连跑三趟就有 0.03pp 的趟间抖动。
// 前提没有机器可读的落点，这条判据度量的就是「这台机器此刻的日程」，不是产品。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PetDock.Hosting;

internal static partial class PerfGate
{
    /// <summary>perftest 基线里必需存在的数值字段（形状门缺一条即红）。</summary>
    private static readonly (string Section, string Field)[] PerfRequiredNumbers =
    {
        ("m-02", "cpu_idle_pct"),
        ("m-04", "private_ws_mb"),
        ("m-03", "first_frame_ms"),
        ("d-14d", "retained_heap_mb"),
    };

    /// <summary>
    /// D-14e/D-14f（RFC-40 语义）：--write-baseline 时重钉基线（连同前提），否则判三条基线判据。
    /// 返回**本方法自己发现的失败数**（调用方累加进 failures）。
    /// </summary>
    private static int Regression(double cpuPct, double wsMb, double firstFrameMs, double retainedMb,
        double idleTotalPct, LoopReadings loop, int seconds, bool writeBaseline, string note)
    {
        string path = BaselinePath;
        if (writeBaseline)
        {
            var root = new JsonObject
            {
                ["note"] = note,
                ["captured_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                // env 三段保留：它是人读的那一份；premise.machine 才是判据读的那一份（两者同源，不改口径）。
                ["env"] = new JsonObject
                {
                    ["os"] = Environment.OSVersion.ToString(),
                    ["cpu"] = CpuModel(),
                    ["cores"] = Environment.ProcessorCount,
                },
                ["premise"] = BuildPremise(idleTotalPct, loop, seconds, WarmupMs),
                ["m-02"] = new JsonObject { ["cpu_idle_pct"] = Round(cpuPct), ["limit"] = CpuIdleMax },
                ["m-04"] = new JsonObject { ["private_ws_mb"] = Round(wsMb), ["limit"] = PrivateWsMaxMb },
                ["m-03"] = new JsonObject { ["first_frame_ms"] = Round(firstFrameMs), ["limit"] = FirstFrameMaxMs },
                ["d-14d"] = new JsonObject { ["retained_heap_mb"] = Round(retainedMb), ["limit_mb"] = RetainedGrowthMaxMb },
                ["unit"] = "RFC-40：绝对门限硬、相对诊断软、前提全等时相对才硬；--write-baseline 每次必须带 note",
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            // 与常驻侧的行形统一（RFC-40 §4.3 末条）：原来这行没有 PASS/FAIL，读起来像一句留言。
            Emit($"CHECK perf-baseline-written PASS {DisplayPath(path)} note={note}");
            return 0;
        }

        int failures = 0;
        JsonObject? baseline = ReadBaseline(path, out string problem);

        // ① 形状（硬）：仓库状态坏了永远是红，但同一条红与负载无关 —— 它不会因为忙就红。
        (bool shapeOk, string shapeDetail) = ShapeVerdict(baseline, problem, path, PerfRequiredNumbers);
        Check(ref failures, "perf-baseline-shape", shapeOk, shapeDetail);

        // ② 相对诊断（软）：没有基线时也必须有一行，否则「没打印」会读成「没问题」。
        RelativeDiagnostic(baseline, cpuPct, wsMb, firstFrameMs, retainedMb);

        // ③ 前提门（前提全等时是硬门）：先看前提在不在，再看七个字段是否全等。
        JsonObject? premise = baseline?["premise"] as JsonObject;
        if (premise is null)
        {
            Emit("CHECK-DISPLAY perf-relative-gated skipped reason=premise-missing");
            return failures;
        }

        List<string> mismatches = PremiseMismatches(premise, idleTotalPct, loop, seconds, WarmupMs);
        if (mismatches.Count > 0)
        {
            Emit("CHECK-DISPLAY perf-relative-gated skipped reason=premise-mismatch "
                + string.Join(" ", mismatches));
            return failures;
        }

        double bCpu = FieldNum(baseline, "m-02", "cpu_idle_pct");
        double bRetained = FieldNum(baseline, "d-14d", "retained_heap_mb");
        double cpuDeltaPp = cpuPct - bCpu;
        double cpuDeltaPct = bCpu > 0 ? cpuDeltaPp * 100 / bCpu : 0;
        double retainedDelta = retainedMb - bRetained;
        // 阈值一个都没改（RFC-40 §3.1 第 2 条：降级的是「是否发射」，不是阈值）：
        // CPU 要同时过相对 5% 与噪声地板 0.05pp；retained 用绝对 0.5MB（贴着 0 的数算百分比没有意义）。
        bool cpuRegressed = cpuDeltaPct > RegressionMaxPct && cpuDeltaPp > CpuNoiseFloorPct;
        bool retainedRegressed = retainedDelta > RetainedGrowthMaxMb;
        Check(ref failures, "perf-relative-gated", !(cpuRegressed || retainedRegressed),
            $"cpu delta={Delta(cpuDeltaPp, "0.000")}pp ({Delta(cpuDeltaPct, "0.0")}%)"
            + $" retained delta={Delta(retainedDelta, "0.000")}MB premise=matched");
        return failures;
    }

    /// <summary>相对诊断行（RFC-40 §4.1）：基线值、本次值、差值、相对百分比，以及「若按旧语义会不会判红」。</summary>
    private static void RelativeDiagnostic(JsonObject? baseline, double cpuPct, double wsMb,
        double firstFrameMs, double retainedMb)
    {
        if (baseline is null)
        {
            Emit("CHECK-DISPLAY perf-relative-diagnostic skipped reason=baseline-missing");
            return;
        }

        double bCpu = FieldNum(baseline, "m-02", "cpu_idle_pct");
        double bWs = FieldNum(baseline, "m-04", "private_ws_mb");
        double bFirst = FieldNum(baseline, "m-03", "first_frame_ms");
        double bRetained = FieldNum(baseline, "d-14d", "retained_heap_mb");
        double cpuDeltaPp = cpuPct - bCpu;
        double cpuDeltaPct = bCpu > 0 ? cpuDeltaPp * 100 / bCpu : 0;
        double retainedDelta = retainedMb - bRetained;
        // 「旧语义」= 同一台机器（旧实现的 sameEnv **只比 env.cpu 的 CPU 型号**）+ CPU 相对+5% 且过地板，
        // 或 retained 超 0.5MB。基线没有 premise 时（RFC-40 之前的文件，正是负例 1 的状态）必须回落到
        // env.cpu 才能忠实模拟旧规则 —— 拿 premise.machine 比会得到 machine=changed，把「旧语义下会红」
        // 误报成「不会红」，这条诊断就失去它存在的意义（RFC-40 §3.4 第 3 条）。
        bool sameMachine = MachineTag() == BaselineMachine(baseline);
        bool wouldFail = sameMachine
            && ((cpuDeltaPct > RegressionMaxPct && cpuDeltaPp > CpuNoiseFloorPct)
                || retainedDelta > RetainedGrowthMaxMb);
        Emit("CHECK-DISPLAY perf-relative-diagnostic"
            + $" cpu baseline={Fmt(bCpu)} now={Fmt(cpuPct)}"
            + $" delta={Delta(cpuDeltaPp, "0.000")}pp ({Delta(cpuDeltaPct, "0.0")}%) floor={Fmt(CpuNoiseFloorPct)}pp"
            + $" ws baseline={Fmt(bWs)} now={Fmt(wsMb)}"
            + $" first baseline={Fmt(bFirst)} now={Fmt(firstFrameMs)}ms"
            + $" retained baseline={Fmt(bRetained)} now={Fmt(retainedMb)} delta={Delta(retainedDelta, "0.000")}MB"
            + $" machine={(sameMachine ? "same" : "changed")}"
            + $" verdict={(wouldFail ? "would-fail-if-gated" : "would-pass-if-gated")}");
    }

    /// <summary>读基线文件；返回 null 时 <paramref name="problem"/> 是形状门的 reason 码。</summary>
    internal static JsonObject? ReadBaseline(string path, out string problem)
    {
        problem = "baseline-unreadable";
        if (!File.Exists(path))
        {
            problem = "baseline-missing";
            return null;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                return null;
            }

            problem = string.Empty;
            return root;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            problem = "baseline-unreadable " + ex.GetType().Name;
            return null;
        }
    }

    /// <summary>
    /// 基线形状（§3.1 第 3 条，硬门）：文件在不在、premise 在不在、schema 对不对、必需数值字段在不在。
    /// 判的是**仓库状态**，同一分钟里跑十次答案都一样 —— 所以它红永远是「仓库坏了」，不是「机器忙」。
    /// </summary>
    internal static (bool Ok, string Detail) ShapeVerdict(JsonObject? baseline, string problem, string path,
        params (string Section, string Field)[] required)
    {
        string file = DisplayPath(path);
        if (baseline is null)
        {
            return (false, $"reason={problem} file={file} (先跑 --write-baseline --note \"…\")");
        }

        if (baseline["premise"] is not JsonObject premise)
        {
            return (false, $"reason=premise-missing file={file} (重钉一次基线即写入前提)");
        }

        if (IntField(premise, "schema") != PremiseSchema)
        {
            return (false, $"reason=premise-schema schema={Text(premise, "schema")}"
                + $" want={PremiseSchema.ToString(CultureInfo.InvariantCulture)} file={file}");
        }

        var missing = new List<string>();
        foreach ((string section, string field) in required)
        {
            if (!HasFieldNum(baseline, section, field))
            {
                missing.Add(section + "." + field);
            }
        }

        return missing.Count == 0
            ? (true, $"file={file} premise=present schema={PremiseSchema.ToString(CultureInfo.InvariantCulture)}")
            : (false, $"reason=numbers-missing fields={string.Join(",", missing)} file={file}");
    }
}
