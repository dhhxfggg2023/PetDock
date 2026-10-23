// 实现 D-03b 分界（docs/40 §3.1）：内核不认识素材文件，只收「像素 + 尺寸」。
// 素材的寻找、解码、缩放由上层（plugins，经 sdk 注入）负责；见 docs/40 §1.1 的三层定义
// 与 §2「依赖只能从上层指向下层」。内核内容源是可见性兜底的最后一道防线：拿不到内容源时
// 用不透明纯色占位帧填满帧缓冲，窗口照常可见、可命中、可拖动 —— 自检的每一项都照常可测。
using System.Drawing;

namespace PetDock.Core.Compositor;

/// <summary>
/// 每帧要合成进帧缓冲的内容描述：一张与帧缓冲同尺寸的 32 位 ARGB 位图，或无内容。
/// 内核对内容的来源一无所知 —— 不找文件、不解码、不缩放、不缓存（docs/40 D-03b）。
/// public 是有意的：这是内核暴露给上层的最小注入面（sdk/plugins 都要能实现它）。
/// </summary>
public interface IFrameContentSource : IDisposable
{
    /// <summary>
    /// 取当前帧内容。返回 null 表示本帧无内容：宿主回退到不透明占位帧。
    /// 实现方保证：位图尺寸与传入的帧尺寸严格一致、格式为 Format32bppArgb、
    /// 在下一次调用前保持有效（位图的放大与缓存是实现方的责任，宿主每帧都会取一次）。
    /// </summary>
    Bitmap? GetFrameContent(Size frameSize);
}

/// <summary>
/// 内容源的**可选**诊断面：它到底交付过几帧。
/// 自检需要把「内容源装上了」与「内容源真的在出帧」分开判 ——
/// 只判前者时，「装上了但一帧都取不出来」会静默通过（M0 的 source=false 打 SELF-OK 就是这一类）。
/// 不实现本接口的内容源仍然可用，判据退化为「帧缓冲已就绪」。
/// </summary>
public interface IFrameSourceStats
{
    /// <summary>累计交付的帧数。</summary>
    long FramesServed { get; }
}

/// <summary>
/// 内容源的**可选**自述面：它现在交付的是不是「真的」内容。
/// 为什么需要它：窗口判「内容源是真的」用「类型不是内核兜底」这一条，
/// 而内容源可以被**代理**（RFC-15 §3.3 的可替换内容源：窗口持有的是代理，代理里那份才可能是兜底）。
/// 不问代理一句，窗口就会把兜底帧算成真内容 —— M0 的 source=false 静默 SELF-OK 就会复活。
/// 不实现本接口的内容源按「是真的」处理（保持既有行为）。
/// </summary>
public interface IFrameContentInspector
{
    /// <summary>当前交付的是真内容（来自视图实现）而不是内核纯色兜底。</summary>
    bool DeliversRealContent { get; }
}

/// <summary>
/// 内容源的**可选**自述面：最近一帧与上一帧是不是**同一个位图对象**（RFC-21 §3.5）。
/// 为什么是对象身份而不是像素比较：像素比较要逐像素扫全帧（8.3ms 预算里做不起），
/// 而「实现方复用了同一张位图」正是「内容没变」可判定、且零成本的等价条件。
/// 不实现本接口的内容源恒 false ⇒ 静态节流永远进不了 Idle 档（fail-active：
/// 宁可多烧电，也不许把一只有动画的宠物冻住）。
/// </summary>
public interface IFrameReuseInspector
{
    /// <summary>最近交付的帧是否复用了上一次的位图对象。</summary>
    bool LastFrameReused { get; }
}

/// <summary>
/// 内容源的**可选**命中面（RFC-28 §3.3）：某些内容源自带命中图，命中不该由位图 alpha 反推
/// （影子不该被点到、挂件该被点到 —— `docs/11 §2.2`）。坐标是帧缓冲坐标。
/// 不实现本接口的内容源仍走「alpha 判定」，既有实现行为不变。
/// </summary>
public interface IFrameHitSurface
{
    /// <summary>这一点是否命中；没有可用帧时返回 false（fail-closed：宁可点不到，也不许误点）。</summary>
    bool Hits(int x, int y);
}
