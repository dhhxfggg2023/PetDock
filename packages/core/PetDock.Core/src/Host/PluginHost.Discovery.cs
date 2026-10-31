// 插件发现与根探测（从 PluginHost.cs 拆出，D-03c）。纯路径逻辑：不碰句柄、不碰调度线程。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

public sealed partial class PluginHost
{
    /// <summary>发现（契约 §5.3）：非递归扫描，只认目录与 manifest 文件的存在，不解析内容。</summary>
    public IReadOnlyList<string> Discover(string pluginsRoot)
    {
        var dirs = new List<string>();
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(pluginsRoot))
            {
                // 覆盖安装半途失败会留下 <id>.bak-<时间戳> 这一份旧目录：它带着清单，却不是插件。
                // 早先这里只认「目录里有清单」，于是它被当成同 id 的第二只装进宿主 ——
                // 用户看到重复项、删掉一只另一只还在，而且再也不会被更新到（2026-10-31 审计）。
                if (PluginDirBackup.IsLeftover(dir))
                {
                    KernelLog.Write($"discover-skipped-leftover dir={dir}");
                    continue;
                }

                if (File.Exists(Path.Combine(dir, ManifestValidator.ManifestFileName)))
                {
                    dirs.Add(Path.GetFullPath(dir));
                }
            }
        }
        catch (Exception ex)
        {
            KernelLog.Write($"discover-failed {ex.GetType().Name}: {ex.Message}");
        }
        dirs.Sort(StringComparer.OrdinalIgnoreCase);
        return dirs;
    }

    /// <summary>校验（契约 §5.2）：原规格 21 全语义，错误逐条返回；成功即登记 id → 目录。</summary>
    public Result<PluginManifest, IReadOnlyList<ManifestError>> ValidateManifest(string pluginDir)
    {
        Result<PluginManifest, IReadOnlyList<ManifestError>> result = ManifestValidator.Validate(pluginDir);
        if (result.IsOk)
        {
            _dirById[result.Ok!.Id] = Path.GetFullPath(pluginDir);
        }
        return result;
    }

    private void AutoLoadAll()
    {
        if (_roots.Count == 0)
        {
            KernelLog.Write("host auto-load skipped: no plugins root");
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string dir in DiscoverAllDirs())
        {
            Result<PluginManifest, IReadOnlyList<ManifestError>> result = ValidateManifest(dir);
            if (!result.IsOk)
            {
                foreach (ManifestError error in result.Error!)
                {
                    KernelLog.Write($"manifest-error {error.Field}: {error.Message}");
                }
                continue;
            }
            PluginManifest manifest = result.Ok!;
            if (!seen.Add(manifest.Id))
            {
                // 同一个 id 出现在两个根里：先扫到的那份生效（用户根优先于内置根）。
                KernelLog.Write($"plugin-skipped-duplicate plugin={manifest.Id} dir={dir}");
                continue;
            }
            if (!ShouldLoad(manifest))
            {
                // 用户关掉的（下一轮生效）；或是清单声明 defaultEnabled:false 的适配器。
                KernelLog.Write(_skipIds.Contains(manifest.Id)
                    ? $"plugin-skipped-disabled plugin={manifest.Id}"
                    : $"plugin-skipped-default plugin={manifest.Id} reason=manifest");
                continue;
            }

            try
            {
                LoadCore(manifest, CancellationToken.None);
            }
            catch (PluginLoadException ex)
            {
                KernelLog.Write($"load-reject {manifest.Id}: {ex.Message}");
            }
        }
    }

    /// <summary>启动期装载决定：显式关 &gt; 显式开 &gt; manifest.defaultEnabled（只在 trustBuiltins 时生效）。</summary>
    internal bool ShouldLoad(PluginManifest manifest)
    {
        if (_skipIds.Contains(manifest.Id))
        {
            return false;
        }
        if (_enabledIds.Contains(manifest.Id))
        {
            return true;
        }
        if (!_trustBuiltins)
        {
            return true; // 自检/显式驱动的旧语义：不认识的也装载
        }
        return PluginPolicy.DefaultEnabled(manifest, IsBuiltin(manifest.Id));
    }

    /// <summary>按根序扫描全部候选目录（同路径去重）。根内按路径排序，保证装载顺序可复现。</summary>
    private IReadOnlyList<string> DiscoverAllDirs()
    {
        var dirs = new List<string>();
        foreach (string root in _roots)
        {
            foreach (string dir in Discover(root))
            {
                if (!dirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                {
                    dirs.Add(dir);
                }
            }
        }
        return dirs;
    }

    /// <summary>探测插件根目录：环境变量 PETDOCK_PLUGINS_ROOT 优先（须存在），
    /// 否则从 AppContext.BaseDirectory 逐级向上找 packages/plugins 或 plugins。
    /// 失败返回 null —— 宿主空转不致命（部署形态可能不带插件目录）。</summary>
    public static string? ProbePluginsRoot()
    {
        string? env = Environment.GetEnvironmentVariable(PluginsRootEnv);
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            return env;
        }
        string? baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            return null;
        }
        try
        {
            for (DirectoryInfo? dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
            {
                string packagesPlugins = Path.Combine(dir.FullName, "packages", "plugins");
                if (Directory.Exists(packagesPlugins))
                {
                    return packagesPlugins;
                }
                string plain = Path.Combine(dir.FullName, "plugins");
                if (Directory.Exists(plain))
                {
                    return plain;
                }
            }
        }
        catch (ArgumentException)
        {
            // 路径形态异常（如已到 UNC 根）—— 放弃向上探测
        }
        return null;
}
}
