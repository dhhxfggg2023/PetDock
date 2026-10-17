// 重放驱动与 core 之间的**唯一**接缝（t1 §8.5：t3 只消费 core 的公开面，不碰 core 一行）。
//
// 本文件解决一个具体问题：t2 的 M4 新面（EventBus / CapabilityBroker / WorldProbe / 目录守门）
// 今天还不存在，而重放测试必须今天就能交付**真判据**，不能等。于是把驱动分成两层：
//
//   层 1（今天就能跑，且跑的是**真的 core 代码**）：
//     - `HostArbiter.Sorted`      —— docs/11:162 的仲裁序（public static 纯函数，core 已有）
//     - `HostEventDispatch`       —— 事件落地与投递序（public 类，core 已有；M2 的记账口）
//     - `PetInstance` 的 tick 面  —— TickBrain / NextDeltaMs / EnforceSessionBudget / 计数
//       （走 roster → BuildInstance → AttachAndStart → Start，与金图同一条公开链）
//   层 2（t2 落地后才可执行）：EventBus 的五种 dispatch、目录守门、CapabilityBroker、可注入 brain。
//     层 2 的判据**已全部写好**，但会以 SKIP + 具名原因输出（§2.6：SKIP 不是 PASS，不许计绿），
//     并在 CoverageLedger 里逐条登记「未覆盖及原因」。
//
// 「可测的部分」这个措辞（验收④）指的就是这两层的分界。层 1 的判据真跑真红；层 2 的判据不假装。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.EventReplay.Tests;

/// <summary>一次重放对 core 的驱动结果（不复用：每次 run 都新建一个，避免状态串场）。</summary>
public sealed class CoreDrive : IDisposable
{
    private readonly PetInstance? _instance;
    private readonly List<string> _notes = new();

    private CoreDrive(PetInstance? instance, HostEventDispatch dispatch)
    {
        _instance = instance;
        Dispatch = dispatch;
    }

    /// <summary>事件落地口（层 1：HostEventDispatch 是 public 类，可直接 new）。</summary>
    public HostEventDispatch Dispatch { get; }

    /// <summary>本驱动实际碰到的 core 类型名（写进规范行，作为「驱动的是真 core 不是替身」的证据）。</summary>
    public IReadOnlyList<string> DrivenSurface => _notes;

    /// <summary>装配一个驱动。roster 取不到视图实现时 <see cref="TickAvailable"/> = false，
    /// 该 run 仍会跑层 1 的纯函数与账记面（它们不依赖实例）—— 不因为装配失败就整条 SKIP。</summary>
    public static CoreDrive Create()
    {
        var dispatch = new HostEventDispatch();
        PetInstance? instance = null;
        var notes = new List<string> { "HostEventDispatch", "HostArbiter.Sorted" };
        string note = string.Empty;
        try
        {
            PetSurfaceRoster roster = PetSurfaceRoster.Discover(PluginsRootForDrive());
            instance = roster.BuildInstance("eventreplay");
            if (instance is not null)
            {
                instance.AttachAndStart(128, 128, 1.0);
                instance.Start();
                notes.Add("PetInstance(TickBrain/NextDeltaMs/EnforceSessionBudget)");
            }
            else
            {
                note = $"instance-not-built root={PluginsRootForDrive() ?? "(null)"} roster={roster.Describe()}";
            }
        }
        catch (Exception error)
        {
            // 装配不了实例不是判据失败：层 1 的纯函数面照跑；如实记一行，run 输出里可见。
            note = $"instance-unavailable {error.GetType().Name}: {error.Message.Split('\n')[0]}";
        }

        var drive = new CoreDrive(instance, dispatch) { KernelLogNote = note };
        drive._notes.AddRange(notes);
        if (note.Length > 0)
        {
            // 装配失败时留一行可见事实（判据据此给 SKIP + 具名原因，不是静默）。
            Console.WriteLine($"EVENT-CHECK Drive.InstanceAvailable INFO {note}");
        }

        return drive;
    }

    /// <summary>
    /// 本驱动要扫的插件根：**仓库的 `packages/plugins`**，不是 `PluginHost.ProbePluginsRoot()` 的答案。
    ///
    /// 为什么不能直接用 `ProbePluginsRoot()`：它从 `AppContext.BaseDirectory` 向上找第一个名为
    /// `plugins` 的目录，而本工程位于 `tests/fixtures/PetDock.EventReplay.Tests/bin/...`——
    /// 向上第三级就撞上 `tests/fixtures/plugins/`（M1 的 3 个探针插件夹具，全是 tier1 无视图实现），
    /// 于是它**遮蔽**了真正的 `packages/plugins`。本会话实测：用前者 → `dirs=3 records=0 view=(none)`
    /// → `BuildInstance` 返回 null → 三条判据 SKIP，看起来像「注入面没做好」，实际是扫错了目录。
    /// 这是测试宿主的路径语义问题，不是内核缺陷：内核在 `dotnet exec PetDock.Core.dll` 下扫得对
    /// （`dirs=6 records=4 view=PetDock.Builtins.SpriteSeqView`）。
    /// 环境变量仍优先（与生产同一优先级），便于 CI 或用例临时换根。
    /// </summary>
    private static string? PluginsRootForDrive()
    {
        string? explicitRoot = PluginHost.ProbePluginsRoot();
        if (explicitRoot is not null
            && explicitRoot.Replace('\\', '/').Contains("/packages/plugins", StringComparison.Ordinal))
        {
            return explicitRoot;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "packages", "plugins");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return explicitRoot;
    }

