// RFC-37 §4：多宠物的**集合层**判据（ui-catalog-*）。
//
// 这一层回答的问题是「清单里登记了几只」，不是「屏幕上有几只」——后者要窗口层，见 RFC-37 §7。
// 判据全部走生产写入路径（PetDataStore.StagePack + RegisterActive），不手写 pets.json：
// 手写的话，判据证明的是「我会写 JSON」，而不是「导入真的会把它记成第二只」。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PetDock.Hosting;

internal static class PetSetChecks
{
    /// <summary>判据行的写出口（与其它判据文件同一形态：每个文件自带一份，不共用一个静态状态）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    public static void Run(Check check)
    {
        string root = Path.Combine(SelfTestScratch.Base, "petdock-set-check-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            var store = new PetDataStore(root);

            // 两次导入 = 清单里两条（RFC-15 的替换语义只换「当前选中」，不删历史）。
            (string firstName, string firstFolder, string firstPath) = Stage(store, root, "alpha-pack");
            store.RegisterActive(firstName, firstFolder, "守岸人");
            (string secondName, string secondFolder, string secondPath) = Stage(store, root, "beta-pack");
            store.RegisterActive(secondName, secondFolder);

            IReadOnlyList<PetDataStore.ActivePack> all = store.ReadPets();
            bool both = all.Count == 2
                && string.Equals(all[0].InstanceId, firstName, StringComparison.Ordinal)
                && string.Equals(all[1].InstanceId, secondName, StringComparison.Ordinal);
            check("ui-catalog-all-packs", both,
                $"count={all.Count} ids={Describe(all)}");

            // 显示名不是内部 id：新清单写原目录名（中文保留），旧清单缺 name 时回落目录名。
            bool named = all.Count == 2
                && string.Equals(all[0].Name, "守岸人", StringComparison.Ordinal)
                && string.Equals(all[1].Name, secondFolder, StringComparison.Ordinal);
            check("ui-catalog-display-name", named,
                $"first={all.FirstOrDefault()?.Name ?? "(none)"} second={all.Skip(1).FirstOrDefault()?.Name ?? "(none)"}");

            // 活跃项从同一份集合里选出来，不另解析一遍清单（一个答案一个主人）。
            PetDataStore.ActivePack? active = store.ReadActive();
            bool activeFromSet = active is not null
                && string.Equals(active.InstanceId, secondName, StringComparison.Ordinal)
                && string.Equals(active.Path, secondPath, StringComparison.Ordinal);
            check("ui-catalog-active-from-set", activeFromSet,
                $"active={active?.InstanceId ?? "(none)"} path={active?.Path ?? "(none)"}");

            // 目录没了的那一条不算「登记过」：清单是记录，磁盘是事实，事实优先（RFC-15 §3.4 的同一口径）。
            store.Discard(firstPath);
            IReadOnlyList<PetDataStore.ActivePack> afterDrop = store.ReadPets();
            bool dropped = afterDrop.Count == 1
                && string.Equals(afterDrop[0].InstanceId, secondName, StringComparison.Ordinal);
            check("ui-catalog-missing-pack", dropped,
                $"count={afterDrop.Count} ids={Describe(afterDrop)}");

            // 当前选中的那只被删掉时，活跃项必须变成空 —— 不许悄悄指向一只已经不存在的宠物。
            store.Discard(secondPath);
            PetDataStore.ActivePack? gone = store.ReadActive();
            bool activeGone = gone is null && store.ReadPets().Count == 0;
            check("ui-catalog-active-gone", activeGone,
                $"active={gone?.InstanceId ?? "(none)"} count={store.ReadPets().Count}");
        }
        catch (Exception error)
        {
            check("ui-catalog-all-packs", false, "exception: " + error.GetType().Name);
            check("ui-catalog-display-name", false, "exception: " + error.GetType().Name);
            check("ui-catalog-active-from-set", false, "exception: " + error.GetType().Name);
            check("ui-catalog-missing-pack", false, "exception: " + error.GetType().Name);
            check("ui-catalog-active-gone", false, "exception: " + error.GetType().Name);
        }
        finally
        {
            TryDelete(root);
        }

        MultiOpen(check);
        MigrateNames(check);
        PackMetadata(check);
    }

