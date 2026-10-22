// 诊断包各节的文本装配（RFC-17 §3.2）。
//
// 为什么不并回 DiagnosticBundle.cs：D-03c 的单文件 400 行上限。
// 本文件只做「读数 → 文本」，不碰文件系统、不碰 zip。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PetDock.Hosting;

/// <summary>诊断包各节的文本装配。</summary>
internal static class DiagnosticSections
{
    /// <summary>人读摘要。帧预算自检行逐字照抄（不重排、不改写）。</summary>
    public static string BuildSummary(DiagnosticReadings readings, RedactRules rules)
    {
        var sb = new StringBuilder();
        sb.Append("PetDock 诊断摘要").Append(Environment.NewLine);
        sb.Append("程序版本: ").Append(readings.ProgramVersion).Append(Environment.NewLine);
        sb.Append("导出时刻: ").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append(Environment.NewLine);
        sb.Append("运行时长: ").Append(FormatMs(readings.UptimeMs)).Append(Environment.NewLine);
        sb.Append("内存: ").Append(readings.ProcessMemory).Append(Environment.NewLine);
        if (readings.Instances.Count == 0)
        {
            // 空态不写数字：没有宠物就没有帧读数，写 0 会被读成「测过是 0」。
            sb.Append("帧泵: (当前没有宠物实例，无帧读数)").Append(Environment.NewLine);
        }
        else
        {
            sb.Append("帧泵: frames=").Append(readings.FrameCount.ToString(CultureInfo.InvariantCulture))
                .Append(" avg=").Append(readings.AvgFrameMs.ToString("0.0", CultureInfo.InvariantCulture)).Append("ms")
                .Append(" (累计平均，非 60 秒采样窗口)")
                .Append(" max=").Append(readings.MaxFrameMs.ToString("0.0", CultureInfo.InvariantCulture)).Append("ms")
                .Append(" over-budget=").Append(readings.OverBudgetFrames.ToString(CultureInfo.InvariantCulture))
                .Append(" target=").Append(readings.TargetFrameMs.ToString("0.0", CultureInfo.InvariantCulture)).Append("ms")
                .Append(Environment.NewLine);
        }

        sb.Append("帧预算自检: ").Append(readings.FrameBudgetLine).Append(Environment.NewLine);

        // RFC-21 §3.4：档位与提交计数。没接帧循环时明说「未接」，不写 0 ——
        // 「没接上」与「一帧都没提交」在数字上看起来一样，在结论上完全不同。
        if (readings.HasThrottle)
        {
            sb.Append("节流档位: tier=").Append(readings.ThrottleTier)
                .Append(" ticks=").Append(readings.ThrottleTicks.ToString(CultureInfo.InvariantCulture))
                .Append(" submitted=").Append(readings.ThrottleSubmitted.ToString(CultureInfo.InvariantCulture))
                .Append(" skipped=").Append(readings.ThrottleSkipped.ToString(CultureInfo.InvariantCulture))
                .Append(Environment.NewLine);
        }
        else
        {
            sb.Append("节流档位: (未接帧循环)").Append(Environment.NewLine);
        }

        sb.Append("插件 (").Append(readings.Plugins.Count.ToString(CultureInfo.InvariantCulture)).Append("):").Append(Environment.NewLine);
        if (readings.RosterUnavailable)
        {
            sb.Append("  (插件花名册暂不可用（宿主未响应），包内该节为空)").Append(Environment.NewLine);
        }
        else if (readings.Plugins.Count == 0)
        {
            sb.Append("  (无插件)").Append(Environment.NewLine);
        }
        else
        {
            foreach (DiagnosticPluginRow row in readings.Plugins)
            {
                sb.Append("  ").Append(row.Id).Append(" ").Append(row.Name)
                    .Append(" v").Append(row.Version)
                    .Append(" tier=").Append(row.Tier.ToString(CultureInfo.InvariantCulture))
                    .Append(" state=").Append(row.State)
                    .Append(" granted=").Append(row.Granted.ToString(CultureInfo.InvariantCulture)).Append(Environment.NewLine);
            }
        }

        sb.Append("宠物实例 (").Append(readings.Instances.Count.ToString(CultureInfo.InvariantCulture)).Append("):").Append(Environment.NewLine);
        if (readings.Instances.Count == 0)
        {
            sb.Append("  (当前没有可显示的宠物 —— 不填任何占位数字)").Append(Environment.NewLine);
        }
        else
        {
            foreach (DiagnosticInstanceRow row in readings.Instances)
            {
                sb.Append("  ").Append(row.Id).Append(" state=").Append(row.State)
                    .Append(" tier=").Append(row.Tier)
                    .Append(" frames=").Append(row.Frames.ToString(CultureInfo.InvariantCulture))
                    .Append(" drops=").Append(row.Drops.ToString(CultureInfo.InvariantCulture)).Append(Environment.NewLine);
            }
        }

        sb.Append("沙箱事件（本次会话内计数）: audit=")
            .Append(readings.AuditCount.ToString(CultureInfo.InvariantCulture))
            .Append(" denied=").Append(readings.DeniedCount.ToString(CultureInfo.InvariantCulture))
            .Append(" budget=").Append(readings.BudgetCount.ToString(CultureInfo.InvariantCulture))
            .Append(" guest-fault=").Append(readings.GuestFaultCount.ToString(CultureInfo.InvariantCulture))
            .Append(Environment.NewLine);

        // 审计流水（RFC-20 §3.4）：两组数字并排 —— 上面是「本次会话」，这里是「跨进程总量」。
        // 读者不该把会话计数误读成历史总量，所以两个口径必须同时在场。
        DiagnosticTrailSummary trail = readings.Trail ?? DiagnosticTrailSummary.Unavailable;
        sb.Append("审计流水（跨进程）: ");
        if (!trail.Available)
        {
            sb.Append("(不可用) ").Append(trail.Note)
                .Append(" —— 审计一节回退为环形缓冲最近事件（非全量）")
                .Append(Environment.NewLine);
        }
        else
        {
            sb.Append("rows=").Append(trail.Rows.ToString(CultureInfo.InvariantCulture))
                .Append(" files=").Append(trail.Files.ToString(CultureInfo.InvariantCulture))
                .Append(" incomplete=").Append(trail.Incomplete.ToString(CultureInfo.InvariantCulture))
                .Append(" dropped=").Append(trail.Dropped.ToString(CultureInfo.InvariantCulture));
            if (trail.Incomplete > 0)
            {
                sb.Append(" (含未完成文件：上次进程被强杀)");
            }

            sb.Append(Environment.NewLine);
        }

        return rules.Text(sb.ToString());
    }

