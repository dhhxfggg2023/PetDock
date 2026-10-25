// docs/11:162 的指令仲裁器（宿主侧）。
// 逐字规则：**指令即返回值**；多条指令冲突时按
//   [suspend > setAnimation > moveTo > emit > requestCapability]
// 的顺序裁决，且宿主可以最终拒绝 ——「这些优先级是 ABI 语义，不是事件级 overlay」。
//
// 实现方式（三个决定，都写在这里以免以后被改回去）：
//   1. 优先级数值就是 DirectiveKind 的枚举数值（见 src/PetRuntime/Brain.cs 文件头），
//      顺序**只在这一处**编码，仲裁器不重复写一遍数值表；
//   2. 排序用**插入排序（稳定）**：同档必须保持实现方给出的原顺序，否则同一 tick 两次可能
//      给出不同结果，直接破坏 docs/11:161 的幂等要求；
//   3. 本类任何方法都不抛：仲裁与落地属于宿主健康线（docs/11:220「宿主不可被插件拒绝」），
//      一条坏指令把宿主循环打穿就违背了这条。被拒绝的指令一律留计数与原因，不许静默吞掉
//      （docs/11:254「插件错误必须表达为 ABI 的返回值或异常，不许静默吞掉」）。
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Core.Sandbox;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

/// <summary>宿主征询回调：suspend 指令落地前先过 host.suspend 的 bail 派发（docs/16:41 逐字）。</summary>
public delegate bool SuspendVetoDelegate(string reason);

/// <summary>宿主窗口迁移回调：moveTo 指令由窗口持有者真移窗（契约 §6）。</summary>
public delegate bool WindowMoveDelegate(int x, int y);

/// <summary>指令落地输出行（契约 §6 DIRECTIVE 行）。</summary>
public delegate void DirectiveLineDelegate(string line);

/// <summary>docs/11:162 的指令仲裁器。</summary>
public sealed class HostArbiter
{
    private readonly PetInstance _instance;
    private readonly Dictionary<DirectiveKind, int> _applied = new();
    private readonly Dictionary<DirectiveKind, int> _refused = new();
    private int _moveToBatch;
    private int _moveToSeen;

    internal HostArbiter(PetInstance instance)
    {
        _instance = instance;
        Events = new HostEventDispatch();
    }

    /// <summary>已落地的指令按 kind 计数。</summary>
    public IReadOnlyDictionary<DirectiveKind, int> Applied => _applied;

    /// <summary>被宿主拒绝的指令按 kind 计数。</summary>
    public IReadOnlyDictionary<DirectiveKind, int> Refused => _refused;

    /// <summary>已授予的能力请求数（走 docs/14 §2 白名单判定）。</summary>
    public int GrantedCapabilities { get; private set; }

    /// <summary>emit 落地的事件口（判据读它证明事件真的发出去了）。</summary>
    public HostEventDispatch Events { get; }

    /// <summary>最近一次 moveTo 的目标（M2 记账；真实移动由窗口持有者执行，属 t3/t4）。</summary>
    public PetPoint? LastMoveTo { get; private set; }

    /// <summary>最近一次拒绝的原因文本（可判定面：为什么某条指令没落地）。</summary>
    public string LastRefusal { get; private set; } = "(none)";

    /// <summary>本实例收到的指令总数（含被拒的）。</summary>
    public int Received { get; private set; }

    /// <summary>宿主征询：suspend 落地前 bail 派发 host.suspend，返回 true = 插件否决。</summary>
    public SuspendVetoDelegate? SuspendVeto { get; set; }

    /// <summary>宿主窗口迁移：moveTo 由窗口持有者真移窗并回执成功与否。</summary>
    public WindowMoveDelegate? WindowMover { get; set; }

    /// <summary>能力裁决 broker（注入时钟；null 时维持 docs/14 白名单判定）。</summary>
    public CapabilityBroker? Capabilities { get; set; }

    /// <summary>指令落地输出行（契约 §6 DIRECTIVE 行格式；自检与常驻共用，每条指令一行）。</summary>
    public event DirectiveLineDelegate? DirectiveLine;

