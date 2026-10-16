// docs/11 §1 的四接口怎么被宿主发现（t1 契约 §1.3 第四步）。**不新开发现路径**：
// 唯一入口仍是 PluginHost.Discover + ValidateManifest（docs/21），本文件只做 Discover 之后那一步。
//   1. kind → 接口，映射见 InterfaceFor（kind 字面值经 PluginKind 包装，HostingTypes.cs 的拼接先例）。
//   2. entry 是相对插件目录的 .dll 时用 Assembly.LoadFrom 并探测实现类；tier 必须为 0
//      （tier 1 的执行体是 wasm 沙箱，托管 ABI 面不是它）。entry 不是 .dll 时不载入代码。
//   3. 多个候选按类型全名 Ordinal 取第一个，并记 surface-ambiguous（不静默挑一个）。
//   4. 一个插件目录里的多个实现类都登记（§3 的视图插件可同时暴露 M0 的内容源工厂）。
// 不认识的 kind（importer / service）一律跳过，不做猜测。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>一个已载入的实现类（哪个插件、哪个类型、服务于哪个接口）。</summary>
public sealed record SurfaceRecord(string PluginId, string PluginDir, string TypeName, Type Interface, object Implementation)
{
    public override string ToString() => $"{Interface.Name}:{TypeName} plugin={PluginId}";
}

/// <summary>实现不了任何接口的类型（如实上报，不静默跳过）。</summary>
public sealed record SurfaceRejection(string PluginId, string Reason)
{
    public override string ToString() => $"{PluginId}:{Reason}";
}

/// <summary>已装配的视图面名册：视图插件的实现，与它旁边可能存在的 M0 内容源工厂。
/// 宿主启动时采一次的只读快照（热重载后重建，见 PluginHost 的 reload 钩子）。</summary>
public sealed class PetSurfaceRoster
{
    private readonly List<SurfaceRecord> _records = new();
    private readonly List<SurfaceRejection> _rejected = new();
    private readonly List<string> _overruns = new();

    public string Root { get; private set; } = string.Empty;

    public int DiscoveredDirs { get; private set; }

    public int ValidManifests { get; private set; }

    public int InvalidManifests { get; private set; }

    public IReadOnlyList<SurfaceRecord> Records => _records;

    public IReadOnlyList<SurfaceRejection> Rejected => _rejected;

    /// <summary>
    /// 已知的文档越界（每次装载的**实况**，不是注释里的一句声明）：目前只有一种 ——
    /// tier 0 + entry 是托管 .dll，与 docs/13 §2「Tier 0 = 声明式资源包，无展开代码」字面冲突。
    /// 单独暴露成一个列表，是为了让它出现在每次装配的诊断行上（`overruns=N`）与 --drivetest 的
    /// 判据里 —— 队长 2026-10-16 的要求：这个越界必须**显式可见**，不许让它看起来像合规。
    /// 方向未定，由 t6 按 docs/40 D-08c 判。
    /// </summary>
    public IReadOnlyList<string> Overruns => _overruns;

    /// <summary>第一个视图实现（按插件 id + 类型名 Ordinal 取首；无则 null）。</summary>
    public IPetView? View => First<IPetView>();

    /// <summary>视图实现的具体类型全名 —— 自检判据 `source=` 那一项（必须非兜底）。</summary>
    public string ViewTypeName => Find<IPetView>()?.TypeName ?? string.Empty;

    public bool HasView => View is not null;

    public IBrain? Brain => First<IBrain>();

    public IPetState? StateLayer => First<IPetState>();

    public IReadOnlyList<IPointerSurface> Surfaces =>
        _records.Where(r => r.Interface == typeof(IPointerSurface))
            .Select(r => (IPointerSurface)r.Implementation).ToList();

    /// <summary>M0 的过渡内容源工厂（t1 契约 §4：保留不改名）。</summary>
    public IFrameContentProvider? ContentProvider => Find<IFrameContentProvider>()?.Implementation as IFrameContentProvider;

