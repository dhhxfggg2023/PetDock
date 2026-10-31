// 插件清单（2026-10-28）：面板看到的**全部**插件，而不只是「已经装载成功的那些」。
//
// 为什么需要这一层：PluginHost.Roster() 只报句柄，被用户在设置里关掉的、策略默认关闭的
// 适配器根本不出现 —— 面板于是连「有这只插件、你可以打开它」都说不出来。
// 这一层扫全部插件根，把「装了什么」与「开没开」分开回答；写动作（开/关/删）也收在这里，
// 面板仍然只发意图，不自己动目录。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

/// <summary>插件页一行的管理视角（纯值）。Origin 是 builtin / user；Removable 由清单的 essential 决定：
/// 系统必需的插件不可删，其余（含内置的可选插件）都可以删。</summary>
public sealed record PluginEntry(
    string Id,
    string Name,
    string Version,
    int Tier,
    IReadOnlyList<string> Capabilities,
    bool Loaded,
    bool Enabled,
    bool Removable,
    bool Essential,
    string Origin,
    string Dir,
    string? Description = null);

/// <summary>插件目录的合并视图 + 开/关/删三个动作。线程模型：方法可以在后台线程调用，
/// 宿主动作由 PluginHost 自己封送到调度线程；删除只碰扫描到的插件目录，系统必需插件由
/// 清单 essential 保护。</summary>
internal sealed partial class PluginInventory
{
    private readonly PluginHost _host;
    private readonly UserSettingsStore? _settings;
    private readonly string _userRoot;

