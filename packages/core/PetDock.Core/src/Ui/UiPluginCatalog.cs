// UI 插件目录（2026-10-28）：从插件清单发现「贡献页面」的受信任插件。
//
// 为什么只接受 tier 0：页面代码与内核同进程、同在 UI 线程。tier 1/2 的第三方代码
// 不应该拿到 UI 线程与 DWM/消息泵；它们将来的页面走声明式描述，由壳解释，而不是
// Assembly.LoadFrom 进 UI 进程。这里把信任边界写在装载点，而不是靠名单。
//
// 清单键：contributes.pages = ["命名空间.类型名", ...]；entry 指向 DLL。
// 没有 pages 的插件完全不装载 —— 普通行为层/展示层插件不该为了「UI 插件目录」被加载一次。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal static class UiPluginCatalog
{
    /// <summary>
    /// 扫描一个插件根并建页。失败不抛：一个坏插件不能让面板打不开，
    /// 失败的插件写一行 kernel 日志，壳继续显示其它页。
    /// </summary>
    public static IReadOnlyList<IUiPlugin> Load(
        string? pluginsRoot,
        IUiPageContext context,
        IReadOnlyCollection<string>? disabled = null)
    {
        if (string.IsNullOrWhiteSpace(pluginsRoot) || !Directory.Exists(pluginsRoot))
        {
            return Array.Empty<IUiPlugin>();
        }

        var plugins = new List<IUiPlugin>();
        foreach (string dir in SafeDirectories(pluginsRoot))
        {
            try
            {
                IUiPlugin? plugin = LoadOne(dir, context, disabled);
                if (plugin is not null)
                {
                    plugins.Add(plugin);
                }
            }
            catch (Exception error)
            {
                KernelLog.Write($"ui-plugin-skip dir={Path.GetFileName(dir)} {error.GetType().Name}: {error.Message}");
            }
        }

        return plugins;
    }

    private static IUiPlugin? LoadOne(
        string dir,
        IUiPageContext context,
        IReadOnlyCollection<string>? disabled)
    {
        Result<PluginManifest, IReadOnlyList<ManifestError>> result = ManifestValidator.Validate(dir);
        if (!result.IsOk || result.Ok is null)
        {
            return null;
        }

        PluginManifest manifest = result.Ok;
        if (!manifest.Essential
            && disabled is not null
            && disabled.Contains(manifest.Id))
        {
            // 停用的界面插件不再建页；清单声明 essential 的随包界面例外（否则无页面可显示）。
            KernelLog.Write($"ui-plugin-skip plugin={manifest.Id} reason=disabled");
            return null;
        }

        if (manifest.Tier != 0)
        {
            KernelLog.Write($"ui-plugin-skip plugin={manifest.Id} reason=tier-{manifest.Tier}");
            return null;
        }

        if (manifest.Contributes is null
            || !manifest.Contributes.Points.TryGetValue("pages", out IReadOnlyList<string>? pages)
            || pages.Count == 0)
        {
            return null;
        }

        string? assemblyPath = ResolveAssembly(dir, manifest.Entry);
        if (assemblyPath is null)
        {
            KernelLog.Write($"ui-plugin-skip plugin={manifest.Id} reason=assembly-missing entry={manifest.Entry}");
            return null;
        }

        Assembly assembly = Assembly.LoadFrom(assemblyPath);
        foreach (string typeName in pages)
        {
            Type? type = assembly.GetType(typeName, throwOnError: false);
            if (type is null || !typeof(IUiPlugin).IsAssignableFrom(type) || Activator.CreateInstance(type) is not IUiPlugin plugin)
            {
                KernelLog.Write($"ui-plugin-skip plugin={manifest.Id} type={typeName} reason=not-iu-iplugin");
                continue;
            }

            // 壳只认清单 id：类型自己写的 Id 不参与身份判定，避免两个插件冒用同一个 id。
            return new ManifestUiPlugin(manifest.Id, plugin);
        }

        return null;
    }

    private static string? ResolveAssembly(string dir, string entry)
    {
        if (!string.IsNullOrWhiteSpace(entry))
        {
            string candidate = Path.IsPathRooted(entry) ? entry : Path.GetFullPath(Path.Combine(dir, entry));
            if (File.Exists(candidate) && candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        // 清单 entry 常写成构建输出路径；发行包里直接是 DLL 文件名。
        string? fallback = SafeFiles(dir, "*.dll")
            .OrderByDescending(path => Path.GetFileName(path).StartsWith("PetDock.Ui", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        return fallback;
    }

    private static IReadOnlyList<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> SafeFiles(string dir, string pattern)
    {
        try
        {
            return Directory.GetFiles(dir, pattern);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private sealed class ManifestUiPlugin : IUiPlugin
    {
        private readonly IUiPlugin _inner;

        public ManifestUiPlugin(string id, IUiPlugin inner)
        {
            Id = id;
            _inner = inner;
        }

        public string Id { get; }

        public IReadOnlyList<IUiPage> CreatePages(IUiPageContext context) => _inner.CreatePages(context);
    }
}
