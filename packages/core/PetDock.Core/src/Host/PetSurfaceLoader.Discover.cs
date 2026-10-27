// 名册的发现半边（D-03c 拆分，2026-10-28）：插件根 → 校验 → 按启动策略过滤 → 登记实现。
// 类主体在 PetSurfaceLoader.cs；两边是同一个 partial 类。
using System.Collections.Generic;
using System.IO;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

public sealed partial class PetSurfaceRoster
{
    /// <summary>从插件根采一次名册。任何一步失败都只登记原因、绝不抛：
    /// 名册为空是合法的启动态，判据的非 0 由调用方（自检）给。</summary>
    public static PetSurfaceRoster Discover(
        string? pluginsRoot,
        IReadOnlyCollection<string>? disabledPlugins = null,
        IReadOnlyCollection<string>? enabledPlugins = null,
        bool respectStartupPolicy = false)
    {
        var roster = new PetSurfaceRoster();
        if (string.IsNullOrWhiteSpace(pluginsRoot) || !Directory.Exists(pluginsRoot))
        {
            KernelLog.Write("surface-roster skipped: no plugins root");
            return roster;
        }

        roster.Root = pluginsRoot;
        using var host = new PluginHost(
            pluginsRoot,
            PluginHost.ResidentTickMs,
            autoLoad: false,
            consent: null,
            disabledPlugins: disabledPlugins,
            enabledPlugins: enabledPlugins,
            trustBuiltins: respectStartupPolicy);
        foreach (string dir in host.Discover(pluginsRoot))
        {
            roster.DiscoveredDirs++;
            Result<PluginManifest, IReadOnlyList<ManifestError>> result = host.ValidateManifest(dir);
            if (!result.IsOk)
            {
                roster.InvalidManifests++;
                foreach (ManifestError error in result.Error!)
                {
                    KernelLog.Write($"surface-manifest-error {Path.GetFileName(dir)} {error.Field}: {error.Message}");
                }

                continue;
            }

            roster.ValidManifests++;
            PluginManifest manifest = result.Ok!;
            if (respectStartupPolicy && !host.ShouldLoad(manifest))
            {
                // 名册是第二条装载路径：这里不认默认开关，就会把清单里已声明默认关的
                // 适配器又装进来（实测默认宠物拿到的是另一个行为层）。
                KernelLog.Write($"surface-skip-default plugin={manifest.Id} reason=manifest");
                continue;
            }

            roster.LoadPlugin(dir, manifest);
        }

        KernelLog.Write($"surface-roster {roster.Describe()}");
        return roster;
    }
}
