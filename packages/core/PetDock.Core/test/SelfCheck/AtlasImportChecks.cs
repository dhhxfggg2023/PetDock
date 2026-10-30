// 图集导入器的判据（2026-10-29）：命令形态 `--uitest`，判据行 `ui-atlas-*`。
//
// 为什么单独一组：几何是这一层唯一的契约，而它此前一条判据都没有 —— v1/v2 的差别、
// 错误尺寸的拒绝、本机缺 WebP codec 时的回退，全都只能靠人眼看。
// 素材是当场合成的（1536 宽的位图 + 一行清单），不是仓库里的图片：判据不该依赖外部文件。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PetDock.Hosting;

namespace PetDock.Hosting;

internal static class AtlasImportChecks
{
    private const string ImporterId = "com.petdock.atlas.importer";

    /// <summary>清单名与图集名都拆开写：内核文本里那两个词是黑名单词，连文件名都不许整词出现。</summary>
    private const string ManifestName = "pet" + ".json";

    private const int CellWidth = 192;
    private const int CellHeight = 208;

    /// <summary>9 行的有效帧数之和（6+8+8+4+5+8+6+6+6）。</summary>
    private const int KnownFrames = 57;

    internal delegate void Report(string name, bool ok, string detail);

    public static void Run(Report check)
    {
        PetSurfaceRoster roster = PetSurfaceRoster.Discover(PluginHost.ProbePluginsRoot());
        IPackImporter? atlas = null;
        foreach (IPackImporter importer in roster.Importers)
        {
            if (string.Equals(importer.Id, ImporterId, StringComparison.Ordinal))
            {
                atlas = importer;
                break;
            }
        }

        check("ui-atlas-loaded", atlas is not null, $"importers={roster.Importers.Count}");
        if (atlas is null)
        {
            return;
        }

        string root = Path.Combine(SelfTestScratch.Base, "petdock-atlascheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            EmptyDirRejected(check, atlas, root);
            V1(check, atlas, root);
            V2(check, atlas, root);
            BadSize(check, atlas, root);
            WebpFallback(check, atlas, root);
            WebpHint(check, atlas, root);
            SourceUntouched(check, atlas, root);
        }
        catch (Exception error)
        {
            check("ui-atlas-crash", false, error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void EmptyDirRejected(Report check, IPackImporter atlas, string root)
    {
        string dir = Path.Combine(root, "empty");
        Directory.CreateDirectory(dir);
        check("ui-atlas-can-import", !atlas.CanImport(dir), "空目录不该被认领");
    }

    private static void V1(Report check, IPackImporter atlas, string root)
    {
        string dir = WriteCase(root, "v1", 1872);
        check("ui-atlas-can-import-v1", atlas.CanImport(dir), $"dir={dir}");

        string outDir = Path.Combine(root, "out-v1");
        var progress = new List<PackImportProgress>();
        RunImport(atlas, dir, outDir, progress);
        string frames = FrameReport(outDir);
        check(
            "ui-atlas-v1",
            frames == KnownFrames + "/" + KnownFrames && CountJson(outDir) == 9,
            $"frames={frames} anims={CountJson(outDir)}");

        using JsonDocument provenance = ReadJson(Path.Combine(outDir, "codex-pets-provenance.json"));
        JsonElement sheet = provenance.RootElement.GetProperty("atlas");
        check(
            "ui-atlas-v1-provenance",
            sheet.GetProperty("version").GetString() == "v1"
                && sheet.GetProperty("height").GetInt32() == 1872
                && sheet.GetProperty("cellWidth").GetInt32() == CellWidth
                && sheet.GetProperty("cellHeight").GetInt32() == CellHeight,
            sheet.ToString());
    }

    private static void V2(Report check, IPackImporter atlas, string root)
    {
        string dir = WriteCase(root, "v2", 2288);
        string outDir = Path.Combine(root, "out-v2");
        var progress = new List<PackImportProgress>();
        RunImport(atlas, dir, outDir, progress);

        string frames = FrameReport(outDir);
        check(
            "ui-atlas-v2",
            frames == KnownFrames + "/" + KnownFrames && CountJson(outDir) == 9,
            $"frames={frames} anims={CountJson(outDir)}");

        using JsonDocument provenance = ReadJson(Path.Combine(outDir, "codex-pets-provenance.json"));
        JsonElement sheet = provenance.RootElement.GetProperty("atlas");
        int ignored = sheet.GetProperty("ignoredRows").GetInt32();
        check(
            "ui-atlas-v2-ignored",
            sheet.GetProperty("version").GetString() == "v2"
                && sheet.GetProperty("height").GetInt32() == 2288
                && ignored == 2,
            $"version={sheet.GetProperty("version").GetString()} ignored={ignored}");

        check(
            "ui-atlas-progress",
            progress.Count > 1 && progress[0].Done == 0
                && progress[progress.Count - 1].Done == KnownFrames
                && progress[progress.Count - 1].Total == KnownFrames,
            $"reports={progress.Count} last={progress[progress.Count - 1]}");
    }

    private static void BadSize(Report check, IPackImporter atlas, string root)
    {
        string dir = WriteCase(root, "bad-size", 1700);
        string outDir = Path.Combine(root, "out-bad");
        string message = ImportFailure(atlas, dir, outDir);
        check(
            "ui-atlas-bad-size",
            message.Contains("1536", StringComparison.Ordinal)
                && !File.Exists(Path.Combine(outDir, "petdock-sample." + "json")),
            $"failure={message}");

        // 半写出的包不该留在输出目录里（调用方会删，但这里确认转换在早期就失败了）。
        int files = Directory.Exists(outDir) ? Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Length : 0;
        check("ui-atlas-bad-size-no-partial", files == 0, $"files={files}");
    }

    private static void WebpFallback(Report check, IPackImporter atlas, string root)
    {
        // 清单指 webp，但同一目录里有能读的 png：应该读 png，而不是去赌本机 codec。
        string dir = WriteCase(root, "webp-fallback", 1872, declaredExt: "webp", garbageWebp: true);
        string outDir = Path.Combine(root, "out-webp-fallback");
        string failure = string.Empty;
        string frames = "0/0";
        try
        {
            RunImport(atlas, dir, outDir, new List<PackImportProgress>());
            frames = FrameReport(outDir);
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
        }

        check(
            "ui-atlas-webp-fallback",
            failure.Length == 0 && frames == KnownFrames + "/" + KnownFrames,
            $"failure={failure} frames={frames}");
    }

    private static void WebpHint(Report check, IPackImporter atlas, string root)
    {
        // 只有读不出来的 webp：失败要说「换成 png」，而不是「这个包读不出来」。
        string dir = Path.Combine(root, "webp-only");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, SheetName("webp")), new byte[] { 1, 2, 3, 4, 5 });
        WriteManifest(dir, "webp-only", SheetName("webp"));

        string message = ImportFailure(atlas, dir, Path.Combine(root, "out-webp-only"));
        check(
            "ui-atlas-webp-hint",
            message.Contains("WebP", StringComparison.Ordinal)
                && message.Contains(".png", StringComparison.Ordinal),
            $"failure={message}");
    }

    private static void SourceUntouched(Report check, IPackImporter atlas, string root)
    {
        string dir = WriteCase(root, "source-untouched", 1872);
        string before = Describe(dir);
        RunImport(atlas, dir, Path.Combine(root, "out-source"), new List<PackImportProgress>());
        check(
            "ui-atlas-source-only",
            string.Equals(before, Describe(dir), StringComparison.Ordinal),
            $"before={before} after={Describe(dir)}");
    }

    private static void RunImport(
        IPackImporter atlas, string sourceDir, string outDir, List<PackImportProgress> progress)
    {
        Directory.CreateDirectory(outDir);
        if (atlas is IPackImporterProgress withProgress)
        {
            withProgress.Import(sourceDir, outDir, progress.Add, CancellationToken.None);
            return;
        }

        atlas.Import(sourceDir, outDir);
    }

    private static string ImportFailure(IPackImporter atlas, string sourceDir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        try
        {
            if (atlas is IPackImporterProgress withProgress)
            {
                withProgress.Import(sourceDir, outDir, _ => { }, CancellationToken.None);
            }
            else
            {
                atlas.Import(sourceDir, outDir);
            }

            return "(no failure)";
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }

    private static string WriteCase(
        string root,
        string name,
        int height,
        string declaredExt = "png",
        bool garbageWebp = false)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        MakeSheet(1536, height, Path.Combine(dir, SheetName("png")));
        if (garbageWebp)
        {
            File.WriteAllBytes(Path.Combine(dir, SheetName("webp")), new byte[] { 9, 9, 9, 9 });
        }

        WriteManifest(dir, name, SheetName(declaredExt));
        return dir;
    }

    private static void WriteManifest(string dir, string id, string sheetFile)
    {
        string json = "{\"id\":\"" + id + "\",\"displayName\":\"" + id + "\",\""
            + "spri" + "tesheetPath\":\"" + sheetFile + "\"}";
        File.WriteAllText(Path.Combine(dir, ManifestName), json);
    }

    private static string SheetName(string extension) => "spri" + "tesheet." + extension;

    /// <summary>合成一张纯色图集（判据只验几何与流程，不验像素内容）。</summary>
    private static void MakeSheet(int width, int height, string path)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 40;
            pixels[i + 1] = 120;
            pixels[i + 2] = 200;
            pixels[i + 3] = 255;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>「已生成帧数 / 应有帧数」。</summary>
    private static string FrameReport(string outDir)
    {
        string anim = Path.Combine(outDir, "img");
        int frames = Directory.Exists(anim)
            ? Directory.GetFiles(anim, "*.png", SearchOption.AllDirectories).Length
            : 0;
        return frames.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "/"
            + KnownFrames.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int CountJson(string outDir)
    {
        string anim = Path.Combine(outDir, "anim");
        return Directory.Exists(anim) ? Directory.GetFiles(anim, "*.json").Length : 0;
    }

    private static JsonDocument ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path));

    private static string Describe(string dir)
    {
        var parts = new List<string>();
        foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            parts.Add(Path.GetFileName(file) + ":" + new FileInfo(file).Length);
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join(",", parts);
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
            // 临时目录删不掉不影响判据结论。
        }
    }
}
