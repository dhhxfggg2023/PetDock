// 宠物实例的素材根（RFC-15 §3.2 第 3 跳）。
// 为什么单独一个文件：PetInstance.cs 已经在 D-03c 的 400 行上限边缘（356 行），
// 而「素材根」是宿主装配期解析、构造后不变的一件事，挤进主文件只会推它越线。
//
// 语义：宿主为**这一只**宠物解析好的素材根目录（每个元素一个目录或一个文件路径）。
// 空 = 宿主没给（视图实现回落到它自己的定位逻辑）。
// core 不解释这里的任何内容 —— 「这个目录里是什么格式」的知识全在视图实现侧（D-03b / P-02）。
using System;
using System.Collections.Generic;

namespace PetDock.Hosting;

public sealed partial class PetInstance
{
    /// <summary>
    /// 本实例的素材根目录（RFC-15 §3.2）：宿主在装配时解析好，构造后不可变。
    /// 空 = 宿主没给（视图实现回落到它自己的默认定位）。
    /// </summary>
    public IReadOnlyList<string> AssetRoots { get; init; } = Array.Empty<string>();
}
