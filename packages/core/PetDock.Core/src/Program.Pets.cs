// 首次运行时把自带的示例宠物装进清单（2026-10-28 第七轮）。
//
// 为什么不是「每次启动都从 pets/sample-pet 直接装配」：那样默认宠物没有清单 id，
// 既不能多开也不能真正删除——托盘「移除」只是把窗口藏起来，重启它又回来了。
// 装进数据区之后，它和用户导入的宠物走同一条清单/恢复/删除路径，动画素材仍是原来那份。
//
// 只做一次：settings.json 的 petCatalogInitialized=true 之后，即使用户把宠物全删了，
// 也不再自动装回来（删除必须真的删得掉）。
using System;
using System.IO;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;
using PetDock.Hosting;

namespace PetDock.Core;

internal static partial class Program
{
    /// <summary>自带示例包相对仓库/发行根的路径（原规格 31 的 Sample Pack）。</summary>
    private const string SamplePackRel = "pets/sample-pet/petdock-sample.json";

    /// <summary>
    /// 确保宠物清单已经初始化过：空清单 + 第一次运行 → 把示例宠物复制进数据区并选为当前。
    /// 返回 true = 这个进程之后可以把「清单空」当成用户真的删光了；false = 初始化没完成，
    /// 调用方仍应装配内置默认宠物（下次启动再试）。
    /// </summary>
    private static bool EnsurePetCatalog(PetDataStore store, UserSettings settings)
    {
        if (settings.PetCatalogInitialized == true)
        {
            return true;
        }

        if (store.ReadPets().Count > 0)
        {
            // 老用户已经有自己的清单：不塞示例宠物，只把初始化标记补上。
            MarkCatalogInitialized(store);
            return true;
        }

        string? sample = FindBundledSamplePack();
        if (sample is null)
        {
            KernelLog.Write("pet-catalog-seed-skip reason=sample-pack-missing");
            return false;
        }

        try
        {
            (string id, string folder, string path) = store.StagePack(sample);
            store.RegisterActive(id, folder, I18n.Get("cp.pets.defaultName"));
            MarkCatalogInitialized(store);
            KernelLog.Write($"pet-catalog-seeded id={id} folder={folder} path={path}");
            return true;
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-catalog-seed-failed {error.GetType().Name}: {error.Message}");
            return false;
        }
    }

    private static void MarkCatalogInitialized(PetDataStore store)
    {
        try
        {
            new UserSettingsStore(store.Root).Update(current => current with { PetCatalogInitialized = true });
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-catalog-marker-failed {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>从 exe 向上找发行/仓库里的示例包；找不到返回 null（由调用方保留内置默认）。</summary>
    private static string? FindBundledSamplePack()
    {
        string? manifest = TrayHost.FindRepoFile(SamplePackRel);
        return manifest is null ? null : Path.GetDirectoryName(manifest);
    }
}
