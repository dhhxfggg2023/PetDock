// docs/11 §3 的视图接口（本 namespace 里的 C# 名 = IPetView）的绑定。
// 它对应 docs/11 哪个接口、以及为什么接口名与上下文类型名都换过，写在 docs/handoff/M2-name-map.md
// （core 之外的文件）——两张冻结文档在那个词上真实冲突，是否走 RFC 由队长裁定，本文件只实现。
//
// §3 的成员一个不少：meta / init / describe / attach / detach / dispose / setAnimation /
// queryFrame / onEvent；docs/11 的 Promise<void> 绑成 Task。
using System.Threading.Tasks;

namespace PetDock.Core.PetRuntime;

/// <summary>
/// docs/11 §3.1 PetDescription：宿主用来决定是否接受这个插件。
/// RequiresHitmap = true 表示「alpha 不可信、必须给显式命中图」（docs/11:111）。
/// </summary>
public sealed record PetDescription(
    PetSize NaturalSize,
    double PreferredFps,
    bool RequiresHitmap,
    System.Collections.Generic.IReadOnlyList<string> Capabilities,
    System.Collections.Generic.IReadOnlyList<string> Assets)
{
    public override string ToString() =>
        $"size={NaturalSize} fps={PetPoint.Fmt(PreferredFps)} hitmap={RequiresHitmap}"
        + $" caps=[{string.Join(",", Capabilities)}] assets={Assets.Count}";
}

/// <summary>
/// docs/11 §3 `init(ctx)` 的上下文。字段取 docs/11 里直接出现过的三项：
/// §2 的实例 id、§3:113 的素材引用（attach 前宿主已 ready）、§7:235 的预算。
/// </summary>
public sealed class PetViewContext
{
    public PetViewContext(
        string petInstanceId,
        System.Collections.Generic.IReadOnlyList<string> assets,
        ResourceBudget budget)
    {
        PetInstanceId = petInstanceId ?? throw new System.ArgumentNullException(nameof(petInstanceId));
        Assets = assets ?? System.Array.Empty<string>();
        Budget = budget;
    }

    /// <summary>实例 id。</summary>
    public string PetInstanceId { get; }

    /// <summary>宿主已就绪的素材引用。</summary>
    public System.Collections.Generic.IReadOnlyList<string> Assets { get; }

    /// <summary>宿主预算（docs/11:235 的自愿降级通道）。</summary>
    public ResourceBudget Budget { get; }
}

/// <summary>
/// docs/11 §3 的视图接口。每实例恰好 1 个。
/// 终止规则（docs/11 §3.2）逐条落在此处，因为它们是**调用方与实现方的共同义务**：
/// - QueryFrame 必须无阻塞：读盘 / 网络 / 大图解码不得出现在它内部（docs/11:119）；
/// - 同一 now 必须返回同一帧（或语义上等价的帧）—— 这是金图测试的基础（docs/11:120）；
/// - 实现方不主动启动任何东西，所有动画推进都由宿主的帧循环驱动（docs/11:121）。
/// </summary>
public interface IPetView
{
    /// <summary>docs/11 §3 `readonly meta: { id, version }`。</summary>
    InterfaceMeta Meta { get; }

    /// <summary>资源加载，不重画（docs/11:92）。失败即抛，宿主按 §9 记 degraded。</summary>
    Task InitAsync(PetViewContext ctx);

    /// <summary>docs/11 §3.1 描述（宿主据此决定接不接受这个插件）。</summary>
    PetDescription Describe();

    /// <summary>获得宿主给的画板（docs/11:94）。</summary>
    void Attach(RenderTarget target);

    /// <summary>释放 GDI / 纹理 / 句柄（docs/11:95）。</summary>
    void Detach();

    /// <summary>彻底下线；Detach 之后必须可存活（docs/11:96）。</summary>
    Task DisposeAsync();

    /// <summary>
    /// 切换动画片段（docs/11:99）。返回 false = 本实现不认识这个 clip。
    /// 宿主据此如实上报，不许静默当成功 ——「动画没切过去但一切正常」是这类接口最坏的失败形态。
    /// </summary>
    bool SetAnimation(string clipId, bool restart = false);

    /// <summary>
    /// 取这一帧（docs/11:100）：同步、纯函数语义、不许读盘。
    /// 抛异常 = 当前帧 dropped（docs/11:258），宿主按 §9 计数、连续 8 次后降级到占位图。
    /// </summary>
    Frame QueryFrame(int nowMs);

    /// <summary>宿主通知（开始 / 暂停 / 隐藏等，docs/11:101）。</summary>
    void OnEvent(PetEvent ev);
}
