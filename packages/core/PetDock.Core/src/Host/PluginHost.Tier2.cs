// RFC-10 §2：Tier 2 生命周期接线（M7）。docs/13 §1 法则二 / docs/14 §1/§4/§5：
// tier 2 = 独立进程 + stdio JSON-RPC + quota/timeout，越限后果是卸载。
// 这里只做最小接线：把 PluginHost 的两个 tier-2 拒载点换成真 spawn + 握手 + 挂到 handle，
// 复用既有生命周期（Disable 保留状态、Reload 换实例、Dispose 全量回收）。
// docs/11 四 ABI 接口的跨进程 wire 契约仍是独立里程碑（docs/50 M7 未交付清单）。
using System.IO;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

public sealed partial class PluginHost
{
    /// <summary>
    /// tier 2 装载：docs/21 §3.4 的 entry = 相对插件根的外部可执行文件（无参数位），
    /// 子进程协议自证——<see cref="Tier2Sandbox.Start"/> 先握手 ping，不回即装载失败。
    /// 失败路径先把半个进程收干净再抛 PluginLoadException（与 tier 1 的装载失败同形）。
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
}
