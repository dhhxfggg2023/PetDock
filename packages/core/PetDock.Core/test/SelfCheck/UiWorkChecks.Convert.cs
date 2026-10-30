// 第三方转换进度/取消的判据（2026-10-29）：从 UiWorkChecks.cs 拆出（D-03c 单文件 400 行）。
// 这里用假导入器锁住两条路由：实现 IPackImporterProgress 走进度重载；旧导入器仍走原方法。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static partial class UiWorkChecks
{
    /// <summary>
    /// 第三方转换进度：实现 IPackImporterProgress 的导入器按帧报告，宿主原样转发；
    /// 旧导入器不实现它仍走原方法（这里用「旧方法被调用就抛」的假导入器锁住路由）。
    /// </summary>
    private static void ConvertProgress(Report check)
    {
        string root = Path.Combine(SelfTestScratch.Base, "petdock-work-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            var roster = new PetSurfaceRoster();
            var frames = new SwappableFrameSource(SolidFrameContent.KernelFallback(), roster);
            var store = new PetDataStore(Path.Combine(root, "data"));
            var import = new PetImportService(
                store,
                frames,
                roster,
                () => null,
                _ => { },
                new IPackImporter[] { new ProgressImporter() });
            string source = Path.Combine(root, "pack");
            Directory.CreateDirectory(source);

            var calls = new List<(int Done, int Total)>();
            ImportResult result = import.Import(
                source,
                default,
                null,
                null,
                null,
                (done, total) => calls.Add((done, total)));

            bool sequence = calls.Count == 3
                && calls[0] == (0, 2)
                && calls[1] == (1, 2)
                && calls[2] == (2, 2);
            bool cleaned = !Directory.Exists(store.PacksDir)
                || Directory.GetFileSystemEntries(store.PacksDir).Length == 0;
            check("ui-import-convert-progress", sequence && cleaned,
                $"calls={string.Join(",", calls)} ok={result.Ok} cleaned={cleaned}");
        }
        catch (Exception error)
        {
            check("ui-import-convert-progress", false, "exception: " + error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>转换中的取消：token 真的进了第三方导入器，取消后不留下转换区/素材区。</summary>
    private static void ConvertCancel(Report check)
    {
        string root = Path.Combine(SelfTestScratch.Base, "petdock-work-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            var roster = new PetSurfaceRoster();
            var frames = new SwappableFrameSource(SolidFrameContent.KernelFallback(), roster);
            var store = new PetDataStore(Path.Combine(root, "data"));
            var import = new PetImportService(
                store,
                frames,
                roster,
                () => null,
                _ => { },
                new IPackImporter[] { new ProgressImporter() });
            string source = Path.Combine(root, "pack");
            Directory.CreateDirectory(source);

            using var cts = new CancellationTokenSource();
            ImportResult result = import.Import(
                source,
                cts.Token,
                null,
                null,
                null,
                (done, _) =>
                {
                    if (done == 0)
                    {
                        cts.Cancel();
                    }
                });

            bool cleaned = !Directory.Exists(store.PacksDir)
                || Directory.GetFileSystemEntries(store.PacksDir).Length == 0;
            check("ui-import-convert-cancel",
                result.WasCancelled && cleaned,
                $"cancelled={result.WasCancelled} cleaned={cleaned}");
        }
        catch (Exception error)
        {
            check("ui-import-convert-cancel", false, "exception: " + error.GetType().Name);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>只认真实接口路径的假导入器：旧 Import 被调用就说明宿主没走进度分支。</summary>
    private sealed class ProgressImporter : IPackImporter, IPackImporterProgress
    {
        public string Id => "test.progress";

        public bool CanImport(string sourceDir) => true;

        public void Import(string sourceDir, string outDir) =>
            throw new InvalidOperationException("host did not use IPackImporterProgress");

        public void Import(string sourceDir, string outDir, PackImportProgressNotify report, CancellationToken token)
        {
            report(new PackImportProgress(0, 2));
            token.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(outDir, "petdock-sample.json"), "{}");
            report(new PackImportProgress(1, 2));
            token.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(outDir, "frame.bin"), "x");
            report(new PackImportProgress(2, 2));
        }
    }
}
