// 原规格 11 §3 的视图接口（本 namespace 里的 C# 名 = IPetView）的绑定。
// 它对应 原规格 11 哪个接口、以及为什么接口名与上下文类型名都换过，写在 交接档案
// （core 之外的文件）——两张冻结文档在那个词上真实冲突，是否走 RFC 由队长裁定，本文件只实现。
//
// §3 的成员一个不少：meta / init / describe / attach / detach / dispose / setAnimation /
// queryFrame / onEvent；原规格 11 的 Promise<void> 绑成 Task。
using System.Threading.Tasks;

namespace PetDock.Core.PetRuntime;

/// <summary>
/// 原规格 11 §3.1 PetDescription：宿主用来决定是否接受这个插件。
/// RequiresHitmap = true 表示「alpha 不可信、必须给显式命中图」（原规格 11:111）。
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
/// 原规格 11 §3 `init(ctx)` 的上下文。字段取 原规格 11 里直接出现过的三项：
/// §2 的实例 id、§3:113 的素材引用（attach 前宿主已 ready）、§7:235 的预算。
/// </summary>
public sealed class PetViewContext
{
    public PetViewContext(
        string petInstanceId,
        System.Collections.Generic.IReadOnlyList<string> assets,
        ResourceBudget budget)
        : this(petInstanceId, assets, budget, null)
    {
    }

    public PetViewContext(
        string petInstanceId,
        System.Collections.Generic.IReadOnlyList<string> assets,
        ResourceBudget budget,
        System.Collections.Generic.IReadOnlyList<string>? assetRoots)
    {
        PetInstanceId = petInstanceId ?? throw new System.ArgumentNullException(nameof(petInstanceId));
        Assets = assets ?? System.Array.Empty<string>();
        Budget = budget;
        AssetRoots = assetRoots ?? System.Array.Empty<string>();
    }

    /// <summary>实例 id。</summary>
    public string PetInstanceId { get; }

    /// <summary>宿主已就绪的素材引用。</summary>
    public System.Collections.Generic.IReadOnlyList<string> Assets { get; }

    /// <summary>
    /// 宿主为本实例解析好的素材根目录（每个元素一个目录）。
    /// 为什么要有它：原规格 11:113 的 assets 是「CAS 引用」——内容寻址的键，不是机器路径。
    /// 而 CAS 尚未落地（原规格 15 §2 整节零实现），所以宿主手上真实持有的是**目录**。
    /// 视图实现据此去读自己的素材，而不必依赖进程级环境变量（那是全局的，不适合按实例切换）。
    /// 宿主填什么、视图怎么用，由实现方各自负责；core 不解析这里的任何内容（D-03b）。
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<string> AssetRoots { get; }

    /// <summary>宿主预算（原规格 11:235 的自愿降级通道）。</summary>
    public ResourceBudget Budget { get; }
}

/// <summary>
/// 原规格 11 §3 的视图接口。每实例恰好 1 个。
/// 终止规则（原规格 11 §3.2）逐条落在此处，因为它们是**调用方与实现方的共同义务**：
/// - QueryFrame 必须无阻塞：读盘 / 网络 / 大图解码不得出现在它内部（原规格 11:119）；
/// - 同一 now 必须返回同一帧（或语义上等价的帧）—— 这是金图测试的基础（原规格 11:120）；
/// - 实现方不主动启动任何东西，所有动画推进都由宿主的帧循环驱动（原规格 11:121）。
/// </summary>
public interface IPetView
{
    /// <summary>原规格 11 §3 `readonly meta: { id, version }`。</summary>
    InterfaceMeta Meta { get; }

    /// <summary>资源加载，不重画（原规格 11:92）。失败即抛，宿主按 §9 记 degraded。</summary>
    Task InitAsync(PetViewContext ctx);

    /// <summary>原规格 11 §3.1 描述（宿主据此决定接不接受这个插件）。</summary>
    PetDescription Describe();

    /// <summary>获得宿主给的画板（原规格 11:94）。</summary>
    void Attach(RenderTarget target);

    /// <summary>释放 GDI / 纹理 / 句柄（原规格 11:95）。</summary>
    void Detach();

    /// <summary>彻底下线；Detach 之后必须可存活（原规格 11:96）。</summary>
    Task DisposeAsync();

    /// <summary>
    /// 切换动画片段（原规格 11:99）。返回 false = 本实现不认识这个 clip。
    /// 宿主据此如实上报，不许静默当成功 ——「动画没切过去但一切正常」是这类接口最坏的失败形态。
    /// </summary>
    bool SetAnimation(string clipId, bool restart = false);

    /// <summary>
    /// 取这一帧（原规格 11:100）：同步、纯函数语义、不许读盘。
    /// 抛异常 = 当前帧 dropped（原规格 11:258），宿主按 §9 计数、连续 8 次后降级到占位图。
    /// </summary>
    Frame QueryFrame(int nowMs);

    /// <summary>宿主通知（开始 / 暂停 / 隐藏等，原规格 11:101）。</summary>
    void OnEvent(PetEvent ev);
}