    public PluginInventory(PluginHost host, UserSettingsStore? settings, string userRoot)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings;
        _userRoot = Path.GetFullPath(userRoot);
    }

    /// <summary>用户插件根（删除与安装只允许在这里发生）。</summary>
    public string UserRoot => _userRoot;

    /// <summary>扫描全部插件根，合并「装载状态」与「用户开关」。</summary>
    public IReadOnlyList<PluginEntry> Rows()
    {
        UserSettings settings = _settings?.Load() ?? UserSettings.Default;
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (PluginRow row in _host.Roster())
            {
                loaded.Add(row.Id);
            }
        }
        catch (TimeoutException)
        {
            KernelLog.Write("inventory-roster-timeout scheduler-stalled");
        }

        var rows = new List<PluginEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string dir in CandidateDirs())
        {
            Result<PluginManifest, IReadOnlyList<ManifestError>> result = ManifestValidator.Validate(dir);
            if (!result.IsOk)
            {
                KernelLog.Write($"inventory-skip dir={dir} manifest-invalid");
                continue;
            }

            PluginManifest manifest = result.Ok!;
            if (!seen.Add(manifest.Id))
            {
                continue; // 同一个 id 出现在两个根里：先扫到的那份生效（与宿主装载同序）
            }

            bool isLoaded = loaded.Contains(manifest.Id);
            bool userOwned = IsUnderUserRoot(dir);
            rows.Add(new PluginEntry(
                manifest.Id,
                manifest.Name,
                manifest.Version,
                manifest.Tier,
                new List<string>(manifest.Capabilities),
                isLoaded,
                EffectiveEnabled(manifest.Id, settings, isLoaded, manifest.DefaultEnabled, userOwned),
                !manifest.Essential,
                manifest.Essential,
                userOwned ? "user" : "builtin",
                dir,
                manifest.Description));
        }

        rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    /// <summary>打开/关闭一只插件。打开 = 落盘 pluginsEnabled + 让宿主立即装载；
    /// 关闭 = 落盘 pluginsDisabled + 让宿主禁用（不卸载）。返回 false 时 error 说清楚原因。</summary>
    public bool SetEnabled(string pluginId, bool enabled, out string? error)
    {
        error = null;
        try
        {
            if (_settings is not null)
            {
                // 原子读-改-写（UserSettingsStore.Update）：插件启停跑在后台线程，切语言/记位置
                // 在 UI 线程，各写各的那一份快照会把对方刚写的字段改回去。
                _settings.Update(actual =>
                {
                    List<string> latestDisabled = actual.DisabledPlugins is null
                        ? new List<string>()
                        : new List<string>(actual.DisabledPlugins);
                    List<string> latestEnabled = actual.EnabledPlugins is null
                        ? new List<string>()
                        : new List<string>(actual.EnabledPlugins);

                    if (enabled)
                    {
                        latestDisabled.RemoveAll(id => string.Equals(id, pluginId, StringComparison.Ordinal));
                        if (!latestEnabled.Contains(pluginId, StringComparer.Ordinal))
                        {
                            latestEnabled.Add(pluginId);
                        }
                    }
                    else
                    {
                        latestEnabled.RemoveAll(id => string.Equals(id, pluginId, StringComparison.Ordinal));
                        if (!latestDisabled.Contains(pluginId, StringComparer.Ordinal))
                        {
                            latestDisabled.Add(pluginId);
                        }
                    }

                    return actual with
                    {
                        DisabledPlugins = latestDisabled,
                        EnabledPlugins = latestEnabled,
                    };
                });
            }

            if (enabled)
            {
                _host.EnableById(pluginId).GetAwaiter().GetResult();
            }
            else
            {
                _host.DisableById(pluginId).GetAwaiter().GetResult();
            }

            KernelLog.Write($"plugin-set-enabled plugin={pluginId} enabled={(enabled ? 1 : 0)}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            KernelLog.Write($"plugin-set-enabled-failed plugin={pluginId} {error}");
            return false;
        }
    }

    /// <summary>删除一只非系统必需的插件：先卸载，再删目录，最后从两个设置表里清掉。
    /// 内置与用户安装走同一条路径；系统必需的插件由清单声明 essential，绝不能从界面上删掉。</summary>
    public bool Remove(string pluginId, out string? error)
    {
        error = null;
        PluginEntry? entry = null;
        foreach (PluginEntry candidate in Rows())
        {
            if (string.Equals(candidate.Id, pluginId, StringComparison.Ordinal))
            {
                entry = candidate;
                break;
            }
        }

        if (entry is null)
        {
            error = I18n.Get("cp.plugins.remove.missing");
            return false;
        }

        if (entry.Essential || !entry.Removable)
        {
            error = I18n.Get("cp.plugins.remove.essential");
            return false;
        }

        try
        {
            _host.Unload(pluginId).GetAwaiter().GetResult();
            string trash = MoveToTrash(entry);
            PruneTrash();

            if (_settings is not null)
            {
                _settings.Update(current => current with
                {
                    DisabledPlugins = current.DisabledPlugins?.Where(id => id != pluginId).ToList(),
                    EnabledPlugins = current.EnabledPlugins?.Where(id => id != pluginId).ToList(),
                });
            }

            KernelLog.Write($"plugin-removed plugin={pluginId} dir={entry.Dir} trash={trash}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            KernelLog.Write($"plugin-remove-failed plugin={pluginId} {error}");
            return false;
        }
    }

    /// <summary>
    /// 删除 = 挪进 plugins/.removed/&lt;id&gt;-&lt;时间戳&gt;，不再 Directory.Delete 整棵树。
    /// 一次误点不该把随程序发来的插件从盘上抹掉（没有重装入口，用户也找不回原文件）；
    /// 扫描只认插件根下**直接**带清单的目录，.removed 里的这一层不会被当成插件再装一遍。
    /// 跨卷时（仓库在 E:、数据在 C:）整棵树搬不过去，退回「复制 → 删源」。
    /// </summary>
    private string MoveToTrash(PluginEntry entry) => Park(entry.Dir, entry.Id, _userRoot);

    /// <summary>
    /// 把一棵目录挪进回收站。删除入口与「覆盖安装半途失败的那份旧目录」共用这一条：
    /// 两个调用方要的是同一件事（看得见、还拿得回来），各写一遍只会漂。
    /// 跨卷时 Directory.Move 抛 IOException，退回「复制 → 删源」。
    /// </summary>
    internal static string Park(string dir, string pluginId, string userRoot)
    {
        string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
        string trashRoot = Path.Combine(userRoot, TrashFolder);
        Directory.CreateDirectory(trashRoot);
        string target = Path.Combine(trashRoot, pluginId + "-" + stamp);
        try
        {
            Directory.Move(dir, target);
        }
        catch (IOException)
        {
            CopyTree(dir, target);
            Directory.Delete(dir, recursive: true);
        }

        return target;
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>该 id 当前是否启用：显式关 &gt; 显式开 &gt; 已装载 &gt; manifest 默认。</summary>
    private static bool EffectiveEnabled(
        string pluginId,
        UserSettings settings,
        bool loaded,
        bool manifestDefault,
        bool userOwned)
    {
        if (Contains(settings.DisabledPlugins, pluginId))
        {
            return false;
        }
        if (Contains(settings.EnabledPlugins, pluginId))
        {
            return true;
        }
        if (loaded)
        {
            return true;
        }
        return manifestDefault && !userOwned;
    }

    private static bool Contains(IReadOnlyList<string>? ids, string pluginId)
    {
        if (ids is null)
        {
            return false;
        }
        foreach (string id in ids)
        {
            if (string.Equals(id, pluginId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private IEnumerable<string> CandidateDirs()
    {
        foreach (string root in _host.Roots)
        {
            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(root).OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                KernelLog.Write($"inventory-root-failed root={root} {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            foreach (string dir in dirs)
            {
                if (File.Exists(Path.Combine(dir, ManifestValidator.ManifestFileName)))
                {
                    yield return Path.GetFullPath(dir);
                }
            }
        }
    }

    private bool IsUnderUserRoot(string dir)
    {
        string full = Path.GetFullPath(dir);
        return full.StartsWith(_userRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
