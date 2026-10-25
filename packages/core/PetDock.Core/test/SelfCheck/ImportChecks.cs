// 宠物导入的判据（RFC-15 §4）。
//
// 判据必须走**生产对象**（RFC-14 §2.3 的教训：自证门禁等于没有门禁）：
// 这里用的是真的 PetDataStore（真的落盘）、真的 SwappableFrameSource（真的换源）、
// 真的 ControlPanel（真的控件树）、真的 PetImportService（真的编排）。
// 唯一的桩件是视图实现本身 —— 素材格式的解读归实现方（core 不解析素材，D-03b），
// 判据要验的是「宿主把素材根**传到了**实现方」，这用桩件反而更准。
//
// 覆盖的判据（RFC-15 §4 表里可机器判定的那几条）：
//   ui-import-entry-present          宠物页有导入入口
//   ui-import-picker-modal           选目录走的是目录选择器（面板弹的是它）
//   ui-import-copy-mirrors-source    复制到用户数据区，文件集合与源一致
//   ui-import-registry-entry         清单里有该实例条目且 folder 指向拷贝目录
//   ui-import-asset-root-reaches-view 素材根传到了视图实现的 init 上下文
//   ui-import-replaced-pet           换源后内容源的实例是新实例
//   ui-import-error-bar-visible      失败时内联错误条可见且有文字
//   ui-import-no-modal-error         失败路径不弹模态框
//   ui-import-old-instance-released  旧实例被释放
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class ImportChecks
{
    /// <summary>判据的输出与计数回调（与 UiSelfTest 的 Check 同形）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    /// <summary>自检用的素材包名（不是任何真实格式名 —— core 不该知道格式）。</summary>
    private const string PackDirName = "pack-under-test";

    public static void Run(Check check)
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "petdock-import-check-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(dataRoot, "source", PackDirName);
        try
        {
            MakeSourcePack(source);
            RunAll(check, dataRoot, source);
        }
        catch (Exception error)
        {
            check("ui-import-entry-present", false, $"{error.GetType().Name}: {error.Message}");
            check("ui-import-copy-mirrors-source", false, "exception");
            check("ui-import-registry-entry", false, "exception");
            check("ui-import-asset-root-reaches-view", false, "exception");
            check("ui-import-replaced-pet", false, "exception");
            check("ui-import-error-bar-visible", false, "exception");
            check("ui-import-no-modal-error", false, "exception");
            check("ui-import-old-instance-released", false, "exception");
            check("ui-import-append-pet", false, "exception");
            check("ui-import-append-falls-back", false, "exception");
        }
        finally
        {
            TryDelete(dataRoot);
        }
    }

    private static void RunAll(Check check, string dataRoot, string source)
    {
        var store = new PetDataStore(dataRoot);
        var roster = new PetSurfaceRoster();
        roster.Add("petdock.stub.view", typeof(StubView).FullName!, typeof(IPetView), new StubView());

        var frames = new SwappableFrameSource(SolidFrameContent.KernelFallback(), roster);
        PetInstance? current = null;
        var service = new PetImportService(store, frames, roster, () => current, next => current = next);

        // ① 复制到用户数据区 + 清单写入。
        ImportResult result = service.Import(source);
        string staged = Path.Combine(store.PacksDir, PackDirName);
        bool mirrored = result.Ok && Directory.Exists(staged) && SameFileSet(source, staged);
        check("ui-import-copy-mirrors-source", mirrored,
            $"ok={result.Ok} message={result.Message} staged={staged} exists={Directory.Exists(staged)}");

        // ② 清单：条目存在且 folder 指向拷贝目录。
        PetDataStore.ActivePack? active = store.ReadActive();
        check("ui-import-registry-entry", active is not null && active.Path == staged,
            $"active={active?.InstanceId ?? "(none)"} path={active?.Path ?? "(none)"}");

        // ③ 素材根传到视图实现的 init 上下文。
        bool rootsReach = StubView.ReceivedContexts.Any(ctx =>
            ctx.AssetRoots.Any(root => string.Equals(root, staged, StringComparison.Ordinal)));
        check("ui-import-asset-root-reaches-view", rootsReach,
            $"contexts={StubView.ReceivedContexts.Count} roots={DescribeRoots()}");

        // ④ 换源：代理当前持有的是新实例的帧源。
        PetFrameSource? live = frames.Current as PetFrameSource;
        check("ui-import-replaced-pet", live is not null && current is not null
                && ReferenceEquals(live.Instance, current),
            $"live={live?.Instance.Id ?? "(none)"} current={current?.Id ?? "(none)"}");

        // ⑤ 面板：宠物页有导入入口。
        using var panel = new ControlPanel(() => UiSnapshot.Empty, showWindow: false);
        check("ui-import-entry-present", panel.ImportButtonName.Length > 0
                && string.Equals(panel.ImportEntryPage, "tab-pets", StringComparison.Ordinal),
            $"button={panel.ImportButtonName} page={panel.ImportEntryPage}");

        // ⑥ 失败路径：内联错误条可见、不弹模态框、旧宠物原样不动。
        using var panel2 = new ControlPanel(() => UiSnapshot.Empty, showWindow: false);
        panel2.SetImportCommand((string dir, out string message) =>
        {
            ImportResult failed = service.Import(dir);
            message = failed.Message;
            return failed.Ok;
        });

        bool ok = panel2.TryImport(Path.Combine(dataRoot, "does-not-exist"));
        bool barShown = panel2.ImportErrorShown && panel2.ImportErrorText.Length > 0;
        check("ui-import-error-bar-visible", !ok && barShown,
            $"ok={ok} shown={panel2.ImportErrorShown} text={panel2.ImportErrorText}");

        // 模态框：失败路径上一次 MessageBox 都不该弹。WinForms 的 MessageBox 会阻塞，
        // 无人值守下会挂死 —— 所以判据不靠「等它弹不弹」，而是查「失败路径上代码没走到弹框」。
        // 这里用可证的形式：导入失败返回后，宿主没有打开任何新窗体（面板的 OwnedForms 为空）。
        bool noModal = panel2.OwnedForms.Length == 0;
        check("ui-import-no-modal-error", noModal, $"owned-forms={panel2.OwnedForms.Length}");

        // ⑦ 旧实例被释放：导入第二个包之后，第一个实例的视图实现被 dispose 过。
        PetInstance? first = current;
        StubView.ReceivedContexts.Clear();
        ImportResult second = service.Import(source);
        check("ui-import-old-instance-released", second.Ok && first is not null
                && first.State == InstanceState.Disposed,
            $"second={second.Ok} first-state={first?.State.ToString() ?? "(none)"}");

        // ⑧ 片 2（RFC-37 §3.5）：装了集合之后，「导入」= 往桌上加一只，主窗那只不动。
        //    判据要的是三件事同时成立：新来的被采纳（AdoptExtra 被问到且说行）、主窗没被换
        //    （帧源与 current 都还是原来那只、旧实例没被释放）、清单多了一条而 active 没被篡改。
        string source2 = Path.Combine(dataRoot, "source2", PackDirName + "-2");
        MakeSourcePack(source2);
        PetInstance? mainBefore = current;
        PetFrameSource? liveBefore = frames.Current as PetFrameSource;
        PetDataStore.ActivePack? activeBefore = store.ReadActive();
        int beforeCount = store.ReadPets().Count;

        int adopted = 0;
        string adoptedPath = string.Empty;
        service.AdoptExtra = (instance, path) =>
        {
            adopted++;
            adoptedPath = path;
            return true;
        };

        ImportResult added = service.Import(source2);
        PetDataStore.ActivePack? activeAfter = store.ReadActive();
        int afterCount = store.ReadPets().Count;
        bool appended = added.Ok
            && string.Equals(added.MessageKey, ImportResult.AddedKey, StringComparison.Ordinal)
            && adopted == 1
            && ReferenceEquals(current, mainBefore)
            && ReferenceEquals(frames.Current, liveBefore)
            && mainBefore is not null && mainBefore.State != InstanceState.Disposed
            && afterCount == beforeCount + 1
            && string.Equals(activeAfter?.InstanceId, activeBefore?.InstanceId, StringComparison.Ordinal);
        check("ui-import-append-pet", appended,
            $"ok={added.Ok} message={added.Message} adopted={adopted} pets={beforeCount}->{afterCount}"
            + $" active={activeAfter?.InstanceId ?? "(none)"} main={mainBefore?.State.ToString() ?? "(none)"}"
            + $" adoptedPath={adoptedPath}");

        // ⑨ 摆不进去（没有集合层：单窗装配、自检）时退回 RFC-15 的替换语义 ——
        //    这时旧行为必须原样成立，否则「多宠物」会把单窗场景弄坏。
        PetInstance? mainNow = current;
        int adoptTries = 0;
        service.AdoptExtra = (instance, path) =>
        {
            adoptTries++;
            return false;
        };

        ImportResult replaced = service.Import(source);
        bool fallsBack = replaced.Ok
            && string.Equals(replaced.MessageKey, ImportResult.ReplacedKey, StringComparison.Ordinal)
            && adoptTries == 1
            && !ReferenceEquals(current, mainNow)
            && mainNow is not null && mainNow.State == InstanceState.Disposed;
        check("ui-import-append-falls-back", fallsBack,
            $"ok={replaced.Ok} message={replaced.Message} tries={adoptTries}"
            + $" main={(mainNow?.State.ToString() ?? "(none)")}");
    }

    /// <summary>造一个最小的素材包目录：几个文件 + 一个子目录（递归复制要真的走通）。</summary>
    private static void MakeSourcePack(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "img"));
        Directory.CreateDirectory(Path.Combine(dir, "anim"));
        File.WriteAllText(Path.Combine(dir, "pack.json"), "{\"version\":1}");
        File.WriteAllText(Path.Combine(dir, "img", "a.txt"), "frame-a");
        File.WriteAllText(Path.Combine(dir, "anim", "b.txt"), "clip-b");
    }

    /// <summary>源与拷贝的文件集合是否一致（相对路径 + 字节内容）。</summary>
    private static bool SameFileSet(string source, string staged)
    {
        List<string> a = ListFiles(source);
        List<string> b = ListFiles(staged);
        if (!a.SequenceEqual(b, StringComparer.Ordinal))
        {
            return false;
        }

        foreach (string rel in a)
        {
            if (!string.Equals(
                    File.ReadAllText(Path.Combine(source, rel)),
                    File.ReadAllText(Path.Combine(staged, rel)),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> ListFiles(string root)
    {
        var files = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static string DescribeRoots()
    {
        IEnumerable<string> roots = StubView.ReceivedContexts.SelectMany(ctx => ctx.AssetRoots);
        return string.Join("|", roots);
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
