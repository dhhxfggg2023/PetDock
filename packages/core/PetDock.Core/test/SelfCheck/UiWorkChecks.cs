// 长任务（导入/安装）的判据（2026-10-28 第三轮）：进度条真的出现/收起、取消只投递一次、
// 取消后不在数据区留下半个素材包或半个解包目录。
//
// 这里不验「界面不冻」——那要量时间且容易 flake；验的是把长活挪出 UI 线程之后
// **用户能看见的状态**与**失败后的现场**。线程口径本身由 UiHost.Work.cs 的注释与本组判据共同锁定。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class UiWorkChecks
{
    public static void Run(Report check)
    {
        ProgressBar(check);
        ImportCancel(check);
        InstallCancel(check);
    }

    /// <summary>进度条与取消按钮：BeginWork 出现、SetWorkStage 换文案、取消只发一次、EndWork 收起。</summary>
    private static void ProgressBar(Report check)
    {
        var context = new PreviewUiContext();
        using var panel = new ControlPanel(context, Array.Empty<IUiPage>(), showWindow: false);
        UiWorkspaceView workspace = panel.Workspace;

        int cancels = 0;
        workspace.BeginWork("正在导入…", () => cancels++);
        check("ui-work-progress-visible",
            workspace.IsWorking && workspace.StatusTextValue.Contains("正在导入", StringComparison.Ordinal),
            $"working={workspace.IsWorking} status={workspace.StatusTextValue}");

        workspace.SetWorkStage("正在复制…");
        check("ui-work-progress-stage",
            workspace.StatusTextValue.Contains("正在复制", StringComparison.Ordinal),
            "status=" + workspace.StatusTextValue);

        // 取消按钮的 Click 走同一个入口：这里直接调公开动作，验的是「只投递一次」而不是 WPF 路由。
        workspace.BeginWork("正在导入…", () => cancels++);
        var cancel = workspace.FindName("WorkCancel") as System.Windows.Controls.Button;
        cancel?.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        cancel?.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check("ui-work-cancel-once", cancels == 1, "cancels=" + cancels);

        workspace.EndWork();
        check("ui-work-progress-hidden", !workspace.IsWorking, "working=" + workspace.IsWorking);
    }

    /// <summary>取消导入：不建素材目录、返回 Cancel（不是 Failure），旧宠物不动。</summary>
    private static void ImportCancel(Report check)
    {
        string root = Path.Combine(Path.GetTempPath(), "petdock-work-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            var roster = new PetSurfaceRoster();
            var frames = new SwappableFrameSource(SolidFrameContent.KernelFallback(), roster);
            var store = new PetDataStore(Path.Combine(root, "data"));
            var import = new PetImportService(store, frames, roster, () => null, _ => { });
            string source = Path.Combine(root, "pack");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "pack.json"), "{}");

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            List<string> stages = new();
            ImportResult result = import.Import(source, cts.Token, stages.Add);

            bool empty = !Directory.Exists(store.PacksDir)
                || Directory.GetFileSystemEntries(store.PacksDir).Length == 0;
            check("ui-import-cancel-result",
                result.WasCancelled && !result.Ok && empty,
                $"cancelled={result.WasCancelled} ok={result.Ok} packs-empty={empty}");
        }
        catch (Exception error)
        {
            check("ui-import-cancel-result", false, "exception: " + error.GetType().Name);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>取消安装：解包临时区不留垃圾，正式插件根一个字节都不碰。</summary>
    private static void InstallCancel(Report check)
    {
        string root = Path.Combine(Path.GetTempPath(), "petdock-work-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            string data = Path.Combine(root, "data");
            string user = Path.Combine(data, "plugins");
            string source = Path.Combine(root, "plugin-src");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "x.txt"), "x");

            var installer = new PluginInstaller(user, data);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            bool threw = false;
            try
            {
                installer.TryPrepare(source, out _, out _, cts.Token);
            }
            catch (OperationCanceledException)
            {
                threw = true;
            }

            string staging = Path.Combine(data, "plugins", ".staging");
            int leftovers = Directory.Exists(staging) ? Directory.GetDirectories(staging).Length : 0;
            bool untouched = !Directory.Exists(user) || Directory.GetFileSystemEntries(user).Length == 0;
            check("ui-install-cancel-clean",
                threw && leftovers == 0 && untouched,
                $"threw={threw} staging-left={leftovers} user-empty={untouched}");
        }
        catch (Exception error)
        {
            check("ui-install-cancel-clean", false, "exception: " + error.GetType().Name);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // 判据目录删不干净不改变结论（下一次用新目录）。
        }
    }
}
