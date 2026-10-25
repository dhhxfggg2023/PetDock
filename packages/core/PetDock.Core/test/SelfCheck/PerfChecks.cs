// 性能页的逐宠物判据（RFC-30 §4）。判据口径来自 docs/18 §2.4 与 docs/10 §3，
// 不由实现自证：面板能摆出来的每个数字，都必须能说清它是「谁的」读数。
//   ui-perf-per-pet-rows      每只宠物一行，且进程级读数列写「(进程共享)」而不是摊数字
//   ui-perf-attributed        只有一只宠物时，帧耗时/超限计数归属到它（avg/max/目标齐全）
//   ui-perf-not-shared        多只宠物时不得把一只泵的读数摊给每一只（帧耗时写「—」、超限 0）
//   ui-perf-counters-unwired  降级/隔离区没有接收口时写「未接」，不写 0
//   ui-perf-overbudget-wired  FramePump 的超限计数真的会涨（面板那一列不是常量）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class PerfChecks
{
    /// <summary>判据回调（D-03b：框架自带的同名泛型委托含黑名单词，自声明代替，同 SettingsChecks 的做法）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    /// <summary>采样窗口（判据用短窗：只验归属规则，不验功耗口径）。</summary>
    private const int WindowMs = 120;

    public static void Run(Check check)
    {
        check = check ?? throw new ArgumentNullException(nameof(check));

        PerPetRows(check);
        Attributed(check);
        NotShared(check);
        CountersUnwired(check);
        OverBudgetWired(check);
    }

    private static void PerPetRows(Check check)
    {
        UiSnapshot snapshot = Snapshot("alpha", "beta", "gamma");
        PerfSummary perf = PerfSampler.Measure(WindowMs, snapshot, null);

        bool rowsOk = perf.Rows.Count == snapshot.Pets.Count + 1;
        string shared = perf.Rows[1].Cpu;
        bool sharedOk = shared == PerfPage.ProcessShared && perf.Rows[1].Memory == PerfPage.ProcessShared;
        check("ui-perf-per-pet-rows", rowsOk && sharedOk,
            $"rows={perf.Rows.Count} pets={snapshot.Pets.Count} cpu1={shared} ws1={perf.Rows[1].Memory}");
    }

    private static void Attributed(Check check)
    {
        UiSnapshot snapshot = Snapshot("solo");
        var readings = new PetFrameReadings(4.2, 9.0, 33.3, 7);
        PerfSummary perf = PerfSampler.Measure(WindowMs, snapshot, _ => readings);

        PerfLine pet = perf.Rows[1];
        bool frameOk = pet.FrameMs.Contains("avg 4.2", StringComparison.Ordinal)
            && pet.FrameMs.Contains("max 9.0", StringComparison.Ordinal)
            && pet.FrameMs.Contains("33.3", StringComparison.Ordinal);
        check("ui-perf-attributed", frameOk && pet.Overruns == 7,
            $"frame={pet.FrameMs} overruns={pet.Overruns}");
    }

    private static void NotShared(Check check)
    {
        UiSnapshot snapshot = Snapshot("one", "two");
        var readings = new PetFrameReadings(4.2, 9.0, 33.3, 7);
        PerfSummary perf = PerfSampler.Measure(
            WindowMs,
            snapshot,
            id => id == "one" ? readings : null);

        bool framesPlaceholder = perf.Rows[2].FrameMs == PerfPage.NotAttributed
            && perf.Rows[1].FrameMs != PerfPage.NotAttributed;
        bool overrunsNotSpread = perf.Rows[2].Overruns is null && perf.Rows[1].Overruns == 7;
        check("ui-perf-not-shared", framesPlaceholder && overrunsNotSpread,
            $"f1={perf.Rows[1].FrameMs} o1={perf.Rows[1].Overruns} f2={perf.Rows[2].FrameMs} o2={perf.Rows[2].Overruns}");
    }

    private static void CountersUnwired(Check check)
    {
        UiSnapshot snapshot = Snapshot("solo");
        PerfSummary perf = PerfSampler.Measure(WindowMs, snapshot, null);
        string text = PerfPage.Summary(perf);
        // RFC-42 §3.4：断的是「两格都写未接」这个结构化事实（以及计数器确实没接），不是中文原文；
        // 「不编造 0」由 !CountersWired 保证 —— 只有接上计数器时 Summary 才会写出数字。
        check("ui-perf-counters-unwired",
            !perf.CountersWired && text.Contains(PerfPage.NotWired, StringComparison.Ordinal),
            $"summary={text} counters-wired={perf.CountersWired}");
    }

    /// <summary>
    /// 超限计数真的会涨。超限的口径是**固定预算 8.3ms**（`FramePump.FrameBudgetMs` = docs/14 §5 的单帧预算），
    /// 不是泵的当前目标周期 —— 所以这里让 tick 睡 20ms（远大于 8.3ms），跑 300ms 后计数必须 &gt; 0。
    /// </summary>
    private static void OverBudgetWired(Check check)
    {
        using var pump = new FramePump();
        pump.SetTargetFrameMs(1.0);
        pump.Start(() =>
        {
            Thread.Sleep(20);
            return true;
        });
        Thread.Sleep(300);
        pump.Stop();
        long over = pump.OverBudgetFrames;
        check("ui-perf-overbudget-wired", over > 0,
            $"over={over.ToString(CultureInfo.InvariantCulture)} frames={pump.Frames}");
    }

    private static UiSnapshot Snapshot(params string[] names)
    {
        var pets = new List<PetLine>(names.Length);
        foreach (string name in names)
        {
            pets.Add(new PetLine(name, name, "sample-pet", "running", "(进程共享)"));
        }

        return new UiSnapshot(pets, Array.Empty<PluginLine>(), PerfSummary.Empty);
    }
}
