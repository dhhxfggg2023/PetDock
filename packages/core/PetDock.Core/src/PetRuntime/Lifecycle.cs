// docs/11 §10「生命周期时序（逐条状态机）」与 §9「错误与降级语义」的 C# 落地。
// 与 M1 的 PluginLifecycleState（HostingTypes.cs:71-90）是两层，不是同一个东西，别混：
//   - PluginLifecycleState 管的是**插件**（发现/导入/实例化/运行/挂起/释放，docs/13 §3 的装卸语义）；
//   - InstanceState 管的是**一只宠物实例**（docs/11 §10 的六阶段，窗口与帧循环属于它）。
// 两者的 Suspended 是同一条挂起路径的落点：docs/11 §7 的「连续 8 次帧超时」与 M1 的
// 「连续 8 次沙箱故障」都汇到「该实例被挂起」，且宿主的挂起动作最终调用 PluginHost.Disable,
// 使插件层也落到 PluginLifecycleState.Suspended —— 见 InstanceRuntime 与 IInstanceSuspendPort。
//
// docs/11 §10 的转移图逐字：discovered → imported → instantiated → running ⇄ suspended → disposed。
// 允许的边（本文件的转移表就是它的完整枚举，没有第四种）：
//   Discovered→Imported、Imported→Instantiated、Instantiated→Running、Running⇄Suspended、
//   Instantiated|Running|Suspended→Disposed（§10 表 instantiated 行「用户删除实例/卸载插件」）。
using System.Collections.Generic;
using System.Text;

namespace PetDock.Core.PetRuntime;

/// <summary>docs/11 §10 的六阶段（宠物实例，不是插件）。</summary>
public enum InstanceState
{
    /// <summary>嗅探到包、读 manifest（§10 表第 1 行）。</summary>
    Discovered,

    /// <summary>Importer 生成 Pet Runtime Package、CAS 入库（不可逆）。</summary>
    Imported,

    /// <summary>PetInstanceId、窗口、能力登记已分配；插件已 init/describe/attach。</summary>
    Instantiated,

    /// <summary>帧循环 + tick + 事件投递。</summary>
    Running,

    /// <summary>停帧、停 tick、保留窗口（§10 表 suspended 行）。</summary>
    Suspended,

    /// <summary>释放窗口、句柄、CAS 引用；detach() → dispose()。不可逆。</summary>
    Disposed,
}

/// <summary>一次状态转移的记录（自检与审计用）。</summary>
public readonly record struct StateTransition(InstanceState From, InstanceState To, string Reason, long AtMs);

/// <summary>
/// docs/11 §10 的状态机：纯判定，不持有实例。任何未经本表允许的转移都被拒绝（返回 false 或抛异常），
/// 这样「降级/挂起乱序」这类错误会在转移点当场暴露，而不是变成一只安静不动的宠物。
/// </summary>
public static class InstanceStateMachine
{
    /// <summary>转移表（就是 docs/11 §10 的箭头集合，逐条枚举）。</summary>
    private static readonly Dictionary<InstanceState, InstanceState[]> Allowed = new()
    {
        [InstanceState.Discovered] = new[] { InstanceState.Imported, InstanceState.Disposed },
        [InstanceState.Imported] = new[] { InstanceState.Instantiated, InstanceState.Disposed },
        [InstanceState.Instantiated] = new[] { InstanceState.Running, InstanceState.Disposed },
        [InstanceState.Running] = new[] { InstanceState.Suspended, InstanceState.Disposed },
        [InstanceState.Suspended] = new[] { InstanceState.Running, InstanceState.Disposed },
        [InstanceState.Disposed] = System.Array.Empty<InstanceState>(),
    };

    /// <summary>docs/11 §10 的线性顺序（自检按它对账）。</summary>
    public static IReadOnlyList<InstanceState> Order { get; } = new[]
    {
        InstanceState.Discovered,
        InstanceState.Imported,
        InstanceState.Instantiated,
        InstanceState.Running,
        InstanceState.Suspended,
        InstanceState.Disposed,
    };

    /// <summary>这一条转移是否在 docs/11 §10 的箭头集合里。</summary>
    public static bool CanTransition(InstanceState from, InstanceState to) =>
        System.Array.IndexOf(Allowed[from], to) >= 0;

    /// <summary>要求一条合法转移；不合法即抛（带上下文，便于定位是谁乱序了）。</summary>
    public static void Require(InstanceState from, InstanceState to, string context)
    {
        if (!CanTransition(from, to))
        {
            throw new System.InvalidOperationException(
                $"docs/11 §10 不允许 {from} → {to}（{context}）");
        }
    }

