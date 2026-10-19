// 插件句柄（契约 §5.2 IPluginHandle 的内部形态）。承载宿主侧全部 per-插件状态：
// 沙箱、能力闸门、句柄、计数器。热重载的「handle 身份不变」由本对象的原地变异实现 ——
// 重载绝不替换本对象，只换它指向的沙箱/manifest（契约 §5.3）。
// 写者唯一（宿主调度线程）；外部读一律经 PluginHost.Snapshot 到调度线程取，避免撕裂。
using System.Collections.Generic;
using System.IO;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

/// <summary>能力句柄（docs/14 §2「能力是句柄，不是开关」）。
/// 禁用 → 吊销（Active=false）但保留记录 —— 「状态保留、不卸载」的一部分。
/// M1 硬编码测试能力集 = 声明 ∩ 白名单（契约 §5.4），授权 UI 属 M5。</summary>
internal sealed class CapabilityLease
{
    public string Id { get; }
    public bool Active { get; set; }

    public CapabilityLease(string id)
    {
        Id = id;
        Active = true; // 装载即授予（docs/14 §2）：句柄生而有效，禁用才吊销
    }
}

internal sealed class PluginHandle : IPluginHandle
{
    private readonly PluginHost _owner;

    internal PluginHandle(
        PluginHost owner,
        PluginManifest manifest,
        string pluginDir,
        PluginEventSink sink,
        string manifestHash)
    {
        _owner = owner;
        Manifest = manifest;
        PluginDir = pluginDir;
        ManifestPath = Path.Combine(pluginDir, ManifestValidator.ManifestFileName);
        ManifestHash = manifestHash;
        Sink = sink;
        State = PluginLifecycleState.Discovered;
    }

    /// <summary>当前 manifest（热重载成功后指向新内容；对象身份不变 —— 契约 §5.3）。</summary>
    public PluginManifest Manifest { get; internal set; }

    /// <summary>生命周期状态（契约 §5.3 状态机）。</summary>
    public PluginLifecycleState State { get; internal set; }

    /// <summary>Suspended → Running（重新分配句柄、重新订阅）。在宿主调度线程上执行。</summary>
    public void Resume() => _owner.ResumeHandle(this);

    internal string PluginDir { get; }
    internal string ManifestPath { get; }
    internal PluginEventSink Sink { get; }
    internal string ManifestHash { get; set; }

    // —— 以下字段由宿主调度线程独占写 ——
    internal PluginSandbox? Sandbox { get; set; }
    internal Tier2Sandbox? Tier2 { get; set; }
    internal CapabilityGate? Gate { get; set; }
    internal List<CapabilityLease> Leases { get; } = new();
    internal long Ticks;
    internal long EventsReceived;
    internal int ConsecutiveFaults;
    internal int ReloadCount;
    internal int ReloadChecks;
    internal bool ReloadPending;

    /// <summary>事件投递（docs/13 §4 订阅面）：仅 Running 接收 —— 禁用后即停止送达。</summary>
    internal void Deliver(string eventName)
    {
        if (State != PluginLifecycleState.Running)
        {
            return;
        }
        EventsReceived++;
    }

    internal int ActiveLeaseCount
    {
        get
        {
            int n = 0;
            foreach (CapabilityLease lease in Leases)
            {
                if (lease.Active)
                {
                    n++;
                }
            }
            return n;
        }
    }

    /// <summary>沙箱存活 = 已装载且未释放（禁用保留为 true —— 不卸载）。tier 1 看 Sandbox，tier 2 看 Tier2。</summary>
    internal bool SandboxAlive => (Sandbox is not null && Sandbox.IsLoaded) || (Tier2 is not null && Tier2.IsLoaded);
}
