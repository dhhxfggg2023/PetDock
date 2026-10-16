// 「实例挂起 → 插件层禁用」的接缝（t1 契约 §8.2 的落点）。
//
// 为什么需要它：docs/11 §7 的「连续 8 次帧超时 → suspend」与 M1 的「连续 8 次沙箱故障 → suspend」
// 必须是**同一条挂起路径的两个计数源**，共用同一个 Suspended 终态 —— 所以实例层挂起时必须
// 把插件层也带到 PluginLifecycleState.Suspended，而不是各自留一个「挂起」的记号。
// core 里实例挂在 Host，插件宿主也在 Host，但本文件不直接引用 PluginHost：
// 用一口接口把两者接起来，好处是自检能注入探针（证明调用真的发生了，而不是「代码看起来会调」）。
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

/// <summary>实例挂起时通知插件层的那一口（实现方负责把它落到 PluginHost.Disable）。</summary>
public interface IInstanceSuspendPort
{
    /// <summary>把某个插件/实例挂起（reason 如实转述，供日志与用户提示）。</summary>
    void SuspendPlugin(string pluginId, string reason);
}

/// <summary>不接插件层的空实现（自检里没有真实插件宿主时的形态）。</summary>
public sealed class NoopSuspendPort : IInstanceSuspendPort
{
    /// <summary>被调用的次数（判据：挂起真的走过了这一口）。</summary>
    public int Calls { get; private set; }

    /// <summary>最近一次调用参数。</summary>
    public string LastCall { get; private set; } = "(none)";

    public void SuspendPlugin(string pluginId, string reason)
    {
        Calls++;
        LastCall = $"{pluginId}:{reason}";
    }
}

/// <summary>把挂起转到真实 PluginHost 的实现（M1 已落地的 Disable 语义：吊销订阅与句柄、保留状态）。</summary>
public sealed class HostSuspendPort : IInstanceSuspendPort
{
    private readonly PluginHost _host;

    public HostSuspendPort(PluginHost host) => _host = host;

    /// <summary>已成功转发的挂起次数。</summary>
    public int Forwarded { get; private set; }

    /// <summary>转发失败的原因（最后一次）。</summary>
    public string LastError { get; private set; } = "(none)";

    public void SuspendPlugin(string pluginId, string reason)
    {
        foreach (PluginHandle handle in _host.HandlesSnapshot())
        {
            if (!string.Equals(handle.Manifest.Id, pluginId, StringComparison.Ordinal))
            {
                continue;
            }

            if (handle.State != PluginLifecycleState.Running)
            {
                return;
            }

            try
            {
                _host.Disable(handle).GetAwaiter().GetResult();
                Forwarded++;
                KernelLog.Write($"instance-suspend-forwarded plugin={pluginId} reason={reason}");
            }
            catch (System.Exception error)
            {
                LastError = $"{error.GetType().Name}: {error.Message}";
                KernelLog.Write($"instance-suspend-forward-failed plugin={pluginId} {LastError}");
            }

            return;
        }

        LastError = "plugin-not-loaded";
    }
}
