// --perftest / --residenttest 共用的**前提**半边（RFC-40 §3.2）：入档、读数、逐字段比较。
//
// 为什么单独一个文件：PerfGate.Baseline.cs 放判据（形状门 / 相对诊断 / 前提门三件的判定），
// 本文件放「前提是什么、从哪来、怎么比」。两者寿命不同 —— 判据的阈值与行形随 RFC 改，
// 前提的字段随机器与花名册改。D-03c 的 400 行上限也在这里：两份判定 + 一份前提挤在一个文件里
// 必然越线（实测 429 > 400），按职责拆而不是删诊断。
//
// 常驻侧（ResidentPerfProbe.Baseline.cs）原样复用本文件的全部纯计算：两份基线若是两套写法，
// 下一次假红只需要换一个入口就能绕过。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace PetDock.Hosting;

internal static partial class PerfGate
{
    /// <summary>前提结构的版本号（RFC-40 §3.2）：只做守护、不参与比较。</summary>
    internal const int PremiseSchema = 1;

    /// <summary>机器三段（§3.2 的 machine）：CPU 型号 | 核数 | 系统版本。三段全等才算同环境。</summary>
    internal static string MachineTag() => CpuModel()
        + "|" + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)
        + "|" + Environment.OSVersion;

    /// <summary>
    /// 帧率档的入档形态：**目标周期**（33.3ms → 33.3ms），不带瞬时档位。
    ///
    /// 与 RFC-40 §3.2 表格里「`perf-throttle-idle tier=… target=…`，完全相等」的出入（以源码为准，见 t8 output）：
    /// 实测一个 10s 采样窗内档位在 active ↔ idle 之间反复切换（`changes=120`，见 perf-throttle-idle 行），
    /// 所以**瞬时档位在同一个进程内都不稳定**，拿它做「完全相等」的前提会把门永久置为 skipped——
    /// 而「慢性变胖的早期信号」正是这条门存在的理由（RFC-40 §3.1）。真正决定读数可比性的是目标周期
    /// （它来自设置与节流策略，跨运行稳定），因此入档的与比较的都取 target；瞬时档位仍逐次打在
    /// `CHECK-DISPLAY perf-throttle-idle` 行上供人看。
    /// </summary>
    internal static string FpsTier(LoopReadings loop)
        => loop.TargetMs.ToString("0.0", CultureInfo.InvariantCulture) + "ms";

    /// <summary>
    /// 花名册（§3.2 的 roster）：插件根（仓库相对路径）+ 根下带清单的目录数。
    /// 判据与 PluginHost.Discover 同形（非递归、只认目录与清单文件存在），于是这个数就是
    /// 「名册上有几个目录」，不是另算一份口径（RFC-40 §6.4 的取值方式以本次实现为准）。
    /// </summary>
    internal static string RosterTag()
    {
        string? root = PluginHost.ProbePluginsRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return "default:0";
        }

        int dirs = 0;
        try
        {
            foreach (string dir in Directory.GetDirectories(root))
            {
                if (File.Exists(Path.Combine(dir, ManifestValidator.ManifestFileName)))
                {
                    dirs++;
                }
            }
        }
        catch (IOException)
        {
            // 目录读不了就照 0 记：前提里的数是「这条读法读到的」，不补也不估。
        }

        return RootTag(root) + ":" + dirs.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>插件根的入档形态：仓库内的相对路径优先，仓库外取尾两段（绝对路径不该进基线文件）。</summary>
    private static string RootTag(string root)
    {
        string full;
        try
        {
            full = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "unknown";
        }

        string rel;
        try
        {
            rel = Path.GetRelativePath(RepoRoot(), full);
        }
        catch (ArgumentException)
        {
            rel = string.Empty;
        }

        if (rel.Length > 0 && !rel.StartsWith("..", StringComparison.Ordinal))
        {
            return rel.Replace('\\', '/');
        }

        string[] parts = full.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^2] + "/" + parts[^1] : "unknown";
    }

    /// <summary>写基线时的前提对象（§3.2：schema + 七个比较字段）。</summary>
    internal static JsonObject BuildPremise(double idleTotalPct, LoopReadings loop, int seconds, int warmupMs) => new()
    {
        ["schema"] = PremiseSchema,
        ["machine"] = MachineTag(),
        ["idle_total_pct"] = Math.Round(idleTotalPct, 3),
        // ambient_band_pp 是**判据的阈值**，必须与判据同源：不是命令行参数、不是 note 散文。
        ["ambient_band_pp"] = AmbientBandPp,
        ["fps_tier"] = FpsTier(loop),
        ["roster"] = RosterTag(),
        ["seconds"] = seconds,
        ["warmup_ms"] = warmupMs,
    };

    /// <summary>
    /// 前提比较（§3.2）：七个比较字段逐条比，返回**每个不符字段一项**的说明；空列表 = 前提全等。
    /// 每个字段各占一项而不是只报第一个：多字段同时不符时（换机器 + 换花名册），
    /// 只报第一个会让人修完一处再跑一趟才发现第二处。
    /// </summary>
    internal static List<string> PremiseMismatches(JsonObject? premise, double idleTotalPct,
        LoopReadings loop, int seconds, int warmupMs)
    {
        var bad = new List<string>();
        if (premise is null)
        {
            return bad;
        }

        string machineBase = Text(premise, "machine");
        string machineNow = MachineTag();
        if (machineBase != machineNow)
        {
            bad.Add($"field=machine baseline={machineBase} now={machineNow}");
        }

        // 负载不比相等，比「不超过基线 + 允许上浮」：空载水平只会往上涨的时候多，回不到安静时刻是常态。
        double idleBase = FieldNum(premise, string.Empty, "idle_total_pct");
        double band = FieldNum(premise, string.Empty, "ambient_band_pp");
        if (idleTotalPct > idleBase + band)
        {
            bad.Add($"field=ambient baseline={Fmt(idleBase)} band={Fmt(band)} now={Fmt(idleTotalPct)}");
        }

        string tierBase = Text(premise, "fps_tier");
        string tierNow = FpsTier(loop);
        if (tierBase != tierNow)
        {
            bad.Add($"field=fps_tier baseline={tierBase} now={tierNow}");
        }

        string rosterBase = Text(premise, "roster");
        string rosterNow = RosterTag();
        if (rosterBase != rosterNow)
        {
            bad.Add($"field=roster baseline={rosterBase} now={rosterNow}");
        }

        if (IntField(premise, "seconds") != seconds)
        {
            bad.Add($"field=seconds baseline={Text(premise, "seconds")}"
                + $" now={seconds.ToString(CultureInfo.InvariantCulture)}");
        }

        if (IntField(premise, "warmup_ms") != warmupMs)
        {
            bad.Add($"field=warmup_ms baseline={Text(premise, "warmup_ms")}"
                + $" now={warmupMs.ToString(CultureInfo.InvariantCulture)}");
        }

        return bad;
    }

    /// <summary>
    /// 基线自己记的机器身份，按**旧语义**取：有 premise 时用 premise.machine（三段），
    /// 没有时回落到 env.cpu（旧实现的 sameEnv 只比 CPU 型号）。诊断行模拟旧语义时必须走这条，
    /// 否则旧文件会被读成「换了机器」，于是「旧语义下会红」的读数被误报成「不会红」。
    /// </summary>
    internal static string BaselineMachine(JsonObject? baseline)
    {
        if (baseline?["premise"] is JsonObject premise && premise["machine"] is not null)
        {
            return Text(premise, "machine");
        }

        string cpu = baseline?["env"]?["cpu"] is JsonValue value && value.TryGetValue(out string? s) && s is not null
            ? s
            : "(missing)";
        // 旧口径只有 CPU 型号：补上现在的核数与系统版本，让它与 MachineTag() 可比。
        return cpu + "|" + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)
            + "|" + Environment.OSVersion;
    }

    /// <summary>读一个数值字段；缺字段或类型不对 → 0（「在不在」由形状门单独判，这里不重复报红）。</summary>
    internal static double FieldNum(JsonObject? node, string section, string field)
    {
        try
        {
            JsonNode? leaf = section.Length == 0 ? node?[field] : node?[section]?[field];
            return leaf is JsonValue value && value.TryGetValue(out double d) ? d : 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return 0;
        }
    }

    internal static bool HasFieldNum(JsonObject? node, string section, string field)
    {
        try
        {
            JsonNode? leaf = node?[section]?[field];
            return leaf is JsonValue value && value.TryGetValue(out double _);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    internal static int IntField(JsonObject? node, string key)
    {
        try
        {
            return node?[key] is JsonValue value && value.TryGetValue(out int v) ? v : int.MinValue;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return int.MinValue;
        }
    }

    /// <summary>字段的展示形态：字符串字段给原文，数值/其他给 JSON 字面量。</summary>
    internal static string Text(JsonObject? node, string key)
    {
        JsonNode? value = node?[key];
        if (value is null)
        {
            return "(missing)";
        }

        if (value is JsonValue jv && jv.TryGetValue(out string? s) && s is not null)
        {
            return s;
        }

        return value.ToJsonString();
    }

    /// <summary>差值的带符号形态（+0.052 / -1.3）：正号是给人看的，不是给解析器看的。</summary>
    internal static string Delta(double v, string format)
        => (v > 0 ? "+" : string.Empty) + v.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>仓库相对路径（证据里读到的路径必须能直接抄进命令）。</summary>
    internal static string DisplayPath(string path)
    {
        try
        {
            string rel = Path.GetRelativePath(RepoRoot(), path);
            return rel.StartsWith("..", StringComparison.Ordinal) ? path : rel.Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
