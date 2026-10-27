// 一只实例的状态落盘面（RFC-24 §6.3）。
//
// 为什么单独一片：PetInstance.cs 已经贴着 D-03c 的 400 行上限，而状态层这里有三个
// 使用场景（装配期换层、宿主写保留键、生命周期边界提交），塞进主文件只会让它更难读。
// 本片只做「把请求转给真正的缓冲区」，不自己攒状态 —— 缓冲区在 PersistentPetState 里，
// 那里才是崩溃语义的唯一结构性保证（见 RFC-24 §6 第 1 条）。
using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

public sealed partial class PetInstance
{
    /// <summary>
    /// 换上落盘层（RFC-24 §6.3）：装配期由 roster 调用一次，之后只有本实例自己用它提交。
    /// 为什么不是 public setter：状态层的归属必须是「每实例恰好一个」，多一个写入口就有
    /// 两个缓冲同时声称自己是真身（这正是 原规格 11 §6 三条约束要防的事）。
    /// </summary>
    internal void InstallStateLayer(IPetState? layer) => StateLayer = layer;

    /// <summary>宿主写自己的状态键（位置）：走与插件补丁同一个缓冲区，然后由提交点落盘。</summary>
    public bool WriteHostState(string key, JsonNode? value)
    {
        if (StateLayer is not PersistentPetState persistent)
        {
            return false;
        }

        persistent.WriteHost(key, value);
        return true;
    }

    /// <summary>
    /// 提交状态（生命周期边界上的落盘点：装载 / 挂起 / 释放，见 RFC-24 §6.3）。
    /// 不抛：边界上抛异常会把「关窗」变成「卡住」，失败只记日志与计数。
    /// </summary>
    public bool CommitState()
    {
        if (StateLayer is not PersistentPetState persistent)
        {
            return false;
        }

        bool ok = persistent.TryCommit(out StateCommitException? refusal);
        if (!ok)
        {
            LastFailure = $"state-commit {refusal!.Reason}";
        }

        return ok;
    }

    /// <summary>
    /// 读上一次落盘的位置（窗口重建时用）。读不到 / 没接落盘层 → null。
    /// 读走内存缓冲区，不碰磁盘（原规格 11:201 的读语义）。
    /// </summary>
    public JsonNode? ReadState(string key) => StateLayer?.Read(key);
}