    public bool HasContentProvider => ContentProvider is not null;

    /// <summary>从插件根采一次名册。任何一步失败都只登记原因、绝不抛：
    /// 名册为空是合法的启动态，判据的非 0 由调用方（自检）给。</summary>
    public static PetSurfaceRoster Discover(string? pluginsRoot)
    {
        var roster = new PetSurfaceRoster();
        if (string.IsNullOrWhiteSpace(pluginsRoot) || !Directory.Exists(pluginsRoot))
        {
            KernelLog.Write("surface-roster skipped: no plugins root");
            return roster;
        }

        roster.Root = pluginsRoot;
        using var host = new PluginHost(pluginsRoot, PluginHost.ResidentTickMs, autoLoad: false);
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
            roster.LoadPlugin(dir, result.Ok!);
        }

        KernelLog.Write($"surface-roster {roster.Describe()}");
        return roster;
    }

    /// <summary>名册里第一个视图实现建出的宠物实例（无视图 → null）。</summary>
    public PetInstance? BuildInstance(string instanceIdPrefix)
    {
        IPetView? view = View;
        if (view is null)
        {
            return null;
        }

        string pluginId = Find<IPetView>()!.PluginId;
        return new PetInstance($"{instanceIdPrefix}.{pluginId}", view, Brain, Surfaces, StateLayer, pluginId, "dll");
    }

    /// <summary>一行诊断（自检与日志共用）。</summary>
    public string Describe() =>
        $"root={Root} dirs={DiscoveredDirs} valid={ValidManifests} invalid={InvalidManifests}"
        + $" records={_records.Count} view={(HasView ? ViewTypeName : "(none)")}"
        + $" provider={(HasContentProvider ? "yes" : "no")} rejected={_rejected.Count}"
        + $" overruns={_overruns.Count}";

    /// <summary>登记一个宿主内建的实现（自检桩件走这条路）。</summary>
    internal void Add(string pluginId, string typeName, Type iface, object implementation)
    {
        _records.Add(new SurfaceRecord(pluginId, "(stub)", typeName, iface, implementation));
        _records.Sort(Compare);
    }

    private void LoadPlugin(string dir, PluginManifest manifest)
    {
        Type? target = InterfaceFor(manifest.Kind);
        if (target is null)
        {
            KernelLog.Write($"surface-skip plugin={manifest.Id} kind={manifest.Kind.Value} (not an ABI role in M2)");
            return;
        }

        if (!manifest.Entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            // docs/13 §2 的 Tier 0 声明式资源包：没有展开代码，不是错误，只是没有实现可登记。
            KernelLog.Write($"surface-skip plugin={manifest.Id} entry={manifest.Entry} (no code unit)");
            return;
        }

        if (manifest.Tier != 0)
        {
            _rejected.Add(new SurfaceRejection(manifest.Id, $"tier-{manifest.Tier}-entry-dll"));
            KernelLog.Write($"surface-reject plugin={manifest.Id} tier={manifest.Tier} reason=managed-entry-requires-tier-0");
            return;
        }

        // ⚠️ 已知的文档越界，**显式记账、不埋在注释里**（队长 2026-10-16 要求：别让它看起来像合规）：
        // docs/13 §2 对 Tier 0 的逐字定义是「声明式资源包，**无展开代码**，主机负责读包」，
        // 而本分支装载的是托管 .NET 程序集 —— **有代码**。字面冲突，方向未定（走 RFC 加
        // 「第一方托管内置实现」条款，或把内置实现移出 manifest/tier 路径由 core 注册），
        // 由 t6 按 docs/40 D-08c 判。本行为记录在每次装载的 stderr 上，使这个越界在判据输出里可见。
        _overruns.Add($"{manifest.Id}:tier-0-with-managed-entry:{manifest.Entry}");
        KernelLog.Write(
            $"surface-overrun plugin={manifest.Id} tier=0 entry={manifest.Entry}"
            + " docs13-tier0-says-no-code t6-D-08c-pending");

        Assembly assembly;
        try
        {
            assembly = Assembly.LoadFrom(Path.Combine(dir, manifest.Entry));
        }
        catch (Exception error)
        {
            // 找不到 / 加载失败（bad image、缺依赖）都落这里：自检的判据要求这种情形可见。
            _rejected.Add(new SurfaceRejection(manifest.Id, $"assembly-load-failed:{error.GetType().Name}"));
            KernelLog.Write($"surface-reject plugin={manifest.Id} entry={manifest.Entry} err={error.GetType().Name}: {error.Message}");
            return;
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            _rejected.Add(new SurfaceRejection(manifest.Id, "types-unreadable"));
            KernelLog.Write($"surface-reject plugin={manifest.Id} err=ReflectionTypeLoadException first={error.LoaderExceptions.FirstOrDefault()?.Message}");
            return;
        }

        bool any = false;
        foreach (Type type in types)
        {
            if (type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            foreach (Type iface in InterfacesFor(type))
            {
                object? instance = null;
                try
                {
                    instance = Activator.CreateInstance(type);
                }
                catch (Exception error)
                {
                    _rejected.Add(new SurfaceRejection(manifest.Id, $"{type.Name}:construct-failed:{error.GetType().Name}"));
                    continue;
                }

                if (instance is null)
                {
                    continue;
                }

                _records.Add(new SurfaceRecord(manifest.Id, dir, type.FullName ?? type.Name, iface, instance));
                KernelLog.Write($"surface-bind plugin={manifest.Id} iface={iface.Name} type={type.FullName} dll={Path.GetFileName(manifest.Entry)}");
                any = true;
            }
        }

        if (!any)
        {
            _rejected.Add(new SurfaceRejection(manifest.Id, $"no-implementation-of:{target.Name}"));
            KernelLog.Write($"surface-reject plugin={manifest.Id} reason=no-{target.Name}-implementation");
        }
    }

    /// <summary>这个实现类服务于哪些接口（一个类可以同时是内容源工厂与视图实现）。</summary>
    private static IEnumerable<Type> InterfacesFor(Type type)
    {
        Type[] candidates = { typeof(IPetView), typeof(IBrain), typeof(IPointerSurface), typeof(IPetState), typeof(IFrameContentProvider) };
        foreach (Type iface in candidates)
        {
            if (iface.IsAssignableFrom(type))
            {
                yield return iface;
            }
        }
    }

    /// <summary>manifest 的 kind → docs/11 的四接口之一（不认识的 kind 返回 null）。</summary>
    private static Type? InterfaceFor(PluginKind kind)
    {
        if (kind == PluginKind.Present)
        {
            return typeof(IPetView);
        }

        if (kind == PluginKind.Brain)
        {
            return typeof(IBrain);
        }

        if (kind == PluginKind.Interact)
        {
            return typeof(IPointerSurface);
        }

        return kind == PluginKind.State ? typeof(IPetState) : null;
    }

    private SurfaceRecord? Find<T>() where T : class
    {
        foreach (SurfaceRecord record in _records)
        {
            if (record.Interface == typeof(T))
            {
                return record;
            }
        }

        return null;
    }

    private T? First<T>() where T : class => Find<T>()?.Implementation as T;

    private static int Compare(SurfaceRecord a, SurfaceRecord b)
    {
        int byPlugin = string.CompareOrdinal(a.PluginId, b.PluginId);
        return byPlugin != 0 ? byPlugin : string.CompareOrdinal(a.TypeName, b.TypeName);
    }

    /// <summary>每个接口各有几个候选（宿主不静默挑一个：自检读它）。</summary>
    public string AmbiguityReport()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SurfaceRecord record in _records)
        {
            counts.TryGetValue(record.Interface.Name, out int n);
            counts[record.Interface.Name] = n + 1;
        }

        var parts = new List<string>();
        foreach (KeyValuePair<string, int> pair in counts)
        {
            parts.Add($"{pair.Key}={pair.Value}");
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join(" ", parts);
    }
}
