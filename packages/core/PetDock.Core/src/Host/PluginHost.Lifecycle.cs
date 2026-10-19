// PluginHost 的生命周期半边（partial 的另一半在 PluginHost.cs）：
// 调度循环、装载/禁用/热重载/恢复的核心逻辑。契约 §5.3 的 11 行转移表在此落地。
// 单文件 <400 行（docs/40 D-03c）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PetDock.Core.Compositor;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

public sealed partial class PluginHost
{
    private const int ConsecutiveFaultLimit = 8; // 契约 §5.3：连续 8 次失败 → 自动挂起
    private const int LifecycleDisposeBudgetMs = 1000; // docs/11 §9：dispose 预算，超时记日志
    private const int MaxEntryBytes = 64 * 1024 * 1024;
    private const string HostSliceEventName = "host.slice";

    public Task<IPluginHandle> Load(PluginManifest manifest, CancellationToken ct)
        => RunOnSchedulerAsync<IPluginHandle>(() => LoadCore(manifest, ct));

    private PluginHandle LoadCore(PluginManifest manifest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string id = manifest.Id;
        if (_handles.Any(h => h.Manifest.Id == id && h.State != PluginLifecycleState.Disposed))
        {
            throw new PluginLoadException(id, new[] { "already loaded (duplicate id)" });
        }

        string pluginDir = ResolveDir(id);
        string manifestPath = Path.Combine(pluginDir, ManifestValidator.ManifestFileName);
        string manifestText;
        try
        {
            manifestText = File.ReadAllText(manifestPath);
        }
        catch (Exception ex)
        {
            throw new PluginLoadException(id, new[] { $"manifest unreadable: {ex.Message}" });
        }
        string manifestHash = ManifestValidator.NormalizedHash(manifestText);

        PluginEventSink sink = new(id);
        PluginHandle handle = new(this, manifest, pluginDir, sink, manifestHash)
        {
            State = PluginLifecycleState.Imported,
        };

        // 契约 §5.3 转移表：init 失败 → 拒绝、释放资源、无半状态（任何失败都不留 handle）
        if (manifest.Tier == 2)
        {
            // docs/13 §1 法则二 / docs/14 §1/§4/§5：tier 2 独立进程，M7 起真 spawn（RFC-10）。
            // wire 契约（帧/命中图/事件包）未定，子进程当前只承载握手与 tick 往返。
            CapabilityGate gate2 = new(manifest.Capabilities, grantedIds: null, sink: sink);
            Tier2Sandbox tier2 = LoadTier2Sandbox(id, pluginDir, manifest, gate2, sink);
            handle.Gate = gate2;
            handle.Tier2 = tier2;
            handle.State = PluginLifecycleState.Instantiated;
            foreach (string granted in gate2.Granted)
            {
                handle.Leases.Add(new CapabilityLease(granted)); // M1 硬编码测试能力集（契约 §5.4）
            }
        }
        else if (manifest.Tier == 1)
        {
            byte[] moduleBytes = ReadEntryModule(pluginDir, manifest);
            CapabilityGate gate = new(manifest.Capabilities, grantedIds: null, sink: sink);
            PluginSandbox sandbox = new(gate, sink, CalibratedBudgets());
            try
            {
                if (!sandbox.Load(moduleBytes))
                {
                    throw new PluginLoadException(id, new[] { "sandbox load rejected: " + sink.Describe() });
                }
            }
            catch
            {
                sandbox.Dispose();
                throw;
            }
            handle.Gate = gate;
            handle.Sandbox = sandbox;
            handle.State = PluginLifecycleState.Instantiated;
            foreach (string granted in gate.Granted)
            {
                handle.Leases.Add(new CapabilityLease(granted)); // M1 硬编码测试能力集（契约 §5.4）
            }
        }
        else
        {
            // tier 0：声明式资源包（docs/13 §2）—— 无执行体，登记即 Instantiated
            handle.State = PluginLifecycleState.Instantiated;
        }

        handle.State = PluginLifecycleState.Running;
        _handles.Add(handle);
        Watch(handle);
        Publish("pluginLoaded", id);
        Publish("pluginActivated", id);
        return handle;
    }

    private byte[] ReadEntryModule(string pluginDir, PluginManifest manifest)
    {
        string entryPath = Path.Combine(pluginDir, manifest.Entry);
        byte[] entryBytes;
        try
        {
            if (!File.Exists(entryPath))
            {
                throw new PluginLoadException(manifest.Id, new[] { $"entry not found: {manifest.Entry}" });
            }
            var info = new FileInfo(entryPath);
            if (info.Length > MaxEntryBytes)
            {
                throw new PluginLoadException(manifest.Id, new[] { $"entry too large: {info.Length} bytes (max {MaxEntryBytes})" });
            }
            entryBytes = File.ReadAllBytes(entryPath);
        }
        catch (PluginLoadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PluginLoadException(manifest.Id, new[] { $"entry unreadable: {ex.Message}" });
        }
        return WasmCarrier.IsWasmModule(entryBytes) ? entryBytes : WasmCarrier.Build(entryBytes);
    }

