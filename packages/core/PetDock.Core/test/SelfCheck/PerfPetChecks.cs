// RFC-37 片 4：性能页的帧读数按**宠物**归属，而不是「一个泵摊给所有宠物」。
//
// 三条判据各挡一种会把面板变成假数字的写法：
//   1. 两只宠物各有一份读数，行里必须是**各自**的数字（摊同一次采样 = 编造，docs/18 §2.4）；
//   2. 取不到读数的那只写「—」，超限计数也写「—」——0 会被读成「测过是 0」；
//   3. 集合的 Readings() 真的按归因键逐只给（主窗 + 每条槽位），这条挡的是「读数还在但键名撞了」。
using System;
using System.Collections.Generic;
using System.Globalization;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class PerfPetChecks
{
    /// <summary>与其它判据文件一样各自声明一次（这些文件之间不共享类型）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    public static void Run(Check check)
    {
        TwoPetsOwnFrames(check);
        MissingFramesStayDash(check);
        SetReadingsPerPet(check);
    }

    /// <summary>两只宠物 → 两行各自的帧耗时与超限计数，进程合计行不抢其中一只的数字。</summary>
    private static void TwoPetsOwnFrames(Check check)
    {
        UiSnapshot snapshot = Snapshot("pet-a", "pet-b");
        PerfSummary summary = PerfSampler.Measure(0, snapshot, id => id switch
        {
            "pet-a" => new PetFrameReadings(1.0, 2.0, 33.3, 3),
            "pet-b" => new PetFrameReadings(4.0, 5.0, 33.3, 7),
            _ => null,
        });

        bool ok = summary.Rows.Count == 3
            && summary.Rows[1].FrameMs.Contains("avg 1.0", StringComparison.Ordinal)
            && summary.Rows[1].Overruns == 3
            && summary.Rows[2].FrameMs.Contains("avg 4.0", StringComparison.Ordinal)
            && summary.Rows[2].Overruns == 7
            && summary.Rows[0].FrameMs == PerfPage.NotAttributed
            && summary.Rows[1].Cpu == I18n.Get(PerfPage.ProcessShared);

        check("ui-perf-per-pet-frames", ok,
            $"rows={summary.Rows.Count} a=\"{summary.Rows[1].FrameMs}\"/{summary.Rows[1].Overruns}"
            + $" b=\"{summary.Rows[2].FrameMs}\"/{summary.Rows[2].Overruns}"
            + $" sum=\"{summary.Rows[0].FrameMs}\" cpu=\"{summary.Rows[1].Cpu}\"");
    }

    /// <summary>取不到读数的那只写「—」；超限计数是 null 而不是 0（页面把它渲染成「—」）。</summary>
    private static void MissingFramesStayDash(Check check)
    {
        UiSnapshot snapshot = Snapshot("pet-a", "pet-missing");
        PerfSummary summary = PerfSampler.Measure(0, snapshot, id =>
            id == "pet-a" ? new PetFrameReadings(1.0, 2.0, 33.3, 1) : null);

        PerfLine missing = summary.Rows[2];
        bool ok = missing.FrameMs == PerfPage.NotAttributed
            && missing.Overruns is null
            && summary.Rows[1].FrameMs != PerfPage.NotAttributed
            && summary.Rows[0].FrameMs == summary.Rows[1].FrameMs;

        check("ui-perf-unattributed-pet-dash", ok,
            $"missing=\"{missing.FrameMs}\" over={missing.Overruns?.ToString(CultureInfo.InvariantCulture) ?? "null"}"
            + $" a=\"{summary.Rows[1].FrameMs}\" sum=\"{summary.Rows[0].FrameMs}\"");
    }

    /// <summary>集合的读数按归因键逐只给：主窗一行（没有循环 → Frames 为 null）+ 每条槽位一行。</summary>
    private static void SetReadingsPerPet(Check check)
    {
        LayeredFrameWindow primary = null!;
        LayeredFrameWindow extra = null!;
        var set = (PetWindowSet?)null;
        try
        {
            primary = new LayeredFrameWindow();
            extra = new LayeredFrameWindow();
            set = new PetWindowSet(primary);
            set.Add(new PetWindowSet.Slot(null, "pack-x", extra, null, new ResidentFrameLoop(extra)));

            IReadOnlyList<PetReading> rows = set.Readings();
            bool ok = rows.Count == 2
                && rows[0].Id == "(primary)"
                && rows[0].Frames is null
                && rows[1].Id == "(slot-1)"
                && rows[1].Frames is not null
                && !rows[0].Id.Equals(rows[1].Id, StringComparison.Ordinal);

            check("ui-perf-set-readings-per-pet", ok,
                $"count={rows.Count} ids={rows[0].Id},{rows[1].Id}"
                + $" frames={rows[0].Frames is not null},{rows[1].Frames is not null}");
        }
        catch (Exception error)
        {
            check("ui-perf-set-readings-per-pet", false, $"threw={error.GetType().Name}: {error.Message}");
        }
        finally
        {
            set?.Dispose();
            primary.Dispose();
            extra.Dispose();
        }
    }

    private static UiSnapshot Snapshot(params string[] ids)
    {
        var pets = new List<PetLine>(ids.Length);
        foreach (string id in ids)
        {
            pets.Add(new PetLine(id, id, "sample-pet", "running", "(进程共享)"));
        }

        return new UiSnapshot(pets, Array.Empty<PluginLine>(), PerfSummary.Empty);
    }
}
