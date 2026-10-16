// docs/11 §4「Brain 接口（行为层）」的 C# 绑定。
// docs/11 的类型名 → 本 namespace 的标识符：IBrain 不变（该词不在术语黑名单内）；
// BrainDirective / WorldSnapshot / BrainContext 同名。
// 契约核心（docs/11:160-162 逐字）：**指令即返回值**。Brain 不许在回调里直接调宿主 API；
// 它只能产出 BrainDirective[]，由宿主统一调度。仲裁顺序是 ABI 语义：
//   suspend > setAnimation > moveTo > emit > requestCapability
// 该顺序在此编码为 DirectiveKind 的数值（越大越优先），宿主与仲裁器（HostArbiter）共用它，
// 避免顺序在两个地方各写一遍。
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace PetDock.Core.PetRuntime;

/// <summary>
/// docs/11 §4 的 BrainDirective 判别式。**数值即 docs/11:162 的仲裁优先级**（越大越优先）：
/// RequestCapability 0 &lt; Emit 1 &lt; MoveTo 2 &lt; SetAnimation 3 &lt; Resume 4 &lt; Suspend 5。
/// Resume 不在 docs/11:162 逐字列出的五项里（它只列了 suspend），按「生命周期类指令同档」处理，
/// 且档内 Suspend 优先于 Resume；一个实例同时提出两者时宿主按此裁决。
/// </summary>
public enum DirectiveKind
{
    /// <summary>kind: "requestCapability"。</summary>
    RequestCapability = 0,

    /// <summary>kind: "emit"。</summary>
    Emit = 1,

    /// <summary>kind: "moveTo"。</summary>
    MoveTo = 2,

    /// <summary>kind: "setAnimation"。</summary>
    SetAnimation = 3,

    /// <summary>kind: "resume"。</summary>
    Resume = 4,

    /// <summary>kind: "suspend"。</summary>
    Suspend = 5,
}

/// <summary>
/// 一条指令（docs/11 §4 的联合类型）。用一个带槽位的 record 表达六种 kind，
/// 各静态工厂保证「哪种 kind 该填哪些字段」；未用到的槽位为 null。
/// </summary>
public sealed record BrainDirective
{
    private BrainDirective(DirectiveKind kind) => Kind = kind;

    /// <summary>指令种类（GateKind 之外，仲裁与分发都只看它）。</summary>
    public DirectiveKind Kind { get; }

    /// <summary>setAnimation 的 clip id（其余 kind 为 null）。</summary>
    public string? Clip { get; private init; }

    /// <summary>setAnimation 的 opts.restart（docs/11:99）。</summary>
    public bool Restart { get; private init; }

    /// <summary>setAnimation 的 opts 其余内容（原样透传，宿主不解释）。</summary>
    public JsonObject? Options { get; private init; }

    /// <summary>moveTo 的目标点。</summary>
    public PetPoint? At { get; private init; }

    /// <summary>moveTo 的速度（可空 = 宿主自定）。</summary>
    public double? Speed { get; private init; }

    /// <summary>emit 的事件包。</summary>
    public PetEvent? Event { get; private init; }

    /// <summary>requestCapability 的能力 id（docs/14 §2 白名单）。</summary>
    public string? Capability { get; private init; }

    /// <summary>requestCapability 的理由（必填，供授权 UI 展示）。</summary>
    public string? Justification { get; private init; }

    /// <summary>suspend / resume 的原因文本。</summary>
    public string? Reason { get; private init; }

    /// <summary>setAnimation：切到某个动画片段。</summary>
    public static BrainDirective SetAnimation(string clip, bool restart = false, JsonObject? options = null)
    {
        System.ArgumentNullException.ThrowIfNull(clip);
        return new BrainDirective(DirectiveKind.SetAnimation)
        {
            Clip = clip,
            Restart = restart,
            Options = options,
        };
    }

    /// <summary>moveTo：请求把宠物移到某点（宿主可最终拒绝）。</summary>
    public static BrainDirective MoveTo(PetPoint at, double? speed = null) =>
        new(DirectiveKind.MoveTo) { At = at, Speed = speed };

    /// <summary>suspend：请求挂起本实例。</summary>
    public static BrainDirective Suspend(string reason) =>
        new(DirectiveKind.Suspend) { Reason = reason ?? string.Empty };