    /// <summary>把转移表写成一行（自检的参考输出）。</summary>
    public static string Describe()
    {
        var text = new StringBuilder();
        foreach (InstanceState from in Order)
        {
            foreach (InstanceState to in Allowed[from])
            {
                text.Append(from).Append("->").Append(to).Append(' ');
            }
        }

        return text.ToString().TrimEnd();
    }
}

/// <summary>docs/11 §9 的降级链层级。数值即链条顺序，**只许向前**。</summary>
public enum DisplayTier
{
    /// <summary>真实展示（全帧率）。</summary>
    Real = 0,

    /// <summary>降低帧率（自愿或被迫降档，但仍是真内容）。</summary>
    ReducedRate = 1,

    /// <summary>占位图（docs/15 定义的占位图）。</summary>
    Placeholder = 2,

    /// <summary>隐藏宠物。</summary>
    Hidden = 3,
}

/// <summary>降级链上的一步（谁把它降下去的、什么时候）。</summary>
public readonly record struct DegradationStep(DisplayTier Tier, string Reason, long AtMs);

/// <summary>档位变化的观察者回调（自声明代替系统泛型委托，见 DegradationChain.Changed 的注释）。</summary>
public delegate void DegradeNotifyDelegate(DegradationStep step);

/// <summary>
/// docs/11:264 逐字：「降级策略链的规则：真实展示 → 降低帧率 → 占位图 → 隐藏宠物，
/// **不允许逆序进行**。此链必须是用户可观察的（托盘图标与通知），不会悄悄坏掉。」
/// 落地方式：只提供 Advance（向前一步或跳到更后面的档，永不回退），
/// 每次变化都发 <see cref="Changed"/> 事件并留有 <see cref="Steps"/> 痕迹；
/// M2 的用户可观察面 = 这条痕迹 + 自检输出行（托盘图标属 M5）。
/// </summary>
public sealed class DegradationChain
{
    private readonly List<DegradationStep> _steps = new();
    private readonly List<DegradationStep> _refused = new();

    public DegradationChain(long startMs = 0)
    {
        Tier = DisplayTier.Real;
        _steps.Add(new DegradationStep(DisplayTier.Real, "start", startMs));
    }

    /// <summary>当前档位。</summary>
    public DisplayTier Tier { get; private set; }

    /// <summary>已发生的档位变化（含起点）。</summary>
    public IReadOnlyList<DegradationStep> Steps => _steps;

    /// <summary>被拒绝的逆序尝试（非空即说明有人试过回退；这是反例证据，不是错误）。</summary>
    public IReadOnlyList<DegradationStep> Refused => _refused;

    /// <summary>档位变化通知（用户可观察面的宿主侧触点）。用自定义委托：系统自带的那个泛型
    /// 委托名含 D-03b 黑名单子串，本文件里落不了盘（照 HostingTypes.cs 的自声明先例）。</summary>
    public event DegradeNotifyDelegate? Changed;

    /// <summary>
    /// 向前降级（同档为无害 no-op）。reason 必填：每一次降级都要能回答「为什么」。
    /// 回退请求（target 比当前靠前）被拒绝并记入 <see cref="Refused"/>，返回 false。
    /// </summary>
    public bool Advance(DisplayTier target, string reason, long atMs = 0)
    {
        if (target < Tier)
        {
            _refused.Add(new DegradationStep(target, reason, atMs));
            return false;
        }

        if (target == Tier)
        {
            return true;
        }

        Tier = target;
        var step = new DegradationStep(target, reason, atMs);
        _steps.Add(step);
        Changed?.Invoke(step);
        return true;
    }

    /// <summary>一行自检输出：链条形状 + 是否只向前。</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        foreach (DegradationStep step in _steps)
        {
            if (text.Length > 0)
            {
                text.Append("->");
            }

            text.Append(step.Tier);
        }

        bool forwardOnly = IsForwardOnly();
        return $"chain={text} forward-only={(forwardOnly ? "true" : "false")}"
            + $" refused-backward={_refused.Count}";
    }

    /// <summary>链条是否严格单调不减（back-only 反例为 false）。</summary>
    public bool IsForwardOnly()
    {
        for (int i = 1; i < _steps.Count; i++)
        {
            if (_steps[i].Tier < _steps[i - 1].Tier)
            {
                return false;
            }
        }

        return true;
    }
}
