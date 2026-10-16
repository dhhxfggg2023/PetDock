// docs/11 §2.3 AnimationClip 的一个**可选**宿主侧扩展：让宿主能问出「这个视图实现声明了哪些片段」。
//
// 为什么需要它：docs/50:21 的「动画可循环」判据要输出 `clip-frames idle=4 walk=4 hover=2`，
// 而 4 / 4 / 2 是**素材里声明**的帧数（docs/31 §1 的 anim/*.json）。core 不许读素材文件
// （docs/01 P-02 第 2 条，且素材清单的文件名含被禁子串），所以声明的帧数必须由实现方报出来，
// 宿主拿它跟「实际观察到的周期内不同帧数」对账 —— 两个数相等，C1 才是可判定的。
//
// 它是**兼容增量**（docs/11 §11：minor 只加不减）：宿主用 `is IClipCatalog` 探测，
// 不实现它的视图实现照常工作（只是 C1 失去「声明值」这一半，宿主会如实报告 declared-unavailable）。
using System.Collections.Generic;

namespace PetDock.Core.PetRuntime;

/// <summary>视图实现声明的片段目录（可选实现，宿主用 is 探测）。</summary>
public interface IClipCatalog
{
    /// <summary>声明的动画片段（帧数 / 名义帧率 / 循环模式）。</summary>
    IReadOnlyList<AnimationClip> Clips { get; }
}
