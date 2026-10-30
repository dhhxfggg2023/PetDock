// 名册的实例化半边（2026-10-29 从 PetSurfaceLoader.cs 拆出）：
// 「视图 + 行为 + 交互 + 状态」各自只有一个主实现，而陪伴层是每实例 0..n 个，
// 所以名册要同时保存两样东西：**实现对象**（角色）与**类型**（陪伴层要按宠物新造）。
// 拆文件的理由与 D-03c 的其它半块相同：主文件贴着 400 行上限。
using System;
using System.Collections.Generic;
using System.Reflection;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

public sealed partial class PetSurfaceRoster
{
    private readonly List<CompanionFactory> _companionFactories = new();

    /// <summary>已登记的陪伴层实现类型数（诊断行用；每只宠物按它新造实例）。</summary>
    public int CompanionTypes => _companionFactories.Count;

    /// <summary>
    /// 名册里第一个视图实现建出的宠物实例（无视图 → null）。
    /// assetRoots 是宿主为本实例解析好的素材根（RFC-15 §3.2）：空 = 没给，
    /// 视图实现回落到它自己的定位逻辑。
    /// freshView = true 时用**新的一份**实现对象建实例（换宠物要用）：
    /// 名册里的实现对象是装载时创建的那一个，两个实例共用它 ⇒ 新实例初始化时
    /// 旧实例还在用它，旧实例一释放就把新宠物一起带走了（视图实现大多不抗重复 init）。
    /// </summary>
    public PetInstance? BuildInstance(
        string instanceIdPrefix,
        IReadOnlyList<string>? assetRoots = null,
        bool freshView = false,
        bool imported = false,
        string? displayName = null)
    {
        IPetView? view = View;
        if (view is null)
        {
            return null;
        }

        string pluginId = Find<IPetView>()!.PluginId;
        if (freshView)
        {
            view = FreshView(view);
        }

        // 行为层也必须是**每实例一份**（2026-10-30）。名册里的对象是装载时造的那一个：
        // 多只宠物共用一个 IBrain 时，行为层的私有状态（当前片段、散步目标、休息计时、
        // 上次观察到的窗口位置）就是共享的 —— 每只 tick 一次就互相改写一次。
        // 实测（把真 brain 喂进确定性模拟）：共用时两只宠物会挤到一起、一路走到墙边，
        // 分开后一直保持各自的位置。用户看到的是「宠物会自己慢慢往一边走」。
        // 与 FreshView 同一条纪律：造不出来就退回原对象（宁可行为错乱，也不让宠物起不来）。
        IBrain? brain = FreshBrain(Brain, out bool ownsBrain);

        // RFC-23 §3.3：行为层可能是另一个插件，日志归属要按各自 id 记（记错了比不记更坏）。
        string? brainPluginId = Find<IBrain>()?.PluginId;
        PetInstance instance = new(
            $"{instanceIdPrefix}.{pluginId}", view, brain, Surfaces, StateLayer, pluginId, "dll", assetRoots, brainPluginId, imported, displayName, ownsBrain);
        AttachStateStore(instance, assetRoots);
        instance.InstallCompanions(CreateCompanions());
        return instance;
    }

    /// <summary>
    /// 按清单的 contributes.companions 把类型登记下来（装配期一次）。
    /// 与其它贡献点的差别：这里登记的是**类型**，不是对象 —— 每只宠物要各自一份数值。
    /// 一个类型都对不上时只记日志（不整体拒绝：插件可能同时贡献了别的角色）。
    /// </summary>
    private void RegisterCompanions(PluginManifest manifest, Assembly assembly)
    {
        IReadOnlyList<string>? names = DeclaredCompanions(manifest);
        if (names is null || names.Count == 0)
        {
            return;
        }

        foreach (string name in names)
        {
            Type? type = assembly.GetType(name, throwOnError: false);
            if (type is null || type.IsAbstract || type.IsInterface || !typeof(IPetCompanion).IsAssignableFrom(type))
            {
                _rejected.Add(new SurfaceRejection(manifest.Id, $"companion-not-a-role:{name}"));
                KernelLog.Write($"companion-reject plugin={manifest.Id} type={name} reason=not-a-companion");
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                _rejected.Add(new SurfaceRejection(manifest.Id, $"companion-no-default-ctor:{name}"));
                KernelLog.Write($"companion-reject plugin={manifest.Id} type={name} reason=no-default-ctor");
                continue;
            }

            _companionFactories.Add(new CompanionFactory(manifest.Id, type.FullName ?? type.Name, type));
            KernelLog.Write($"companion-bind plugin={manifest.Id} type={type.FullName}");
        }
    }

    /// <summary>清单声明的陪伴层类型名；没声明时 null。</summary>
    private static IReadOnlyList<string>? DeclaredCompanions(PluginManifest manifest)
    {
        PluginContributes? contributes = manifest.Contributes;
        if (contributes is null)
        {
            return null;
        }

        return contributes.Points.TryGetValue("companions", out IReadOnlyList<string>? names) ? names : null;
    }

    /// <summary>
    /// 为一只新实例造陪伴层。造不出来就跳过并记日志（一只陪伴层坏掉不该让宠物起不来）。
    /// </summary>
    private IReadOnlyList<IPetCompanion> CreateCompanions()
    {
        if (_companionFactories.Count == 0)
        {
            return Array.Empty<IPetCompanion>();
        }

        var created = new List<IPetCompanion>(_companionFactories.Count);
        foreach (CompanionFactory factory in _companionFactories)
        {
            try
            {
                if (Activator.CreateInstance(factory.Type) is IPetCompanion companion)
                {
                    created.Add(companion);
                }
            }
            catch (Exception error)
            {
                KernelLog.Write($"companion-construct-failed plugin={factory.PluginId} type={factory.TypeName}"
                    + $" {error.GetType().Name}: {error.Message}");
            }
        }

        return created;
    }

    /// <summary>
    /// 造一份新的行为层对象（同一类型、无参构造）。owned = true 表示**这份是本实例的**，
    /// 释放时要由实例自己 DisposeAsync（共用对象不能由某一只顺带销毁）。
    /// 造不出来（无参构造缺失 / 构造抛）就退回原对象，并把 owned 留在 false ——
    /// 与 FreshView 同一条取舍：宁可退回共享对象，也不让宠物起不来。
    /// </summary>
    private static IBrain? FreshBrain(IBrain? original, out bool owned)
    {
        owned = false;
        if (original is null)
        {
            return null;
        }

        try
        {
            Type type = original.GetType();
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                return original;
            }

            if (Activator.CreateInstance(type) is IBrain created)
            {
                owned = true;
                return created;
            }

            return original;
        }
        catch (Exception error)
        {
            KernelLog.Write($"brain-fresh-failed {error.GetType().Name}: {error.Message}");
            return original;
        }
    }

    /// <summary>
    /// 造一份新的视图实现对象（同一类型、无参构造）。
    /// 造不出来就退回原对象：换宠物失败时旧宠物还在，不该因为「造不出新对象」就空桌。
    /// </summary>
    private static IPetView FreshView(IPetView original)
    {
        try
        {
            Type type = original.GetType();
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                return original;
            }

            return Activator.CreateInstance(type) as IPetView ?? original;
        }
        catch (Exception error)
        {
            KernelLog.Write($"surface-fresh-view-failed {error.GetType().Name}: {error.Message}");
            return original;
        }
    }

    /// <summary>一条陪伴层登记（插件 id + 类型；实例按宠物新造）。</summary>
    private sealed record CompanionFactory(string PluginId, string TypeName, Type Type);
}