    private string ResolveDir(string pluginId)
    {
        if (_dirById.TryGetValue(pluginId, out string? known)
            && File.Exists(Path.Combine(known, ManifestValidator.ManifestFileName)))
        {
            return known;
        }
        // 回退：调用方可能直接持 manifest 调 Load（未经本宿主 ValidateManifest）
        foreach (string dir in Discover(_pluginsRoot))
        {
            Result<PluginManifest, IReadOnlyList<ManifestError>> result = ValidateManifest(dir);
            if (result.IsOk && result.Ok!.Id == pluginId)
            {
                return dir;
            }
        }
        throw new PluginLoadException(pluginId, new[] { $"plugin directory not found under {_pluginsRoot}" });
    }

    public Task Disable(IPluginHandle handle)
        => RunOnSchedulerVoid(() => DisableCore(handle));

    private void DisableCore(IPluginHandle pluginHandle)
    {
        if (pluginHandle is not PluginHandle handle)
        {
            throw new ArgumentException("handle was not issued by this host");
        }
        switch (handle.State)
        {
            case PluginLifecycleState.Suspended:
                return; // 幂等：已禁用
            case PluginLifecycleState.Running:
                break;
            default:
                throw new InvalidOperationException($"disable requires Running (current {handle.State})");
        }

        // 1) 事件订阅吊销：Deliver 只对 Running 生效 → 状态切换即停送达
        // 2) 能力句柄吊销（docs/14 §2）：gate 逐项 Revoke（带审计）+ 宿主侧 lease 置 Inactive（记录保留）
        if (handle.Gate is not null)
        {
            foreach (string granted in handle.Gate.Granted.ToArray())
            {
                handle.Gate.Revoke(granted);
            }
        }
        foreach (CapabilityLease lease in handle.Leases)
        {
            lease.Active = false;
        }
        // 3) 状态保留（docs/13 §3）：不卸载 —— 沙箱、计数、manifest 全部保留，仅 Suspended
        handle.State = PluginLifecycleState.Suspended;
        Publish("pluginSuspend", handle.Manifest.Id);
    }

    public Task Reload(IPluginHandle handle, CancellationToken ct)
        => RunOnSchedulerVoid(() => ReloadCore(handle, ct));