    /// <summary>进程级读数（JSON）。</summary>
    public static string BuildRuntime(DiagnosticReadings readings, RedactRules rules)
    {
        var doc = new JsonObject
        {
            ["schemaVersion"] = DiagnosticBundle.SchemaVersion,
            ["agent_id"] = "host",
            ["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["programVersion"] = readings.ProgramVersion,
            ["uptimeMs"] = readings.UptimeMs,
            ["memory"] = readings.ProcessMemory,
            ["memoryScope"] = "process",
            ["frameCount"] = readings.FrameCount,
            ["avgFrameMs"] = Math.Round(readings.AvgFrameMs, 3),
            ["maxFrameMs"] = Math.Round(readings.MaxFrameMs, 3),
            ["frameTimingNote"] = "累计平均，不是 60 秒采样窗口的平均",
            ["overBudgetFrames"] = readings.OverBudgetFrames,
            ["frameElapsedMs"] = readings.FrameElapsedMs,
            ["targetFrameMs"] = Math.Round(readings.TargetFrameMs, 3),
            ["frameBudget"] = readings.FrameBudgetLine,
            // RFC-21 §3.4：档位与提交计数。没接帧循环时不写数字（null），
            // 写成 0 会让「没接上」和「一帧都没提交」看起来一样。
            ["throttleTier"] = readings.HasThrottle ? readings.ThrottleTier : null,
            ["throttleTicks"] = readings.HasThrottle ? readings.ThrottleTicks : (long?)null,
            ["throttleSubmitted"] = readings.HasThrottle ? readings.ThrottleSubmitted : (long?)null,
            ["throttleSkipped"] = readings.HasThrottle ? readings.ThrottleSkipped : (long?)null,
        };
        return rules.Text(doc.ToJsonString(Indented));
    }

    /// <summary>插件读数（JSON）。能力 id 必须落在 docs/14 §2 集合内。</summary>
    public static string BuildPlugins(DiagnosticReadings readings, RedactRules rules)
    {
        var array = new JsonArray();
        foreach (DiagnosticPluginRow row in readings.Plugins)
        {
            var item = new JsonObject
            {
                ["id"] = row.Id,
                ["name"] = row.Name,
                ["version"] = row.Version,
                ["tier"] = row.Tier,
                ["state"] = row.State,
                ["grantedHandles"] = row.Granted,
                ["declaredCapabilityIds"] = ToJsonArray(row.DeclaredCapabilityIds),
                ["grantedCapabilityIds"] = ToJsonArray(row.GrantedCapabilityIds),
                ["deniedCapabilityIds"] = ToJsonArray(row.DeniedCapabilityIds),
            };
            array.Add(item);
        }

        var doc = new JsonObject
        {
            ["schemaVersion"] = DiagnosticBundle.SchemaVersion,
            ["agent_id"] = "host",
            ["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["rosterUnavailable"] = readings.RosterUnavailable,
            ["plugins"] = array,
        };
        return rules.Text(doc.ToJsonString(Indented));
    }

    /// <summary>宠物实例读数（JSON）。</summary>
    public static string BuildInstances(DiagnosticReadings readings, RedactRules rules)
    {
        var array = new JsonArray();
        foreach (DiagnosticInstanceRow row in readings.Instances)
        {
            array.Add(new JsonObject
            {
                ["id"] = row.Id,
                ["sourceKind"] = row.SourceKind,
                ["state"] = row.State,
                ["tier"] = row.Tier,
                ["frames"] = row.Frames,
                ["drops"] = row.Drops,
                ["ticks"] = row.Ticks,
                ["brainFaults"] = row.BrainFaults,
                ["lastFailure"] = row.LastFailure,
                ["transitions"] = row.Transitions,
            });
        }

        var doc = new JsonObject
        {
            ["schemaVersion"] = DiagnosticBundle.SchemaVersion,
            ["agent_id"] = "host",
            ["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["instances"] = array,
        };
        return rules.Text(doc.ToJsonString(Indented));
    }

    /// <summary>审计事件流水（每行一个 JSON）。时间戳来自跨进程流水（RFC-20）。</summary>
    public static string BuildAudit(DiagnosticReadings readings, RedactRules rules)
    {
        var sb = new StringBuilder();
        foreach (DiagnosticAuditRow row in readings.Audit)
        {
            var item = new JsonObject
            {
                ["pluginId"] = row.PluginId,
                ["kind"] = row.Kind,
                ["detail"] = rules.AuditText(row.Detail),
            };

            // 回退到环形缓冲时事件没有时间戳：那一格留空，而不是编一个。
            if (!string.IsNullOrEmpty(row.Timestamp))
            {
                item["timestamp"] = row.Timestamp;
            }

            sb.Append(item.ToJsonString()).Append(Environment.NewLine);
        }

        return sb.ToString();
    }

    /// <summary>包清单：schemaVersion、agent_id、timestamp、程序版本、逐文件 sha256。</summary>
    public static string BuildManifest(
        IReadOnlyList<(string Path, byte[] Bytes)> entries,
        DiagnosticReadings readings,
        int incompleteLogCount,
        RedactRules rules)
    {
        var files = new JsonArray();
        foreach ((string path, byte[] bytes) in entries)
        {
            files.Add(new JsonObject
            {
                ["path"] = path,
                ["bytes"] = bytes.Length,
                ["sha256"] = Sha256Hex(bytes),
            });
        }

        var doc = new JsonObject
        {
            ["schemaVersion"] = DiagnosticBundle.SchemaVersion,
            ["agent_id"] = "host",
            ["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["programVersion"] = readings.ProgramVersion,
            ["incompleteLogFiles"] = incompleteLogCount,
            ["note"] = "sha256 用于发现传输损坏，不是签名，不防篡改",
            ["files"] = files,
        };
        return rules.Text(doc.ToJsonString(Indented));
    }

    /// <summary>能力 id 数组 → JSON 数组（不合法的 id 换成占位符，绝不原样写出）。</summary>
    private static JsonArray ToJsonArray(IReadOnlyList<string> ids)
    {
        var array = new JsonArray();
        foreach (string id in ids)
        {
            // 用 JsonValue.Create 而不是 array.Add(id)：泛型 Add<T> 会把字符串包成
            // JsonValueCustomized<string>，写出时要求 JsonSerializerOptions 带 TypeInfoResolver，
            // 否则运行期抛 InvalidOperationException（与 ConsentStore.cs 同一个坑，已实测）。
            array.Add(JsonValue.Create(
                RedactRules.IsAllowedCapabilityId(id) ? id : RedactRules.CapabilityPlaceholder)!);
        }

        return array;
    }

    /// <summary>字节的 SHA-256（小写十六进制）。manifest 的逐文件校验值用它算。
    /// 为什么不复用 ManifestValidator.NormalizedHash：那个是给 manifest **归一化 JSON**
    /// 算的（键序无关），诊断包要的是「文件字节本身」的校验值，两者语义不同。</summary>
    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>缩进输出用的选项（只读一次，避开重复造实例）。</summary>
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string FormatMs(long ms) =>
        (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
