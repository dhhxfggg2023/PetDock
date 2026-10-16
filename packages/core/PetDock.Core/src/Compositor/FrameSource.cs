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