    /// <summary>实例装配失败时的原因（空串 = 装上了）。</summary>
    public string KernelLogNote { get; private set; } = string.Empty;

    /// <summary>实例是否可用（tick 面可测 = 它非 null）。</summary>
    public bool TickAvailable => _instance is not null;

    /// <summary>一次 tick：把夹具的 world / dt / tod 原样交给 core（t1 §1.3 第 5 步的落点）。</summary>
    public void Tick(int dtMs, int nowMs, WorldSnapshot world)
    {
        if (_instance is null)
        {
            return;
        }

        _instance.TickBrain(_instance.NextDeltaMs(nowMs), world);
    }

    /// <summary>把夹具的指令交给 core 的仲裁器（这是**真的** HostArbiter，不是替身）。</summary>
    public IReadOnlyList<BrainDirective> Apply(IReadOnlyList<BrainDirective> directives)
    {
        if (_instance is null)
        {
            return HostArbiter.Sorted(directives);
        }

        IReadOnlyList<BrainDirective> ordered = HostArbiter.Sorted(directives);
        _instance.Arbiter.Apply(directives);
        return ordered;
    }

    /// <summary>把一条事件投进 core 的落地口（层 1 的记账面）。<paramref name="atMs"/> 由夹具给，
    /// **不是墙钟** —— 这是 §8.2 规则 2 在驱动侧的落点。</summary>
    public void Publish(string type, JsonObject? payload, long atMs) =>
        Dispatch.Publish(new PetEvent(type, atMs, payload));

    /// <summary>实例的 tick 计数（层 1 的可观察面）。</summary>
    public long Ticks => _instance?.Ticks ?? -1;

    /// <summary>已发事件数（core 自己的计数，不是我这边数的）。</summary>
    public int LoggedEvents => Dispatch.Log.Count;

    /// <summary>该类型在 core 的日志里出现了几次（core 自己的 `CountOf`）。</summary>
    public int CountOf(string type) => Dispatch.CountOf(type);

    /// <summary>强制会话预算（docs/11:260 的时间触发面）。</summary>
    public bool EnforceSessionBudget(int nowMs) => _instance?.EnforceSessionBudget(nowMs) ?? false;

    /// <summary>§8.4 N4 / §6.5 的注入面：把一只受控 brain 装进已装配的实例。
    /// 走 core 的公开方法 <c>PetInstance.InstallBrain</c>（t10 落地），**不用反射**：
    /// 反射会让判据变成「测试自己造的场景」，这里调用的就是宿主热重载 brain 要走的那条路。
    /// 返回 false = 实例不可换装（已 disposed）或实例未装配 —— 调用方据此断言，而不是静默继续。</summary>
    public bool InstallBrain(IBrain brain) => _instance?.InstallBrain(brain) ?? false;

    /// <summary>宿主征询接缝（§6.5）：装一个在 <c>host.suspend</c> 的 bail 阶段投票的监听者。
    /// core 侧 <c>HostArbiter.SuspendVeto</c> 是 public setter，这里只是把它包成可读的形状。</summary>
    public bool InstallSuspendVeto(Func<string, bool> veto)
    {
        if (_instance is null)
        {
            return false;
        }

        _instance.Arbiter.SuspendVeto = reason => veto(reason);
        return true;
    }

    /// <summary>实例当前状态的可判定面（§6.5 两个子断言都要读它）。</summary>
    public InstanceState? State => _instance?.State;

    /// <summary>brain 故障计数（§8.4 N4 判据读它）。</summary>
    public int BrainFaults => _instance?.BrainFaults ?? -1;

    /// <summary>是否已降级（§8.4 N4 判据读它）。</summary>
    public bool BrainDegraded => _instance?.BrainDegraded ?? false;

    /// <summary>仲裁器（public 面：Applied / Refused 计数、DirectiveLine、SuspendVeto、WindowMover）。</summary>
    public HostArbiter? Arbiter => _instance?.Arbiter;

    public void Dispose() => _instance?.Dispose();
}

