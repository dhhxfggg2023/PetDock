// 长任务与陪伴层的契约半边（R1g / R1h，2026-10-31）。
//
// 这两个服务太薄，不值得各开一个类：它们只是把 UiHost 里已经存在的机制
// （RunWorkAsync 的单飞/进度/取消、BuildCompanionLines 的转述）翻译成 PetDock.App 的形状。
// R8 删旧层时实现搬去 AppHost，这里只剩这段翻译。
//
// 一份实现、两个调用面：旧面板仍按 UiCommandResult 取结果，新层按 WorkResult 取三态 ——
// 两者都落到 RunWorkCoreAsync，所以「同一时刻只跑一件」「取消不是失败」不会出现两套口径。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PetDock.App;
using PetDock.Core.Compositor;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>契约侧读到的状态。旧面板不读它（自己画进度条），但两边同源。</summary>
    private WorkState _workState = WorkState.Idle;

    private event EventHandler? WorkChanged;

    WorkState IWorkService.State => _workState;

    event EventHandler? IWorkService.Changed
    {
        add => WorkChanged += value;
        remove => WorkChanged -= value;
    }

    Task<WorkResult> IWorkService.RunAsync(
        string stage,
        Func<CancellationToken, IProgress<WorkProgress>, Task<WorkResult>> body) =>
        RunWorkCoreAsync(stage, "ui.work.cancelled", body);

    void IWorkService.Cancel() => CancelWork();

    // ── ICompanionService（R1h）────────────────────────────────────────────

    IReadOnlyList<CompanionRow> ICompanionService.Rows() => BuildCompanionRows();

    AppResult ICompanionService.Invoke(string petId, string companionId, string commandId) =>
        Translate(InvokeCompanionFromPanel(petId, companionId, commandId));

    /// <summary>旧行 → 契约行。只换字段名（CommandLine → Button），不解释内容。</summary>
    private IReadOnlyList<CompanionRow> BuildCompanionRows()
    {
        IReadOnlyList<CompanionLine> lines = BuildCompanionLines();
        var rows = new List<CompanionRow>(lines.Count);
        foreach (CompanionLine line in lines)
        {
            var buttons = new List<CompanionButton>(line.Commands.Count);
            foreach (CompanionCommandLine command in line.Commands)
            {
                buttons.Add(new CompanionButton(command.CommandId, command.Label));
            }

            rows.Add(new CompanionRow(
                line.PetId,
                line.PetName,
                line.CompanionId,
                line.Title,
                line.Status,
                line.Detail,
                buttons));
        }

        return rows;
    }

    // ── 长任务的单一实现 ───────────────────────────────────────────────────

    /// <summary>
    /// 后台跑一件长活，同时维持「单飞 + 进度 + 取消 + 三态结局」这一套。
    /// body 抛 OperationCanceledException = 用户取消；这里把它换成 cancelKey 的文案。
    /// 终点一定落回 UI 线程可读的状态（State）并把 Changed 发出去。
    /// </summary>
    private async Task<WorkResult> RunWorkCoreAsync(
        string stage,
        string cancelKey,
        Func<CancellationToken, IProgress<WorkProgress>, Task<WorkResult>> body)
    {
        if (_workCancel is not null)
        {
            // 已经在跑另一件长活：不排队、不并发写同一份数据区。
            return WorkResult.Failure(I18n.Get("ui.work.busy"));
        }

        // 这里一定在 UI 线程上（页面点击 / 托盘命令）：补抓构造期还没装好的同步上下文。
        _uiContext ??= SynchronizationContext.Current;

        using var cts = new CancellationTokenSource();
        _workCancel = cts;
        // 首帧必报：负的时间基让「距上一帧」从一开始就超过节流窗口。
        _lastProgressMs = -ProgressThrottleMs;
        SetWorkState(new WorkState(true, stage, 0.0));
        ShowWork(stage);
        try
        {
            return await Task.Run(() => body(cts.Token, new WorkSink(this)), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return WorkResult.Cancel(I18n.Get(cancelKey));
        }
        catch (Exception error)
        {
            // body 自己应该已经把可预期的失败翻译成文案；走到这里的是没预料的异常。
            KernelLog.Write($"ui-work-failed {error.GetType().Name}: {error.Message}");
            return WorkResult.Failure(I18n.Get("ui.work.failed"));
        }
        finally
        {
            _workCancel = null;
            SetWorkState(WorkState.Idle);
            HideWork();
        }
    }

    /// <summary>进度汇点：节流后切回 UI 线程写 State（插件拿到的 IProgress 不碰界面）。</summary>
    private sealed class WorkSink : IProgress<WorkProgress>
    {
        private readonly UiHost _owner;

        public WorkSink(UiHost owner) => _owner = owner;

        public void Report(WorkProgress value) => _owner.ReportTypedProgress(value);
    }

    /// <summary>
    /// 进度真的写成 State。这里**故意不**往 UI 线程投递：State 是给界面读的不可变值，
    /// Changed 就在报进度的那个线程上发（契约里写明了订阅方自己切线程）——
    /// 若在这里 Post，靠消息泵才能看到进度，而等待中的调用方恰好把泵堵住。
    /// 旧面板那边另有 ReportWorkProgress（它必须按控件线程投递），两条路互不干扰。
    /// </summary>
    private void ReportTypedProgress(WorkProgress value)
    {
        if (!ShouldReportProgress(value.Ratio >= 1.0))
        {
            return;
        }

        string stage = value.Stage ?? _workState.Stage;
        ReportWorkProgress(value.Ratio, stage);
        SetWorkState(new WorkState(true, stage, value.Ratio));
    }

    private void SetWorkState(WorkState state)
    {
        _workState = state;
        WorkChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── 旧命令结果 ⇄ 契约三态 ──────────────────────────────────────────────

    /// <summary>旧的两态（Ok/Cancelled 分开）+ 文案 → 契约三态。</summary>
    private static WorkResult FoldingWork(UiCommandResult result) =>
        result.Ok ? WorkResult.Success(result.Message)
        : result.Cancelled ? WorkResult.Cancel(result.Message)
        : WorkResult.Failure(result.Message);

    /// <summary>契约三态 → 旧面板要的形状。</summary>
    private static UiCommandResult UnfoldingWork(WorkResult result) => result.Outcome switch
    {
        WorkOutcome.Done => UiCommandResult.Success(result.Message),
        WorkOutcome.Cancelled => UiCommandResult.Cancel(result.Message),
        _ => UiCommandResult.Failure(result.Message),
    };
}
