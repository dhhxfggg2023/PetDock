// 名册的状态落盘配置面（RFC-24 §6.1 / §6.3）。
//
// 为什么单独一片：名册主文件（PetSurfaceLoader.cs）贴着 D-03c 的 400 行上限，
// 而这里只有两件事 —— 「数据区里的 state 目录是哪个」与「怎么给实例接上落盘层」。
// 关键在于**只有一个答案**：目录由组合根一次性给出（见 StateDir 的注释），
// 装配路径里不再临时解析数据区（docs/40 D-08a「一个真相」）。
using System.Collections.Generic;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

public sealed partial class PetSurfaceRoster
{
    /// <summary>
    /// 状态落盘的目录（RFC-24 §6.1，通常是 <c>&lt;数据区&gt;/state</c>）。null = 不落盘
    /// （自检的纯内存实例、或装配期还没解析数据区）。由组合根在装载后一次性给出，之后每建一只实例都按它落盘 ——
    /// 不在 BuildInstance 里临时解析，否则「哪个根是真身」会出现两个答案（docs/40 D-08a「一个真相」）。
    /// </summary>
    public string? StateDir { get; set; }

    /// <summary>
    /// 给实例接上落盘层（RFC-24 §6.3）。没配数据根就不接 —— 自检与纯内存用法保持原样。
    /// 事件出口走实例自己的仲裁器事件口：迁移完成这类事件要能被 brain 看见（docs/16）。
    /// </summary>
    private void AttachStateStore(PetInstance instance, IReadOnlyList<string>? assetRoots)
    {
        if (string.IsNullOrEmpty(StateDir))
        {
            return;
        }

        IPetState core = StateLayer ?? new MemoryPetState();
        PersistentPetState store = PersistentPetState.Attach(
            instance.Id, core, StateDir!, assetRoots, instance.Arbiter.Events.Publish);
        instance.InstallStateLayer(store);
    }
}
