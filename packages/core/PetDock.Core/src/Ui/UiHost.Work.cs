// 面板上的长任务（2026-10-28 第三轮）：导入素材、安装插件。
//
// 为什么单开一个文件：UiHost.Panel.cs 已贴着 400 行上限；而且「把长活挪出 UI 线程」
// 本来就是一整套取舍（后台跑、进度、取消、失败文案），值得单独一处说清楚。
//
// 线程口径：
//   · 复制/解压/校验/登记在后台线程（Task.Run）—— 大素材包不再把界面冻住；
//   · 建实例、换源、挂窗口这些会碰帧循环的步骤经 RunOnUi 回到 UI 线程；
//   · UI 线程只等 await，不等后台线程，所以 Send/Invoke 不会自锁。
using System;
using System.Threading;
using System.Threading.Tasks;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>
    /// UI 线程的同步上下文。构造 UiHost 时消息泵通常还没跑起来（Application.Run 在构造之后），
    /// 那时 Current 是 null；所以第一次跑长任务（必在 UI 线程）时再补抓一次。
    /// </summary>
    private SynchronizationContext? _uiContext = SynchronizationContext.Current;

    /// <summary>当前后台任务的取消开关；null = 没有任务在跑。</summary>
    private CancellationTokenSource? _workCancel;

    /// <summary>是否有长任务在跑（自检与进度条都看它）。</summary>
    public bool IsWorking => _workCancel is not null;

    /// <summary>用户点了进度条旁边的「取消」：只是请求，任务在下一个安全点停下。</summary>
    public void CancelWork()
    {
        try
        {
            _workCancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 任务刚好结束：取消请求落空不是错误。
        }
    }

    /// <summary>从面板导入素材包（后台执行，完成时回到 UI 线程）。</summary>
    private async Task<UiCommandResult> ImportPetFromPanelAsync(string sourceDir)
    {
        PetImportService? import = _import;
        if (import is null)
        {
            return UiCommandResult.Failure(I18n.Get("ui.panel.importUnavailable"));
        }

        UiCommandResult result = await RunWorkAsync(
            I18n.Get("cp.pets.importing"),
            "cp.pets.import.cancelled",
            token =>
            {
                ImportResult imported = import.Import(sourceDir, token, ReportWorkStage, RunOnUi);
                if (imported.WasCancelled)
                {
                    return UiCommandResult.Cancel(imported.Message);
                }

                return imported.Ok
                    ? UiCommandResult.Success(imported.Message)
                    : UiCommandResult.Failure(imported.Message);
            });

        if (result.Ok)
        {
            RefreshTrayPets();
            RefreshUi();
        }

        return result;
    }

    /// <summary>从面板安装插件（解压/校验/落盘在后台，完成时回到 UI 线程）。</summary>
    private async Task<UiCommandResult> InstallPluginFromPanelAsync(string source, bool enable)
    {
        PluginInstaller? installer = _installer;
        PluginInventory? inventory = _inventory;
        if (installer is null || inventory is null)
        {
            return UiCommandResult.Failure(I18n.Get("cp.plugins.install.unavailable"));
        }

        bool installed = false;
        UiCommandResult result = await RunWorkAsync(
            I18n.Get("cp.plugins.installing"),
            "cp.plugins.install.cancelled",
            token =>
            {
                if (!installer.TryPrepare(source, out InstallPlan? plan, out string? error, token) || plan is null)
                {
                    return UiCommandResult.Failure(error ?? I18n.Get("cp.plugins.install.failed"));
                }

                KernelLog.Write(
                    $"plugin-install-prepared id={plan.Id} version={plan.Version}"
                    + $" replaces={(plan.Replaces ? 1 : 0)} files={plan.Files} bytes={plan.Bytes}");

                if (!installer.TryCommit(plan, out error, token))
                {
                    return UiCommandResult.Failure(error ?? I18n.Get("cp.plugins.install.failed"));
                }

                if (enable && !inventory.SetEnabled(plan.Id, true, out string? enableError))
                {
                    // 装上了但没打开：这仍然算安装成功，把原因留在日志里（状态栏已经有成功文案）。
                    KernelLog.Write($"plugin-install-enable-failed id={plan.Id} {enableError}");
                }

                installed = true;
                return UiCommandResult.Success(I18n.Get("cp.plugins.install.done"));
            });

        if (installed)
        {
            RefreshUi();
        }

        return result;
    }

    /// <summary>
    /// 把一个可能很慢的宿主动作放到后台跑，并在面板底部显示进度与取消按钮。
    /// body 抛 OperationCanceledException = 用户取消；返回值原样交给页面。
    /// </summary>
    private async Task<UiCommandResult> RunWorkAsync(
        string startText,
        string cancelKey,
        Func<CancellationToken, UiCommandResult> body)
    {
        if (_workCancel is not null)
        {
            // 面板已经在跑另一件长活：不排队、不并发写同一份数据区。
            return UiCommandResult.Failure(I18n.Get("ui.work.busy"));
        }

        // 这里一定在 UI 线程上（页面点击 / 托盘命令）：补抓构造期还没装好的同步上下文。
        _uiContext ??= SynchronizationContext.Current;

        using var cts = new CancellationTokenSource();
        _workCancel = cts;
        ShowWork(startText);
        try
        {
            return await Task.Run(() => body(cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return UiCommandResult.Cancel(I18n.Get(cancelKey));
        }
        catch (Exception error)
        {
            // body 自己应该已经把可预期的失败翻译成文案；走到这里的是没预料的异常。
            KernelLog.Write($"ui-work-failed {error.GetType().Name}: {error.Message}");
            return UiCommandResult.Failure(I18n.Get("ui.work.failed"));
        }
        finally
        {
            _workCancel = null;
            HideWork();
        }
    }

    /// <summary>进度阶段（后台线程调用）：把一句「正在做什么」贴到底部状态条。</summary>
    private void ReportWorkStage(string key)
    {
        ControlPanel? panel = _panel;
        if (panel is null || panel.IsDisposed)
        {
            return;
        }

        string text = I18n.Get(key);
        if (panel.InvokeRequired)
        {
            panel.BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
            {
                if (!panel.IsDisposed)
                {
                    panel.Workspace.SetWorkStage(text);
                }
            }));
            return;
        }

        panel.Workspace.SetWorkStage(text);
    }

    private void ShowWork(string text)
    {
        ControlPanel? panel = _panel;
        if (panel is null || panel.IsDisposed)
        {
            return;
        }

        panel.Workspace.BeginWork(text, CancelWork);
    }

    private void HideWork()
    {
        ControlPanel? panel = _panel;
        if (panel is null || panel.IsDisposed)
        {
            return;
        }

        // finally 可能落在非 UI 线程（没有同步上下文时）：这里必须按控件线程投递。
        if (panel.InvokeRequired)
        {
            panel.BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
            {
                if (!panel.IsDisposed)
                {
                    panel.Workspace.EndWork();
                }
            }));
            return;
        }

        panel.Workspace.EndWork();
    }

    /// <summary>后台线程执行一段必须落在 UI 线程上的动作（阻塞直到做完）。</summary>
    private void RunOnUi(UiCommand step)
    {
        SynchronizationContext? context = _uiContext;
        if (context is not null && !ReferenceEquals(SynchronizationContext.Current, context))
        {
            context.Send(_ => step(), null);
            return;
        }

        // 没有上下文可投递时，至少用面板自己的消息队列；面板也没了才原地执行。
        ControlPanel? panel = _panel;
        if (panel is not null && !panel.IsDisposed && panel.InvokeRequired)
        {
            panel.Invoke((System.Windows.Forms.MethodInvoker)(() => step()));
            return;
        }

        step();
    }
}
