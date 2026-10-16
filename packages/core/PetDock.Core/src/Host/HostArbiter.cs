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
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

/// <summary>
/// 已落地事件与宿主自身补发事件的分发口（M2 的最小面）。
/// 总线路由（docs/16 的 emit / parallel / serial / bail / waterfall 五种分发模式）属 M3/M4；
/// M2 只提供「记账 + 可注入的观察者」两个能力，好让判据（t4/t5）能读到 emit 真的落地了。
/// 事件信封（docs/02 §1.4）不在此解释载荷语义 —— 总线不解释载荷（同条款）。
/// </summary>
public sealed class HostEventDispatch
{
    private readonly List<PetEvent> _log = new();

    /// <summary>每条落地的事件（M2 记账；顺序 = 落地顺序）。</summary>
    public IReadOnlyList<PetEvent> Log => _log;

    /// <summary>观察者：宿主补发事件（如 host.canceled）与插件 emit 都会到这里。</summary>
    public event EventNotifyDelegate? Published;

    /// <summary>记录并发布一条事件。</summary>
    public void Publish(PetEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        _log.Add(evt);
        try
        {
            Published?.Invoke(evt);
        }
        catch (Exception error)
        {
            KernelLog.Write($"event-observer-failed {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>按类型计数（判据面）。</summary>
    public int CountOf(string type)
    {
        int n = 0;
        foreach (PetEvent evt in _log)
        {
            if (string.Equals(evt.Type, type, StringComparison.Ordinal))
            {
                n++;
            }
        }

        return n;
    }
}

/// <summary>事件观察者回调（自声明代替系统泛型委托，理由见 HostingTypes.cs 的先例）。</summary>
public delegate void EventNotifyDelegate(PetEvent evt);

/// <summary>docs/11:162 的指令仲裁器。</summary>
public sealed class HostArbiter
{
    private readonly PetInstance _instance;
    private readonly Dictionary<DirectiveKind, int> _applied = new();
    private readonly Dictionary<DirectiveKind, int> _refused = new();

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
                return _instance.Suspend(directive.Reason ?? "brain-suspend");

            case DirectiveKind.Resume:
                return _instance.Resume(directive.Reason ?? "brain-resume");

            case DirectiveKind.SetAnimation:
                if (directive.Clip is null)
                {
                    LastRefusal = "setAnimation 缺 clip";
                    return false;
                }

                bool switched = _instance.View.SetAnimation(directive.Clip, directive.Restart);
                if (!switched)
                {
                    LastRefusal = $"setAnimation refused clip={directive.Clip}";
                }

                return switched;

            case DirectiveKind.MoveTo:
                if (directive.At is not PetPoint at)
                {
                    LastRefusal = "moveTo 缺目标点";
                    return false;
                }

                LastMoveTo = at;
                return true;

            case DirectiveKind.Emit:
                if (directive.Event is not PetEvent evt)
                {
                    LastRefusal = "emit 缺事件包";
                    return false;
                }

                Events.Publish(evt);
                return true;

            case DirectiveKind.RequestCapability:
                if (directive.Capability is null)
                {
                    LastRefusal = "requestCapability 缺能力 id";
                    return false;
                }

                // docs/14 §2：白名单之外的能力一律拒绝并记因（不许静默授予）。
                if (!CapabilityIds.IsWellKnown(directive.Capability))
                {
                    LastRefusal = $"capability not in whitelist: {directive.Capability}";
                    return false;
                }

                GrantedCapabilities++;
                return true;

            default:
                LastRefusal = $"unknown directive kind {directive.Kind}";
                return false;
        }
    }

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
            + $" moveTo={LastMoveTo?.ToString() ?? "-"} emits={Events.Log.Count}"
            + $" received={Received} last-refusal={LastRefusal}";
    }
}