    /// <summary>逐字格式（契约 §6）：ok / refused 都输出；refused 带 why。</summary>
    private void EmitDirectiveLine(string line)
    {
        try { DirectiveLine?.Invoke(line); }
        catch (Exception error)
        {
            KernelLog.Write($"directive-line-failed {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// 落地一批指令。顺序已按 docs/11:162 排好；同批里的一条把实例挂起之后，
    /// 后续条目仍在同一次调用里落地（它们是同一 tick 的产物），但任何试图调用插件的条目
    /// 在挂起态下会被拒绝 —— 那正是「宿主可以最终拒绝」的落点。
    /// </summary>
    public void Apply(IReadOnlyList<BrainDirective> directives)
    {
        if (directives is null || directives.Count == 0)
        {
            return;
        }

        Received += directives.Count;
        var ordered = new List<BrainDirective>(directives);
        StableSortByPriority(ordered);
        // 契约 §5.1：同批多条 moveTo 全部落地（LastMoveTo 为后者），但 core.window.moved 只发一条。
        _moveToBatch = 0;
        _moveToSeen = 0;
        foreach (BrainDirective d in ordered)
        {
            if (d.Kind == DirectiveKind.MoveTo) _moveToBatch++;
        }

        foreach (BrainDirective directive in ordered)
        {
            bool ok;
            try
            {
                ok = ApplyOne(directive);
            }
            catch (Exception error)
            {
                LastRefusal = $"{directive.Kind} threw {error.GetType().Name}: {error.Message}";
                ok = false;
            }

            Bump(ok ? _applied : _refused, directive.Kind);
        }
    }

    /// <summary>宿主自身补发一条事件（如 docs/11:260 的 host.canceled）。</summary>
    public void NoteHostEvent(string type)
    {
        Events.Publish(PetEvent.Named(type));
        Bump(_applied, DirectiveKind.Emit);
    }

    private bool ApplyOne(BrainDirective directive)
    {
        switch (directive.Kind)
        {
            case DirectiveKind.Suspend:
                string reason = directive.Reason ?? "brain-suspend";
                // docs/16:41 逐字：挂起先过 host.suspend 的 bail 派发，插件可否决。
                if (SuspendVeto is not null && SuspendVeto(reason))
                {
                    LastRefusal = "suspend blocked by plugin";
                    EmitDirectiveLine($"DIRECTIVE suspend reason={reason} event=host.suspend blocked=true");
                    return false;
                }

                _instance.Suspend(reason);
                EmitDirectiveLine($"DIRECTIVE suspend reason={reason} event=host.suspend blocked=false");
                return true;

            case DirectiveKind.Resume:
                // 契约 §6：先发 host.resume 再改状态。
                Events.Publish(new PetEvent(EventCatalog.HostResume, PetEvent.NowMs(), new JsonObject
                {
                    ["reason"] = directive.Reason ?? "brain-resume",
                }));
                bool resumed = _instance.Resume(directive.Reason ?? "brain-resume");
                EmitDirectiveLine($"DIRECTIVE resume reason={directive.Reason ?? "brain-resume"} event=host.resume");
                return resumed;

            case DirectiveKind.SetAnimation:
                if (directive.Clip is null)
                {
                    LastRefusal = I18n.Get("host.arbiter.refusal.no-clip");
                    EmitDirectiveLine("DIRECTIVE setAnimation clip= restart=false result=refused:no-clip");
                    return false;
                }

                bool switched = _instance.View.SetAnimation(directive.Clip, directive.Restart);
                if (switched)
                {
                    _instance.LastSetAnimation = (directive.Clip, directive.Restart);
                    EmitDirectiveLine($"DIRECTIVE setAnimation clip={directive.Clip} restart={FlagOf(directive.Restart)} result=ok");
                }
                else
                {
                    LastRefusal = $"setAnimation refused clip={directive.Clip}";
                    EmitDirectiveLine($"DIRECTIVE setAnimation clip={directive.Clip} restart={FlagOf(directive.Restart)} result=refused:view");
                }

                return switched;

            case DirectiveKind.MoveTo:
                if (directive.At is not PetPoint at)
                {
                    LastRefusal = I18n.Get("host.arbiter.refusal.no-target");
                    EmitDirectiveLine("DIRECTIVE moveTo x=0 y=0 moved=false event=none");
                    return false;
                }

                LastMoveTo = at;
                // 契约 §6：真移窗；成功发 core.window.moved，失败记因不发事件。
                if (WindowMover is not null)
                {
                    bool moved = WindowMover((int)at.X, (int)at.Y);
                    if (!moved)
                    {
                        LastRefusal = "moveTo win32-failed err=SetWindowPos";
                        EmitDirectiveLine($"DIRECTIVE moveTo x={(int)at.X} y={(int)at.Y} moved=false event=none");
                        return false;
                    }
                }

                // 契约 §5.1：同一批里只发一条 core.window.moved（后者 = 最终位姿）。
                _moveToSeen++;
                if (_moveToSeen < _moveToBatch)
                {
                    EmitDirectiveLine($"DIRECTIVE moveTo x={(int)at.X} y={(int)at.Y} moved=true event=coalesced");
                    return true;
                }

                Events.Publish(new PetEvent(EventCatalog.WindowMoved, PetEvent.NowMs(), new JsonObject
                {
                    ["windowId"] = _instance.Id,
                    ["position"] = new JsonArray((int)at.X, (int)at.Y),
                }));
                EmitDirectiveLine($"DIRECTIVE moveTo x={(int)at.X} y={(int)at.Y} moved=true event=core.window.moved");
                return true;

            case DirectiveKind.Emit:
                if (directive.Event is not PetEvent evt)
                {
                    LastRefusal = I18n.Get("host.arbiter.refusal.no-event");
                    EmitDirectiveLine("DIRECTIVE emit type= payload-fields=0");
                    return false;
                }

                Events.Publish(evt);
                EmitDirectiveLine($"DIRECTIVE emit type={evt.Type} payload-fields={evt.Payload?.Count ?? 0}");
                return true;

            case DirectiveKind.RequestCapability:
                if (directive.Capability is null)
                {
                    LastRefusal = I18n.Get("host.arbiter.refusal.no-capability");
                    EmitDirectiveLine("DIRECTIVE requestCapability cap= verdict=refused latency=0 reason=no-capability");
                    return false;
                }

                // docs/14 §2：白名单之外的能力一律拒绝并记因（不许静默授予）。
                if (!CapabilityIds.IsWellKnown(directive.Capability))
                {
                    LastRefusal = $"capability not in whitelist: {directive.Capability}";
                    EmitDirectiveLine($"DIRECTIVE requestCapability cap={directive.Capability} verdict=refused latency=0 reason=not-whitelisted");
                    return false;
                }

                if (Capabilities is not null)
                {
                    long requestedAt = PetEvent.NowMs();
                    CapabilityVerdict verdict = Capabilities.Request(directive.Capability, directive.Justification, requestedAt);
                    if (verdict.Kind == CapabilityVerdictKind.Refused)
                    {
                        LastRefusal = $"capability refused: {directive.Capability} reason={verdict.Reason}";
                        EmitDirectiveLine($"DIRECTIVE requestCapability cap={directive.Capability} verdict=refused latency={verdict.LatencyMs} reason={verdict.Reason}");
                        return false;
                    }

                    EmitDirectiveLine($"DIRECTIVE requestCapability cap={directive.Capability} verdict=accepted latency={verdict.LatencyMs} reason=-");
                }

                GrantedCapabilities++;
                return true;

            default:
                LastRefusal = $"unknown directive kind {directive.Kind}";
                return false;
        }
    }

    private static string FlagOf(bool value) => value ? "true" : "false";

    /// <summary>
    /// docs/11:162 的排序结果（纯函数）。自检直接断言它，而不是靠「落地副作用」反推顺序 ——
    /// 副作用能证明「都落地了」，证明不了「谁先被裁决」，而后者正是这条 ABI 语义本身。
    /// </summary>
    public static IReadOnlyList<BrainDirective> Sorted(IReadOnlyList<BrainDirective>? directives)
    {
        var copy = new List<BrainDirective>(directives ?? Array.Empty<BrainDirective>());
        StableSortByPriority(copy);
        return copy;
    }

    /// <summary>按 kind 数值降序稳定排序（同档保持原序）—— 稳定是本条 ABI 语义的一部分。</summary>
    private static void StableSortByPriority(List<BrainDirective> items)
    {
        for (int i = 1; i < items.Count; i++)
        {
            BrainDirective key = items[i];
            int j = i - 1;
            while (j >= 0 && items[j].Kind < key.Kind)
            {
                items[j + 1] = items[j];
                j--;
            }

            items[j + 1] = key;
        }
    }

    private static void Bump(Dictionary<DirectiveKind, int> counter, DirectiveKind kind)
    {
        counter.TryGetValue(kind, out int current);
        counter[kind] = current + 1;
    }

    /// <summary>一行自检输出：落地 / 拒绝计数 + moveTo / emit 痕迹。</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        foreach (DirectiveKind kind in new[]
        {
            DirectiveKind.Suspend, DirectiveKind.SetAnimation, DirectiveKind.MoveTo,
            DirectiveKind.Emit, DirectiveKind.RequestCapability, DirectiveKind.Resume,
        })
        {
            _applied.TryGetValue(kind, out int applied);
            _refused.TryGetValue(kind, out int refused);
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(kind).Append('=').Append(applied);
            if (refused > 0)
            {
                text.Append('!').Append(refused);
            }
        }

        return $"arbiter {text} leases={GrantedCapabilities}"
            + $" moveTo={LastMoveTo?.ToString() ?? "-"} emits={Events.TotalPublished}"
            + $" received={Received} last-refusal={LastRefusal}";
    }
}
