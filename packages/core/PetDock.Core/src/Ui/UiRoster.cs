// 快照装配（RFC-12 §2.2）：把宿主与实例的**只读**读数变成面板能直接摆放的行。
// 这里不做任何判定、不改任何状态——判定在宿主，展示在面板（docs/18 §2.5）。
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
    /// 装配一次快照。pets 允许为空（常驻路径只装配一个实例，且装配失败时确实没有宠物）；
    /// plugins 来自宿主只读花名册；perf 由调用方给出（采样是显式动作，不在重绘里偷偷做）。
    /// </summary>
    public static UiSnapshot Build(PetInstance? pet, PluginHost? host, PerfSummary perf, SettingsView? settings = null)
    {
        var pets = new List<PetLine>(1);
        if (pet is not null)
        {
            pets.Add(new PetLine(
                Id: pet.Id,
                Name: pet.Id,
                Source: string.IsNullOrEmpty(pet.SourceKind) ? I18n.Get("ui.roster.source.kernelFallback") : pet.SourceKind,
                State: PluginHost.DescribeInstanceState(pet.State)
                    + (pet.BrainDegraded ? I18n.Get("ui.roster.state.degraded") : string.Empty),
                Memory: ProcessMemoryMb()));
        }

        var plugins = new List<PluginLine>();
        if (host is not null)
        {
            foreach (PluginRow row in host.Roster())
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

        return new UiSnapshot(pets, plugins, perf, settings);
    }

    /// <summary>
    /// 进程级私有工作集。宠物与内核同进程（docs/10 §3），所以面板上写「进程」不是我偷懒——
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
