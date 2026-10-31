// 长任务与陪伴层的**契约面**判据（R1g / R1h，2026-10-31）。
//
// 为什么另开一组：UiWorkChecks 验的是旧面板（ControlPanel + UiWorkspaceView）看得见的状态；
// 这里验的是新层唯一会拿到的那份形状 —— IWorkService / ICompanionService 自己的语义。
// 两者落到同一份 RunWorkCoreAsync 实现，但判据各盯一头：那边盯「画出来了没」，这里盯
// 「同一时刻只跑一件」「取消是取消不是失败」「进度真的写成三态」「陪伴行从同一个出口出来」。
//
// 线程口径：这些调用都从线程池发起（Task.Run 包一层再阻塞取结果），因为被测语义就是
// 「body 在后台线程跑」；在 UI 线程上直接 GetResult() 反而会考到同步上下文而不是被测对象。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PetDock.App;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class WorkContractChecks
{
    public static void Run(Report check)
    {
        SingleFlight(check);
        Progress(check);
        Cancel(check);
        Companions(check);
    }

    /// <summary>已有任务在跑时不排队：第二件立刻拿到失败，且第一件的状态没被顶掉。</summary>
    private static void SingleFlight(Report check)
    {
        using var window = new LayeredFrameWindow();
        window.Hide();
        using var host = new UiHost(window, null, null);
        IWorkService work = host;

        using var started = new ManualResetEventSlim(false);
        using var gate = new ManualResetEventSlim(false);
        Task<WorkResult> first = Task.Run(() => work.RunAsync("第一件", (token, progress) =>
        {
            started.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
            return Task.FromResult(WorkResult.Success("done"));
        }));

        bool began = started.Wait(TimeSpan.FromSeconds(10));
        bool busyVisible = began && work.State.Busy;
        WorkResult second = Task.Run(() => work.RunAsync("第二件", (token, progress) =>
            Task.FromResult(WorkResult.Success("should-not-run")))).GetAwaiter().GetResult();

        gate.Set();
        WorkResult firstResult = first.GetAwaiter().GetResult();
        check("ui-work-contract-single-flight",
            began && busyVisible
                && second.Outcome == WorkOutcome.Failed && second.Message.Length > 0
                && firstResult.Ok && !work.State.Busy,
            $"began={began} busy={busyVisible} second={second.Outcome} first={firstResult.Outcome} idle={!work.State.Busy}");
    }

    /// <summary>进度真的写成三态：半程有确定比例，最后一帧沿用上一次文案，结束回到 idle。</summary>
    private static void Progress(Report check)
    {
        using var window = new LayeredFrameWindow();
        window.Hide();
        using var host = new UiHost(window, null, null);
        IWorkService work = host;

        var seen = new List<WorkState>();
        EventHandler onChange = (_, _) =>
        {
            lock (seen)
            {
                seen.Add(work.State);
            }
        };

        work.Changed += onChange;
        WorkResult result;
        try
        {
            result = Task.Run(() => work.RunAsync("正在复制…", (token, progress) =>
            {
                progress.Report(new WorkProgress(0.5, "50%"));
                progress.Report(new WorkProgress(1.0, null));
                return Task.FromResult(WorkResult.Success("done"));
            })).GetAwaiter().GetResult();
        }
        finally
        {
            work.Changed -= onChange;
        }

        bool half = false;
        bool keptStage = false;
        bool idleLast = false;
        lock (seen)
        {
            half = seen.Exists(s => s.Busy && Math.Abs(s.Ratio - 0.5) < 0.001 && s.Stage == "50%");
            keptStage = seen.Exists(s => s.Busy && Math.Abs(s.Ratio - 1.0) < 0.001 && s.Stage == "50%");
            idleLast = seen.Count > 0 && !seen[seen.Count - 1].Busy;
        }

        check("ui-work-contract-progress",
            result.Ok && half && keptStage && idleLast && !work.State.Busy,
            $"ok={result.Ok} half={half} story={keptStage} idle-last={idleLast} events={seen.Count}");
    }

    /// <summary>取消是第三种结局，不是失败：body 抛 OCE 换来 Cancelled，状态照样回 idle。</summary>
    private static void Cancel(Report check)
    {
        using var window = new LayeredFrameWindow();
        window.Hide();
        using var host = new UiHost(window, null, null);
        IWorkService work = host;

        WorkResult result = Task.Run(() => work.RunAsync("正在导入…", (token, progress) =>
        {
            work.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(WorkResult.Success("unreachable"));
        })).GetAwaiter().GetResult();

        check("ui-work-contract-cancel",
            result.Outcome == WorkOutcome.Cancelled && !result.Ok && result.Message.Length > 0
                && !work.State.Busy && !host.IsWorking,
            $"outcome={result.Outcome} message={result.Message} idle={!work.State.Busy} working={host.IsWorking}");
    }

    /// <summary>陪伴层从契约的唯一出口出来：没有宠物就是空表，找不到宠物给失败而不是抛。</summary>
    private static void Companions(Report check)
    {
        using var window = new LayeredFrameWindow();
        window.Hide();
        using var host = new UiHost(window, null, null);
        ICompanionService companions = host;

        IReadOnlyList<CompanionRow> rows = companions.Rows();
        AppResult missing = companions.Invoke("no-such-pet", "no-such-companion", "no-such-command");
        check("ui-companion-contract-surface",
            rows.Count == 0 && !missing.Ok && missing.Message.Length > 0,
            $"rows={rows.Count} invoke-ok={missing.Ok} message={missing.Message}");
    }
}
