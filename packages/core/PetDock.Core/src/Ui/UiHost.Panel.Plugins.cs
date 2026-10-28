// 插件页的启停、安装、删除入口（从 UiHost.Panel.cs 拆出，D-03c）。
// 与主文件同一条纪律：面板只发意图，判定/弹窗/落账都在宿主侧；动作都离开 UI 线程。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>
    /// 插件页「禁用/启用」入口：Running → 禁用；Suspended → 启用（不卸载，原规格 13 §3）。
    /// 同样放到后台线程：动作要过宿主调度线程，在 UI 线程上等它会把界面冻住。
    /// </summary>
    public void TogglePluginDisabled(string pluginId)
    {
        if (_host is null)
        {
            return;
        }

        _ = Task.Run(() => TogglePluginDisabledCore(pluginId));
    }

    /// <summary>禁用/启用的本体（后台线程）。</summary>
    private void TogglePluginDisabledCore(string pluginId)
    {
        PluginHost? host = _host;
        if (host is null)
        {
            return;
        }

        // 管理面优先：它能处理「装了但没装载」的插件（启动时被策略跳过的适配器），
        // 而 FindHandle 只看得见已经装载的句柄 —— 旧路径对这类插件只能记一行 skip。
        if (_inventory is not null)
        {
            bool? enabled = null;
            bool essential = false;
            foreach (PluginEntry entry in _inventory.Rows())
            {
                if (string.Equals(entry.Id, pluginId, StringComparison.Ordinal))
                {
                    enabled = entry.Enabled;
                    essential = entry.Essential;
                    break;
                }
            }

            if (enabled is null)
            {
                KernelLog.Write($"toggle-skip plugin={pluginId} reason=unknown-plugin");
                return;
            }

            // 随包界面插件是面板的唯一入口：停用它等于把用户锁在门外。
            // 清单自己声明 essential，内核不特判任何 id。
            if (essential)
            {
                KernelLog.Write($"toggle-skip plugin={pluginId} reason=essential");
                return;
            }

            bool target = !enabled.Value;
            if (_inventory.SetEnabled(pluginId, target, out string? inventoryError))
            {
                KernelLog.Write($"plugin-toggle plugin={pluginId} enabled={(target ? 1 : 0)}");
            }
            else
            {
                KernelLog.Write($"toggle-failed plugin={pluginId} {inventoryError}");
            }

            return;
        }

        PluginHandle? handle = FindHandle(host, pluginId);
        if (handle is null)
        {
            KernelLog.Write($"toggle-skip plugin={pluginId} reason=not-loaded");
            return;
        }

        try
        {
            if (handle.State == PluginLifecycleState.Running)
            {
                host.Disable(handle).GetAwaiter().GetResult();
                KernelLog.Write($"plugin-disabled plugin={pluginId}");
                PersistPluginDisabled(pluginId, disabled: true);
                return;
            }

            if (handle.State == PluginLifecycleState.Suspended)
            {
                handle.Resume();
                KernelLog.Write($"plugin-enabled plugin={pluginId}");
                PersistPluginDisabled(pluginId, disabled: false);
                return;
            }

            KernelLog.Write($"toggle-skip plugin={pluginId} reason=state-{handle.State}");
        }
        catch (Exception ex)
        {
            KernelLog.Write($"toggle-failed plugin={pluginId} {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 把「用户关掉了这只插件」落盘（2026-10-26 用户要求「插件可以选择关闭，重启生效」）。
    /// 运行时那半由 <see cref="PluginHost.Disable"/> 负责，这里管的是**下一轮**：
    /// 落进 settings.json 的 pluginsDisabled，Program 起装载之前读它、直接跳过这些 id。
    /// 没有设置存储（自检形态）就只留一行日志 —— 不假装记住了。
    /// </summary>
    private void PersistPluginDisabled(string pluginId, bool disabled)
    {
        UserSettingsStore? store = _settings;
        if (store is null)
        {
            KernelLog.Write($"plugin-toggle-not-persisted plugin={pluginId} reason=no-settings");
            return;
        }

        try
        {
            // 原子读-改-写（UserSettingsStore.Update）：切语言/记位置同时发生时不互相覆盖。
            UserSettings current = store.Load();
            var ids = current.DisabledPlugins is null
                ? new List<string>()
                : new List<string>(current.DisabledPlugins);
            bool present = ids.Exists(id => string.Equals(id, pluginId, StringComparison.Ordinal));
            if (present == disabled)
            {
                return; // 已经是这个状态：不白写一次盘
            }

            if (disabled)
            {
                ids.Add(pluginId);
            }
            else
            {
                ids.RemoveAll(id => string.Equals(id, pluginId, StringComparison.Ordinal));
            }

            // 写的时候再读一遍：上面判断完之后，切语言/记位置可能已经写过盘，
            // 拿着旧快照 Save 会把它们刚写的字段改回去。
            int count = store.Update(actual =>
            {
                var next = actual.DisabledPlugins is null
                    ? new List<string>()
                    : new List<string>(actual.DisabledPlugins);
                next.RemoveAll(id => string.Equals(id, pluginId, StringComparison.Ordinal));
                if (disabled)
                {
                    next.Add(pluginId);
                }

                return actual with { DisabledPlugins = next };
            }).DisabledPlugins?.Count ?? 0;
            KernelLog.Write($"plugin-disabled-persisted plugin={pluginId} disabled={(disabled ? 1 : 0)} count={count}");
        }
        catch (Exception error)
        {
            KernelLog.Write($"plugin-toggle-persist-failed plugin={pluginId} {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// 面板「安装插件…」入口的执行体（在 UI 线程上跑，和导入素材同一取舍）。
    /// 顺序：解到临时区并校验 → 落到用户插件根 → 打开它（打开会触发一次授权询问）。
    /// 返回 null = 成功；返回字符串 = 成功之前就失败了，这条字符串原样进状态栏。
    /// </summary>
    private string? InstallPluginFromPanel(string source, bool enable)
    {
        if (_installer is null || _inventory is null)
        {
            return I18n.Get("cp.plugins.install.unavailable");
        }

        if (!_installer.TryPrepare(source, out InstallPlan? plan, out string? error) || plan is null)
        {
            return error;
        }

        KernelLog.Write(
            $"plugin-install-prepared id={plan.Id} version={plan.Version} replaces={(plan.Replaces ? 1 : 0)}"
            + $" files={plan.Files} bytes={plan.Bytes}");
        if (!_installer.TryCommit(plan, out error))
        {
            return error;
        }

        if (enable && !_inventory.SetEnabled(plan.Id, true, out string? enableError))
        {
            // 装上了但没打开：这仍然算安装成功，把原因留在日志里（状态栏已经有成功文案）。
            KernelLog.Write($"plugin-install-enable-failed id={plan.Id} {enableError}");
        }

        return null;
    }

    /// <summary>面板「删除」入口：只允许删用户装进来的插件（内置的在管理面里被拒）。</summary>
    private string? RemovePluginFromPanel(string pluginId)
    {
        if (_inventory is null)
        {
            return I18n.Get("cp.plugins.remove.unavailable");
        }

        return _inventory.Remove(pluginId, out string? error) ? null : error;
    }

    /// <summary>按 id 找宿主侧的行（授权复核要读它的声明能力与已授予能力）。</summary>
    private static PluginRow? FindRow(PluginHost host, string pluginId)
    {
        foreach (PluginRow row in host.Roster())
        {
            if (string.Equals(row.Id, pluginId, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>按 id 找句柄（禁用/启用要按 id 说话，面板不持有句柄）。</summary>
    private static PluginHandle? FindHandle(PluginHost host, string pluginId)
    {
        try
        {
            foreach (PluginHandle handle in host.HandlesSnapshot())
            {
                if (string.Equals(handle.Manifest.Id, pluginId, StringComparison.Ordinal))
                {
                    return handle;
                }
            }
        }
        catch (TimeoutException)
        {
            KernelLog.Write("plugin-lookup-timeout scheduler-stalled");
        }

        return null;
    }
}
