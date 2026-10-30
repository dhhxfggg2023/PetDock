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

internal static partial class UiWorkChecks
{
    public static void Run(Report check)
    {
        ProgressBar(check);
        ImportCancel(check);
        InstallCancel(check);
        CopyProgress(check);
        CopyCancel(check);
        InstallProgress(check);
        ConvertProgress(check);
        ConvertCancel(check);
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

        // 确定态：复制阶段有总量，进度条必须从不确定态切到 0..100 的实数进度。
        workspace.BeginWork("正在复制…", () => { });
        workspace.SetWorkProgress(0.42, "42%");
        bool known = workspace.WorkProgressKnown
            && Math.Abs(workspace.WorkPercent - 42.0) < 0.5;
        workspace.SetWorkStage("正在登记…");
        check("ui-work-progress-determinate",
            known && !workspace.WorkProgressKnown,
            $"known={known} percent={workspace.WorkPercent} after-stage={workspace.WorkProgressKnown}");
        workspace.EndWork();
    }

    /// <summary>取消导入：不建素材目录、返回 Cancel（不是 Failure），旧宠物不动。</summary>
    private static void ImportCancel(Report check)
    {
        string root = Path.Combine(SelfTestScratch.Base, "petdock-work-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
        string root = Path.Combine(SelfTestScratch.Base, "petdock-work-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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

    /// <summary>复制进度：回调从 0 开始、以「已复制 == 总量」收尾，落盘结果完整。</summary>
    private static void CopyProgress(Report check)
    {
        string root = Path.Combine(SelfTestScratch.Base, "petdock-work-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            string source = Path.Combine(root, "pack");
            Directory.CreateDirectory(source);
            long expected = 0;
            for (int i = 0; i < 3; i++)
            {
                string file = Path.Combine(source, "frame" + i + ".bin");
                File.WriteAllBytes(file, new byte[300 * 1024]);
                expected += new FileInfo(file).Length;
            }

            var store = new PetDataStore(Path.Combine(root, "data"));
            var calls = new List<(long Copied, long Total)>();
            (string _, string _, string path) = store.StagePack(source, default, (copied, total) => calls.Add((copied, total)));

            bool started = calls.Count > 0 && calls[0].Copied == 0 && calls[0].Total == expected;
            bool finished = calls.Count > 1
                && calls[calls.Count - 1].Copied == expected
                && calls[calls.Count - 1].Total == expected;
            bool copied = Directory.Exists(path)
                && Directory.GetFiles(path).Length == 3;
            check("ui-import-copy-progress", started && finished && copied,
                $"calls={calls.Count} started={started} finished={finished} files={Directory.Exists(path)}");
        }
        catch (Exception error)
        {
            check("ui-import-copy-progress", false, "exception: " + error.GetType().Name);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>文件内部取消：下一次读块就停，素材目录里不留半个包。</summary>
    private static void CopyCancel(Report check)
    {
        string root = Path.Combine(SelfTestScratch.Base, "petdock-work-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            string source = Path.Combine(root, "pack");
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, "big.bin"), new byte[3 * 1024 * 1024]);

            var store = new PetDataStore(Path.Combine(root, "data"));
            using var cts = new CancellationTokenSource();
            bool threw = false;
            try
            {
                store.StagePack(source, cts.Token, (copied, _) =>
                {
                    if (copied > 0)
                    {
                        cts.Cancel();
                    }
                });
            }
            catch (OperationCanceledException)
            {
                threw = true;
            }

            bool cleaned = !Directory.Exists(store.PacksDir)
                || Directory.GetFileSystemEntries(store.PacksDir).Length == 0;
            check("ui-import-copy-cancel-midfile", threw && cleaned,
                $"threw={threw} cleaned={cleaned}");
        }
        catch (Exception error)
        {
            check("ui-import-copy-cancel-midfile", false, "exception: " + error.GetType().Name);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>安装器的复制进度与导入同源：0 起步、总量收尾、落位成功。</summary>
    private static void InstallProgress(Report check)
    {
        string root = Path.Combine(SelfTestScratch.Base, "petdock-work-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            string data = Path.Combine(root, "data");
            string user = Path.Combine(data, "plugins");
            string source = Path.Combine(root, "plugin-src");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "petdock-plugin.json"),
                "{\"id\":\"com.example.progress\",\"name\":\"progress\",\"version\":\"1.0.0\","
                + "\"api\":{\"major\":1,\"minor\":0},\"kind\":\"service\",\"tier\":0,"
                + "\"entry\":\"main.js\",\"capabilities\":[],\"license\":{\"spdx\":\"CC0-1.0\"}}");
            File.WriteAllText(Path.Combine(source, "main.js"), "// progress fixture\n");

            var installer = new PluginInstaller(user, data);
            var calls = new List<(long Copied, long Total)>();
            bool prepared = installer.TryPrepare(source, out InstallPlan? plan, out string? error, default,
                (copied, total) => calls.Add((copied, total)));
            bool started = calls.Count > 0 && calls[0].Copied == 0;
            bool finished = calls.Count > 1
                && calls[calls.Count - 1].Copied == calls[calls.Count - 1].Total;
            if (plan is not null)
            {
                installer.Discard(plan);
            }

            check("ui-install-copy-progress", prepared && started && finished,
                $"prepared={prepared} calls={calls.Count} started={started} finished={finished} error={error}");
        }
        catch (Exception error)
        {
            check("ui-install-copy-progress", false, "exception: " + error.GetType().Name);
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
