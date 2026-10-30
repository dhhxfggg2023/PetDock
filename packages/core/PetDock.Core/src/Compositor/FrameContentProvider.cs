// 实现 原规格 10「插件宿主」在内容侧的接入口：内核只声明「谁来提供一帧像素」，
// 至于像素来自哪个文件、怎么解码缩放，内核一无所知（原规格 40 §3.1 D-03b）。
using System.Drawing;

namespace PetDock.Core.Compositor;

/// <summary>
/// 上层内容源工厂契约。由 plugins 层实现，内核在运行时按本接口发现并调用它；
/// 找不到工厂、工厂返回 null、工厂构造或调用抛异常，一律退回内核纯色兜底
/// （SolidFrameContent）——窗口仍然可见、仍然可命中。
/// </summary>
public interface IFrameContentProvider
{
    /// <summary>产出一帧内容源；返回 null 表示上层这一轮不提供内容，内核退回兜底。</summary>
    IFrameContentSource? Create();

    /// <summary>这一轮装配的说明（用了什么、为什么没给内容），内核原样转述，不解释它的含义。</summary>
    string Note { get; }
}