    /// <summary>resume：请求恢复本实例。</summary>
    public static BrainDirective Resume(string reason) =>
        new(DirectiveKind.Resume) { Reason = reason ?? string.Empty };

    /// <summary>emit：往总线上发一条事件。</summary>
    public static BrainDirective Emit(PetEvent evt) =>
        new(DirectiveKind.Emit) { Event = evt ?? throw new System.ArgumentNullException(nameof(evt)) };

    /// <summary>requestCapability：申请一项能力（宿主可拒绝，docs/14 §2）。</summary>
    public static BrainDirective RequestCapability(string capability, string justification) =>
        new(DirectiveKind.RequestCapability)
        {
            Capability = capability ?? throw new System.ArgumentNullException(nameof(capability)),
            Justification = justification ?? string.Empty,
        };

    public override string ToString() => Kind switch
    {
        DirectiveKind.SetAnimation => $"setAnimation {Clip}{(Restart ? " restart" : string.Empty)}",
        DirectiveKind.MoveTo => $"moveTo {At}",
        DirectiveKind.Suspend => $"suspend {Reason}",
        DirectiveKind.Resume => $"resume {Reason}",
        DirectiveKind.Emit => $"emit {Event}",
        DirectiveKind.RequestCapability => $"requestCapability {Capability}",
        _ => Kind.ToString(),
    };
}

/// <summary>docs/11 §4 WorldSnapshot：宿主每 tick 交给 Brain 的世界快照（全部由宿主提供）。</summary>
public readonly record struct WorldSnapshot(
    PetPoint? Cursor,
    PetRect WindowRect,
    string? FocusedApp,
    int TimeOfDayMinutes,
    bool IsOccluded)
{
    /// <summary>只有窗口矩形的空世界（无可信输入时的形态）。</summary>
    public static WorldSnapshot Empty(PetRect window) => new(null, window, null, 0, false);

    public override string ToString() =>
        $"cursor={Cursor?.ToString() ?? "off"} window={WindowRect} focused={FocusedApp ?? "-"}"
        + $" tod={TimeOfDayMinutes} occluded={IsOccluded}";
}

/// <summary>
/// docs/11 §4 `init(ctx: BrainContext)` 的上下文。docs/11 未逐字规定字段，
/// 这里只给「宿主必然持有、且不泄漏系统能力」的四项：实例 id、状态层、队列事件、预算。
/// </summary>
public sealed class BrainContext
{
    public BrainContext(string petInstanceId, IPetState? state, ResourceBudget budget)
    {
        PetInstanceId = petInstanceId ?? throw new System.ArgumentNullException(nameof(petInstanceId));
        State = state;
        Budget = budget;
    }

    /// <summary>实例 id（UUIDv5(CAS 内容哈希)，docs/11 §2）。</summary>
    public string PetInstanceId { get; }

    /// <summary>本实例的状态层（可为 null：宿主未装配 state 插件）。</summary>
    public IPetState? State { get; }

    /// <summary>宿主预算（Brain 可据此自愿降级）。</summary>
    public ResourceBudget Budget { get; }
}

/// <summary>
/// docs/11 §4 IBrain（C# 绑定）。实现方必须是纯函数式的：相同 tick 输入 → 相同指令序列（docs/11:161），
/// 且 tick 内不许做持久化写（持久化写走 IPetState.Commit）。
/// </summary>
public interface IBrain
{
    /// <summary>docs/11 §4 `readonly meta: { id, version }`。</summary>
    InterfaceMeta Meta { get; }

    /// <summary>初始化（docs/11 §4 init）；失败即抛，宿主按 §9 记 degraded。</summary>
    System.Threading.Tasks.Task InitAsync(BrainContext ctx);

    /// <summary>下线。宿主按 docs/11 §9 给 1000ms 预算。</summary>
    System.Threading.Tasks.Task DisposeAsync();

    /// <summary>
    /// 宿主每 tick 调用一次；dt 以毫秒计，可累加、可被零化（挂起时）。
    /// 抛异常 = 本 tick 的 directives 全部作废 + 状态标记 degraded（docs/11:259）。
    /// </summary>
    IReadOnlyList<BrainDirective> Tick(int dtMs, WorldSnapshot world);

    /// <summary>事件注入（docs/11:137：总线顺序 = 到达顺序）。</summary>
    void OnEvent(PetEvent ev);
}