    private void ReloadCore(IPluginHandle pluginHandle, CancellationToken ct)
    {
        if (pluginHandle is not PluginHandle handle)
        {
            throw new ArgumentException("handle was not issued by this host");
        }
        ct.ThrowIfCancellationRequested();
        handle.ReloadChecks++;
        handle.ReloadPending = false;

        if (handle.State == PluginLifecycleState.Disposed)
        {
            throw new InvalidOperationException("cannot reload a disposed plugin");
        }
        if (!File.Exists(handle.ManifestPath))
        {
            // manifest 被删：不卸载正在运行的实例，等它回来（docs/13 §3：热重载是替换，不是卸载）
            KernelLog.Write($"reload-skip manifest-gone plugin={handle.Manifest.Id}");
            return;
        }
        string text = File.ReadAllText(handle.ManifestPath);
        string newHash = ManifestValidator.NormalizedHash(text);
        if (newHash == handle.ManifestHash)
        {
            return; // 内容未变（mtime-only）→ 无操作（契约 §5.3 判据）
        }
        Result<PluginManifest, IReadOnlyList<ManifestError>> result = ManifestValidator.Validate(handle.PluginDir);
        if (!result.IsOk)
        {
            foreach (ManifestError error in result.Error!)
            {
                KernelLog.Write($"manifest-error {error.Field}: {error.Message}");
            }
            KernelLog.Write($"reload-reject plugin={handle.Manifest.Id}: new manifest invalid, old instance keeps running");
            return;
        }
        PluginManifest newManifest = result.Ok!;
        if (newManifest.Id != handle.Manifest.Id)
        {
            // id 变了 = 另一个插件：本 handle 只认自己的 id
            KernelLog.Write($"reload-skip id-changed from={handle.Manifest.Id} to={newManifest.Id}");
            return;
        }

        // —— 内容确实变了：解除旧实例（吊销句柄 + 释放旧沙箱）——
        if (handle.Gate is not null)
        {
            foreach (string granted in handle.Gate.Granted.ToArray())
            {
                handle.Gate.Revoke(granted);
            }
        }
        foreach (CapabilityLease lease in handle.Leases)
        {
            lease.Active = false;
        }
        Publish("pluginUnloading", handle.Manifest.Id);
        handle.Sandbox?.Dispose();
        handle.Sandbox = null;
        handle.Tier2?.Dispose();
        handle.Tier2 = null;
        handle.Gate = null;

        // —— 装载新实例（handle 身份与外部订阅不变 —— 契约 §5.3）——
        try
        {
            if (newManifest.Tier == 2)
            {
                // RFC-10：tier 2 热重载 = 杀旧子进程 + spawn 新子进程；握手不过 → 下方 catch 落 Suspended。
                CapabilityGate gate2 = new(newManifest.Capabilities, grantedIds: null, sink: handle.Sink);
                Tier2Sandbox tier2 = LoadTier2Sandbox(newManifest.Id, handle.PluginDir, newManifest, gate2, handle.Sink);
                handle.Gate = gate2;
                handle.Tier2 = tier2;
                handle.Leases.Clear();
                foreach (string granted in gate2.Granted)
                {
                    handle.Leases.Add(new CapabilityLease(granted));
                }
            }
            else if (newManifest.Tier == 1)
            {
                byte[] moduleBytes = ReadEntryModule(handle.PluginDir, newManifest);
                CapabilityGate gate = new(newManifest.Capabilities, grantedIds: null, sink: handle.Sink);
                PluginSandbox sandbox = new(gate, handle.Sink, CalibratedBudgets());
                if (!sandbox.Load(moduleBytes))
                {
                    throw new PluginLoadException(newManifest.Id, new[] { "sandbox load rejected after reload: " + handle.Sink.Describe() });
                }
                handle.Gate = gate;
                handle.Sandbox = sandbox;
                handle.Leases.Clear();
                foreach (string granted in gate.Granted)
                {
                    handle.Leases.Add(new CapabilityLease(granted));
                }
            }
            else
            {
                handle.Leases.Clear();
            }
            handle.Manifest = newManifest;
            handle.ManifestHash = newHash;
            handle.ConsecutiveFaults = 0;
            handle.ReloadCount++;
            handle.State = PluginLifecycleState.Running;
            Publish("pluginLoaded", newManifest.Id);
            Publish("pluginActivated", newManifest.Id);
        }
        catch (Exception ex)
        {
            // 旧实例已解除、新实例装载失败：降级 Suspended（无半状态；恢复 = Resume 或再次改文件触发重载）
            handle.State = PluginLifecycleState.Suspended;
            KernelLog.Write($"reload-failed plugin={newManifest.Id} {ex.Message} (instance suspended)");
            throw;
        }
    }

    /// <summary>Suspended → Running（契约 §5.3）：重新分配句柄、重新订阅。
    /// 注：沙箱层 gate 无公开 Grant（t3 API），重绑 gate 属 M2 沙箱 API 工作 —— M1 的恢复在
    /// 宿主侧完成（lease 置回 Active + 补审计），事件订阅随状态恢复自动生效。</summary>
    internal void ResumeHandle(PluginHandle handle)
    {
        RunOnScheduler(() =>
        {
            if (handle.State != PluginLifecycleState.Suspended)
            {
                throw new InvalidOperationException($"resume requires Suspended (current {handle.State})");
            }
            foreach (CapabilityLease lease in handle.Leases)
            {
                lease.Active = true;
                handle.Sink.EmitRegrant(lease.Id);
            }
            handle.State = PluginLifecycleState.Running;
            Publish("pluginResume", handle.Manifest.Id);
        });
    }

    /// <summary>宿主侧事件分发入口（docs/13 §4 订阅面）：仅 Running 句柄收到。
    /// 在调度线程上串行投递 —— 与 tick 同一队列，绝不并发。</summary>
    public void PostPluginEvent(string name)
    {
        PostCommand(() =>
        {
            foreach (PluginHandle handle in _handles.ToArray())
            {
                handle.Deliver(name);
            }
        });
    }

    /// <summary>自检用：当前句柄的一致性快照（在调度线程上取，避免撕裂读）。</summary>
    internal IReadOnlyList<PluginHandle> HandlesSnapshot() => Snapshot(() => _handles.ToList());

    /// <summary>
    /// docs/14 §5 CPU 预算 8.3ms/片 → 燃料刻度的 M1 标定（SandboxBudgets 注释：宿主在 Load 时标定）。
    /// 燃料与毫秒无通用换算，取 8.3ms × ~120M 指令/秒 ≈ 1M 燃料/片：对 M1 载体模块（空 tick）
    /// 极其宽裕，同时保持「每片有界」的语义。FuelPerSlice=0（未标定）会让任何执行立即
    /// trap（all fuel consumed），故宿主必须显式给正值。
    /// </summary>
    private static SandboxBudgets CalibratedBudgets() => new()
    {
        FuelPerSlice = 1_000_000,
    };
}
