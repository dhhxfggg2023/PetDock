// 插件管理动作（2026-10-28）：用户从面板打开 / 关闭 / 卸载一只插件。
//
// 三个动作都封送到宿主调度线程执行 —— 与 tick、热重载同一条线，绝不并发改闸门与句柄表。
// 「删目录」不在这里：文件系统归 PluginInventory 管，宿主只负责先把插件卸载干净
// （顺序反了会让正在跑的沙箱指着一个不存在的目录）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

public sealed partial class PluginHost
{
    /// <summary>本宿主扫描的插件根（主根在前，其余依次）。插件目录由调用方在这些根里定位。</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>按 id 打开一只插件：已装载而被禁用 → 恢复；根本没装载 → 重新校验清单再装载。
    /// 面板在后台线程等它；调度线程卡住会由 RunOnSchedulerVoid 的等待方超时暴露。</summary>
    public Task EnableById(string pluginId) => RunOnSchedulerVoid(() => EnableByIdCore(pluginId));

    /// <summary>按 id 禁用（不卸载：句柄、沙箱、计数保留，见 原规格 13 §3）。</summary>
    public Task DisableById(string pluginId) => RunOnSchedulerVoid(() => DisableByIdCore(pluginId));

    /// <summary>按 id 卸载并遗忘：吊销租约、释放沙箱、从句柄表移除。删除插件目录前必须先做。</summary>
    public Task Unload(string pluginId) => RunOnSchedulerVoid(() => UnloadCore(pluginId));

    private PluginHandle? FindHandleCore(string pluginId)
    {
        foreach (PluginHandle handle in _handles)
        {
            if (handle.State != PluginLifecycleState.Disposed
                && string.Equals(handle.Manifest.Id, pluginId, StringComparison.Ordinal))
            {
                return handle;
            }
        }
        return null;
    }

    private void EnableByIdCore(string pluginId)
    {
        PluginHandle? existing = FindHandleCore(pluginId);
        if (existing is not null)
        {
            if (existing.State == PluginLifecycleState.Suspended)
            {
                ResumeHandleCore(existing);
                return;
            }
            KernelLog.Write($"enable-skip plugin={pluginId} reason=state-{existing.State}");
            return;
        }

        // 没装载过：从任一插件根重新校验并装载。ResolveDir 找不到会抛 PluginLoadException，
        // 面板把它转成一句「这个插件已经不在了」。
        string dir = ResolveDir(pluginId);
        Result<PluginManifest, IReadOnlyList<ManifestError>> result = ManifestValidator.Validate(dir);
        if (!result.IsOk)
        {
            throw new PluginLoadException(
                pluginId,
                result.Error!.Select(error => $"{error.Field}: {error.Message}").ToArray());
        }
        _dirById[pluginId] = dir;
        LoadCore(result.Ok!, CancellationToken.None);
        KernelLog.Write($"plugin-enabled-on-demand plugin={pluginId}");
    }

    private void DisableByIdCore(string pluginId)
    {
        PluginHandle? handle = FindHandleCore(pluginId);
        if (handle is null)
        {
            KernelLog.Write($"disable-skip plugin={pluginId} reason=not-loaded");
            return;
        }
        DisableCore(handle);
    }

    private void UnloadCore(string pluginId)
    {
        PluginHandle? handle = FindHandleCore(pluginId);
        if (handle is null)
        {
            _dirById.TryRemove(pluginId, out _);
            return;
        }

        if (handle.State is PluginLifecycleState.Running or PluginLifecycleState.Suspended)
        {
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
            Publish("pluginUnloading", pluginId);
            handle.Sandbox?.Dispose();
            handle.Sandbox = null;
            handle.Tier2?.Dispose();
            handle.Tier2 = null;
            handle.Gate = null;
        }

        handle.State = PluginLifecycleState.Disposed;
        _handles.Remove(handle);
        _dirById.TryRemove(pluginId, out _);
        KernelLog.Write($"plugin-unloaded plugin={pluginId}");
    }

    /// <summary>该 id 是否来自随包发行根（主根）。静默授予只信这一侧，用户装进来的永远要问。</summary>
    internal bool IsBuiltin(string pluginId)
    {
        if (string.IsNullOrEmpty(_pluginsRoot) || !_dirById.TryGetValue(pluginId, out string? dir))
        {
            return false;
        }

        string root = Path.GetFullPath(_pluginsRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(dir).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
