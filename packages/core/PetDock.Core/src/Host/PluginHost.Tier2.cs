// RFC-10 §2：Tier 2 生命周期接线（M7）。原规格 13 §1 法则二 / 原规格 14 §1/§4/§5：
// tier 2 = 独立进程 + stdio JSON-RPC + quota/timeout，越限后果是卸载。
// 这里做两件事：① 把 PluginHost 的两个 tier-2 拒载点换成真 spawn + 握手 + 挂到 handle；
// ② RFC-28：把句柄的帧面/头脑面交成内核已知的两个类型（IFrameContentSource / IBrain）。
using System.IO;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

public sealed partial class PluginHost
{
    /// <summary>
    /// tier 2 装载：原规格 21 §3.4 的 entry = 相对插件根的外部可执行文件（无参数位），
    /// 子进程协议自证——<see cref="Tier2Sandbox.Start"/> 先握手 ping、再取一次 meta（RFC-27 §3.1），
    /// 不回即装载失败。失败路径先把半个进程收干净再抛 PluginLoadException（与 tier 1 的装载失败同形）。
    /// </summary>
    private Tier2Sandbox LoadTier2Sandbox(
        string pluginId,
        string pluginDir,
        PluginManifest manifest,
        CapabilityGate gate,
        ISandboxEventSink sink)
    {
        string command = Path.GetFullPath(Path.Combine(pluginDir, manifest.Entry));
        Tier2Sandbox sandbox = new(gate, sink, command, workingDir: pluginDir);
        try
        {
            if (!sandbox.Start())
            {
                throw new PluginLoadException(pluginId, new[] { $"tier-2 handshake failed (pid={sandbox.Pid}, no ping reply)" });
            }
        }
        catch
        {
            sandbox.Dispose();
            throw;
        }

        return sandbox;
    }

    /// <summary>
    /// RFC-28 §3.1：把这个句柄的帧面变成内容源。非 tier-2 句柄、或沙箱已不在 → null
    /// （与 FrameComposition 既有纪律一致：「上层没提供内容」不是错误）。
    /// </summary>
    public IFrameContentSource? CreateFrameContent(IPluginHandle handle) =>
        handle is PluginHandle row && row.Tier2 is { } sandbox && sandbox.IsLoaded
            ? new Tier2FrameContent(row.Manifest.Id, sandbox)
            : null;

    /// <summary>
    /// RFC-28 §3.4：把这个句柄的头脑面变成 IBrain。没取到 meta 的客体会在这里被拒
    /// （没有身份的头脑没法被选择器引用，也没法在日志里说清是谁在动）。
    /// </summary>
    public IBrain? CreateBrain(IPluginHandle handle) =>
        handle is PluginHandle row && row.Tier2 is { } sandbox && sandbox.IsLoaded && sandbox.Hello is not null
            ? new Tier2BrainBridge(sandbox)
            : null;
}