    /// <summary>多开 = 清单里两条指向同一目录；删一条不删素材，删最后一条才删目录。</summary>
    private static void MultiOpen(Check check)
    {
        string root = Path.Combine(SelfTestScratch.Base, "petdock-multi-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            var store = new PetDataStore(root);
            (string id, string folder, string path) = Stage(store, root, "alpha-pack");
            store.RegisterActive(id, folder, "守岸人");

            PetDataStore.ActivePack added = store.AddInstance(id, "守岸人（2）");
            IReadOnlyList<PetDataStore.ActivePack> packs = store.ReadPets();
            bool two = packs.Count == 2
                && !string.Equals(packs[0].InstanceId, packs[1].InstanceId, StringComparison.Ordinal)
                && string.Equals(packs[0].Path, packs[1].Path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(added.Name, "守岸人（2）", StringComparison.Ordinal);

            store.RemoveFromCatalog(id);
            bool kept = !store.DeletePackIfUnreferenced(path) && Directory.Exists(path);

            store.RemoveFromCatalog(added.InstanceId);
            bool gone = store.DeletePackIfUnreferenced(path) && !Directory.Exists(path);

            check("ui-catalog-multi-open", two && kept && gone,
                $"two={two} kept={kept} gone={gone} ids={Describe(packs)}");
        }
        catch (Exception error)
        {
            check("ui-catalog-multi-open", false, "exception: " + error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>旧清单没有 name 时从素材包清单补；只补空值，读不到就保持回落到目录名。</summary>
    private static void MigrateNames(Check check)
    {
        string root = Path.Combine(SelfTestScratch.Base, "petdock-names-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            string named = Path.Combine(root, "packs", "old-pack");
            string unnamed = Path.Combine(root, "packs", "no-manifest");
            Directory.CreateDirectory(named);
            Directory.CreateDirectory(unnamed);
            File.WriteAllText(Path.Combine(named, "petdock-sample.json"), """{"name":"守岸人"}""");
            File.WriteAllText(
                Path.Combine(root, "pets.json"),
                """{"schemaVersion":1,"active":"user.old-pack","pets":[{"id":"user.old-pack","folder":"old-pack"},{"id":"user.no-manifest","folder":"no-manifest"}]}""");

            var store = new PetDataStore(root);
            int filled = store.MigrateMissingNames();
            IReadOnlyList<PetDataStore.ActivePack> packs = new PetDataStore(root).ReadPets();
            int again = new PetDataStore(root).MigrateMissingNames();
            bool ok = filled == 1
                && packs.Count == 2
                && string.Equals(packs[0].Name, "守岸人", StringComparison.Ordinal)
                && string.Equals(packs[1].Name, "no-manifest", StringComparison.Ordinal)
                && again == 0;
            check("ui-catalog-name-migration", ok,
                $"filled={filled} again={again} names={string.Join(",", packs.Select(pack => pack.Name))}");
        }
        catch (Exception error)
        {
            check("ui-catalog-name-migration", false, "exception: " + error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>标准包元数据：作者、对象形式的许可、画布尺寸、包内图标。</summary>
    private static void PackMetadata(Check check)
    {
        string root = Path.Combine(SelfTestScratch.Base, "petdock-meta-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            string pack = Path.Combine(root, "pack");
            Directory.CreateDirectory(pack);
            File.WriteAllText(
                Path.Combine(pack, "petdock-sample.json"),
                """{"author":"Alice","license":{"spdx":"CC0-1.0"},"naturalSize":{"w":64,"h":80}}""");
            File.WriteAllBytes(Path.Combine(pack, "icon.png"), new byte[] { 1, 2, 3 });
            PetPackMetadata? meta = PetPackMetadataReader.Read(pack);
            bool ok = meta is not null
                && string.Equals(meta.Author, "Alice", StringComparison.Ordinal)
                && string.Equals(meta.License, "CC0-1.0", StringComparison.Ordinal)
                && meta.NaturalWidth == 64
                && meta.NaturalHeight == 80
                && meta.IconPath is not null;
            check("ui-catalog-pack-metadata", ok,
                $"author={meta?.Author} license={meta?.License} size={meta?.NaturalWidth}x{meta?.NaturalHeight} icon={meta?.IconPath is not null}");
        }
        catch (Exception error)
        {
            check("ui-catalog-pack-metadata", false, "exception: " + error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static (string Id, string Folder, string Path) Stage(PetDataStore store, string root, string name)
    {
        string source = Path.Combine(root, "src-" + name);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "pack.json"), "{\"name\":\"" + name + "\"}");
        return store.StagePack(source);
    }

    private static string Describe(IReadOnlyList<PetDataStore.ActivePack> packs)
    {
        if (packs.Count == 0)
        {
            return "(none)";
        }

        var ids = new List<string>(packs.Count);
        foreach (PetDataStore.ActivePack pack in packs)
        {
            ids.Add(pack.InstanceId);
        }

        return string.Join(",", ids);
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
            // 清理失败不影响判据结论：临时目录下次自己会被系统收走。
        }
    }
}