/// <summary>夹具的 FixtureDirective ↔ core 的 <see cref="BrainDirective"/> 的双向桥。
/// 名字与 core 的枚举逐字一致（setAnimation / moveTo / emit / requestCapability / suspend / resume）。</summary>
public static class DirectiveBridge
{
    /// <summary>夹具指令 → core 指令（不认识的 kind 抛 —— 夹具写错必须看得见）。</summary>
    public static BrainDirective ToCore(FixtureDirective directive) => directive.Kind switch
    {
        "setAnimation" => BrainDirective.SetAnimation(
            directive.Clip ?? throw new ArgumentException("setAnimation 缺 clip", nameof(directive)),
            directive.Restart),
        "moveTo" => BrainDirective.MoveTo(ToPoint(directive)),
        "emit" => BrainDirective.Emit(new PetEvent(
            directive.EventType ?? throw new ArgumentException("emit 缺 eventType", nameof(directive)), 0)),
        "requestCapability" => BrainDirective.RequestCapability(
            directive.Capability ?? throw new ArgumentException("requestCapability 缺 capability", nameof(directive)),
            directive.Reason ?? string.Empty),
        "suspend" => BrainDirective.Suspend(directive.Reason ?? string.Empty),
        "resume" => BrainDirective.Resume(directive.Reason ?? string.Empty),
        _ => throw new ArgumentOutOfRangeException(nameof(directive), directive.Kind, "表外指令 kind"),
    };

    /// <summary>core 指令 → 规范文本（与夹具侧同一个渲染函数：两侧用同一套口径才比得动，
    /// 「两份渲染」正是 Drift 的来源）。</summary>
    public static string Render(FixtureDirective directive)
    {
        var text = new StringBuilder(directive.Kind);
        if (directive.Clip is not null)
        {
            text.Append(" clip=").Append(directive.Clip).Append(" restart=").Append(Lower(directive.Restart));
        }

        if (directive.At is not null)
        {
            text.Append(" at=").Append(directive.At[0]).Append(',').Append(directive.At[1]);
        }

        if (directive.Capability is not null)
        {
            text.Append(" capability=").Append(directive.Capability);
        }

        if (directive.Reason is not null)
        {
            text.Append(" reason=").Append(directive.Reason);
        }

        if (directive.EventType is not null)
        {
            text.Append(" eventType=").Append(directive.EventType);
        }

        return text.ToString();
    }

    /// <summary>core 指令 → 规范文本（与 <see cref="Render(FixtureDirective)"/> 逐字同构）。</summary>
    public static string Render(BrainDirective directive)
    {
        var text = new StringBuilder(directive.Kind.ToString().Length == 0
            ? string.Empty
            : LowerKind(directive.Kind));
        switch (directive.Kind)
        {
            case DirectiveKind.SetAnimation:
                text.Append(" clip=").Append(directive.Clip).Append(" restart=").Append(Lower(directive.Restart));
                break;
            case DirectiveKind.MoveTo:
                PetPoint at = directive.At!.Value;
                text.Append(" at=").Append(Fmt(at.X)).Append(',').Append(Fmt(at.Y));
                break;
            case DirectiveKind.RequestCapability:
                text.Append(" capability=").Append(directive.Capability);
                break;
            case DirectiveKind.Suspend:
            case DirectiveKind.Resume:
                text.Append(" reason=").Append(directive.Reason);
                break;
            case DirectiveKind.Emit:
                text.Append(" eventType=").Append(directive.Event?.Type);
                break;
            default:
                break;
        }

        return text.ToString();
    }

    /// <summary>core 的 DirectiveKind 名字（首字母小写 = 夹具与 docs/16 的拼法）。</summary>
    public static string LowerKind(DirectiveKind kind)
    {
        string name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    /// <summary>payload 的规范渲染（键按名字排序 = 同一份数据只有一个文本形态）。</summary>
    public static string RenderPayload(JsonObject? payload)
    {
        if (payload is null || payload.Count == 0)
        {
            return "-";
        }

        var keys = new List<string>();
        foreach (KeyValuePair<string, JsonNode?> pair in payload)
        {
            keys.Add(pair.Key);
        }

        keys.Sort(StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (string key in keys)
        {
            if (text.Length > 0)
            {
                text.Append(',');
            }

            text.Append(key).Append('=').Append(RenderValue(payload[key]));
        }

        return text.ToString();
    }

    private static string RenderValue(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            var parts = new List<string>();
            foreach (JsonNode? item in array)
            {
                parts.Add(RenderValue(item));
            }

            return string.Join("|", parts);
        }

        return node is null ? "null" : node.ToJsonString();
    }

    private static PetPoint ToPoint(FixtureDirective directive)
    {
        if (directive.At is null || directive.At.Length < 2)
        {
            throw new ArgumentException("moveTo 缺目标点", nameof(directive));
        }

        return new PetPoint(directive.At[0], directive.At[1]);
    }

    private static string Fmt(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Lower(bool value) => value ? "true" : "false";
}
