// 快照装配（RFC-12 §2.2）：把宿主与实例的**只读**读数变成面板能直接摆放的行。
// 这里不做任何判定、不改任何状态——判定在宿主，展示在面板（原规格 18 §2.5）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal static class UiRoster
{
    /// <summary>
    /// 宠物从哪来：用户导入的、发行物自带的、还是内核兜底的桩件。
    /// 不直接显示 <see cref="PetInstance.SourceKind"/> —— 那是 "dll"/"stub"，用户看不懂，
    /// 也不回答「这只宠物是从哪来的」（2026-10-28 实测面板里整列写着 dll）。
    /// </summary>
    private static string DescribeSource(PetInstance pet)
    {
        if (pet.Imported)
        {
            return I18n.Get("cp.pets.source.imported");
        }

        if (string.Equals(pet.SourceKind, "stub", StringComparison.Ordinal))
        {
            return I18n.Get("cp.pets.source.builtin");
        }

        return string.IsNullOrEmpty(pet.SourceKind)
            ? I18n.Get("ui.roster.source.kernelFallback")
            : I18n.Get("cp.pets.source.plugin");
    }

    /// <summary>
    /// 装配一次快照。pets 允许为空（常驻路径只装配一个实例，且装配失败时确实没有宠物）；
    /// plugins 来自宿主只读花名册；settings 与 inventory 由调用方给出。
    /// </summary>
    public static UiSnapshot Build(
        PetInstance? pet,
        PluginHost? host,
        SettingsView? settings = null,
        PluginInventory? inventory = null,
        IReadOnlyList<PetLine>? extraPets = null,
        string? primaryPetId = null,
        bool primaryOnDesktop = true)
    {
        var pets = new List<PetLine>(1);
        if (pet is not null)
        {
            pets.Add(new PetLine(
                Id: primaryPetId ?? pet.Id,
                // 用户导入的是「我的桌宠」，不是 user.-----；内置那只显示「内置宠物」，不是内部实例 id。
                Name: pet.Imported ? pet.DisplayName : I18n.Get("cp.pets.name.builtin"),
                Source: DescribeSource(pet),
                State: PluginHost.DescribeInstanceState(pet.State)
                    + (pet.BrainDegraded ? I18n.Get("ui.roster.state.degraded") : string.Empty)
                    + (primaryOnDesktop ? string.Empty : " · " + I18n.Get("cp.pets.state.hidden")),
                Memory: ProcessMemoryMb())
            {
                OnDesktop = primaryOnDesktop,
            });
        }

        // 主窗之外的那几只（多宠物）：行由宿主侧按槽位装配，这里只追加，不重新判定。
        if (extraPets is not null)
        {
            pets.AddRange(extraPets);
        }

        var plugins = new List<PluginLine>();
        var loaded = new Dictionary<string, PluginRow>(StringComparer.Ordinal);
        if (host is not null)
        {
            foreach (PluginRow row in host.Roster())
            {
                loaded[row.Id] = row;
            }
        }

        if (inventory is not null)
        {
            // 管理视角：装了但没开的插件也要出现在表里，否则用户没有入口把它打开。
            foreach (PluginEntry entry in inventory.Rows())
            {
                loaded.TryGetValue(entry.Id, out PluginRow? row);
                plugins.Add(new PluginLine(
                    Id: entry.Id,
                    Name: entry.Name,
                    Version: entry.Version,
                    Tier: entry.Tier,
                    State: DescribePluginState(row, entry),
                    Granted: row?.Granted ?? 0,
                    Source: I18n.Get(entry.Origin == "user" ? "cp.plugins.origin.user" : "cp.plugins.origin.builtin"),
                    Enabled: entry.Enabled,
                    Loaded: entry.Loaded,
                    Removable: entry.Removable,
                    Essential: entry.Essential));
            }
        }
        else if (host is not null)
        {
            foreach (PluginRow row in loaded.Values)
            {
                plugins.Add(new PluginLine(
                    Id: row.Id,
                    Name: row.Name,
                    Version: row.Version,
                    Tier: row.Tier,
                    State: row.State.ToString(),
                    Granted: row.Granted));
            }
        }

        return new UiSnapshot(pets, plugins, settings);
    }

    /// <summary>
    /// 插件页「状态」列的文案：已装载的看生命周期，未装载的看用户开关。
    /// 不直接显示枚举名 —— 用户读不懂 Running / Suspended。
    /// </summary>
    private static string DescribePluginState(PluginRow? row, PluginEntry entry)
    {
        if (row is null)
        {
            return entry.Enabled ? I18n.Get("cp.plugins.state.pending") : I18n.Get("cp.plugins.state.off");
        }

        return row.State switch
        {
            PluginLifecycleState.Running => I18n.Get("cp.plugins.state.running"),
            PluginLifecycleState.Suspended => I18n.Get("cp.plugins.state.suspended"),
            _ => row.State.ToString(),
        };
    }

    /// <summary>
    /// 进程级私有工作集。宠物与内核同进程（原规格 10 §3），所以面板上写「进程」不是我偷懒——
    /// 单只宠物的独占内存根本不存在，把它拆成「每宠物内存」会是一个编造的数字。
    /// </summary>
    public static string ProcessMemoryMb()
    {
        using Process self = Process.GetCurrentProcess();
        double mb = self.WorkingSet64 / (1024.0 * 1024.0);
        return I18n.Get("ui.roster.memory.process", mb.ToString("0.0", CultureInfo.InvariantCulture));
    }

    /// <summary>降级数（面板顶部汇总用）：实例级降级标记为真的宠物个数。</summary>
    public static int DegradedCount(PetInstance? pet) => pet is not null && pet.BrainDegraded ? 1 : 0;
}
