// RFC-37 §4（片 3）：托盘的**逐只**判据（ui-tray-pet-*）。
//
// 要证的只有两件事：① 原规格 18 §1 的「每宠物一条子菜单」真的按桌上有几只长出来；
// ② 点第一条只动第一只 —— 假命令（每条都路由到主窗）在没有判据之前和真命令长得一模一样。
using System;
using System.Collections.Generic;
using System.IO;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal static class TrayPetChecks
{
    /// <summary>判据行的写出口（每个判据文件自带一份，不共用一个静态状态）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    public static void Run(Check check)
    {
        try
        {
            TrayPerItem(check);
            TrayCommandTargetsOwn(check);
            TrayLanguageReload(check);
        }
        catch (Exception error)
        {
            check("ui-tray-pet-per-item", false, "exception: " + error.GetType().Name);
            check("ui-tray-pet-command-targets-own", false, "exception: " + error.GetType().Name);
            check("ui-tray-language-reload", false, "exception: " + error.GetType().Name);
        }

        CatalogRemoval(check);
    }

    /// <summary>三只宠物 ⇒ 三条子菜单（pet-1..pet-3），每条三个子项（移动/暂停/移除）。</summary>
    private static void TrayPerItem(Check check)
    {
        UiCommand noop = () => { };
        var pets = new List<PetMenuItem>
        {
            new("alpha", noop, noop, noop),
            new("beta", noop, noop, noop),
            new("gamma", noop, noop, noop),
        };

        using var tray = new TrayHost(pets, noop, noop);
        IReadOnlyList<string> order = tray.ItemOrder;
        bool orderOk = order.Count == 5
            && order[0] == "panel"
            && order[1] == "pet-1"
            && order[2] == "pet-2"
            && order[3] == "pet-3"
            && order[4] == "exit";

        IReadOnlyList<string> children = tray.PetChildTitles(1);
        // 断言**键**而不是文案：文案搬进 assets/i18n 之后，断言中文的判据红的是判据（RFC-42 §3.4）。
        IReadOnlyList<string> keys = tray.PetChildKeyList(1);
        bool childrenOk = children.Count == 3
            && keys.Count == 3
            && keys[0] == "ui.tray.pet.move"
            && keys[1] == "ui.tray.pet.pause"
            && keys[2] == "ui.tray.pet.remove";

        check("ui-tray-pet-per-item", orderOk && childrenOk && tray.PetItemCount == 3,
            $"items={string.Join(",", order)} keys={string.Join("/", keys)} children={string.Join("/", children)} count={tray.PetItemCount}");
    }

    /// <summary>点第二条的「隐藏」，只有第二只的命令被调用。</summary>
    private static void TrayCommandTargetsOwn(Check check)
    {
        int alpha = 0;
        int beta = 0;
        int gamma = 0;
        var pets = new List<PetMenuItem>
        {
            new("alpha", () => alpha++, () => alpha++, () => alpha++),
            new("beta", () => beta++, () => beta++, () => beta++),
            new("gamma", () => gamma++, () => gamma++, () => gamma++),
        };

        using var tray = new TrayHost(pets, () => { }, () => { });
        bool clicked = tray.ClickPetChild(1, 2); // beta 的「移除」
        bool onlyBeta = clicked && alpha == 0 && beta == 1 && gamma == 0;
        check("ui-tray-pet-command-targets-own", onlyBeta,
            $"clicked={clicked} alpha={alpha} beta={beta} gamma={gamma}");
    }

    /// <summary>
    /// 换语言后托盘文字真的跟着换：固定项、每宠物一条、托盘提示都要用新文案表重建。
    /// 只换 I18n 不重建菜单，用户看到的是「设置里选了 English，托盘还是中文」。
    /// </summary>
    private static void TrayLanguageReload(Check check)
    {
        string original = I18n.Language;
        try
        {
            I18n.Load("zh-CN");
            UiCommand noop = () => { };
            using var tray = new TrayHost(
                new List<PetMenuItem> { new("alpha", noop, noop, noop) },
                noop,
                noop);

            string zhPanel = tray.FixedItemText("panel");
            string zhPet = tray.PetItemTitle(0);

            I18n.Load("en-US");
            tray.ReloadLanguage();
            string enPanel = tray.FixedItemText("panel");
            string enPet = tray.PetItemTitle(0);

            bool ok = !string.Equals(zhPanel, enPanel, StringComparison.Ordinal)
                && !string.Equals(zhPet, enPet, StringComparison.Ordinal)
                && string.Equals(enPanel, I18n.Get("ui.tray.panel"), StringComparison.Ordinal)
                && enPet.Contains("alpha", StringComparison.Ordinal);
            check("ui-tray-language-reload", ok,
                $"panel={zhPanel}→{enPanel} pet={zhPet}→{enPet}");
        }
        finally
        {
            I18n.Load(original);
        }
    }

    /// <summary>
    /// 移除要连清单一起改：被移除的是 active 时，active 交给剩下的第一只
    /// （否则重启之后主窗会悄悄退回默认宠物）。
    /// </summary>
    private static void CatalogRemoval(Check check)
    {
        string root = Path.Combine(Path.GetTempPath(), "petdock-tray-check-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            var store = new PetDataStore(root);
            (string firstName, string firstFolder, _) = Stage(store, root, "alpha-pack");
            store.RegisterActive(firstName, firstFolder);
            (string secondName, string secondFolder, _) = Stage(store, root, "beta-pack");
            store.RegisterActive(secondName, secondFolder);

            bool removed = store.RemoveFromCatalog(secondName);
            IReadOnlyList<PetDataStore.ActivePack> left = store.ReadPets();
            PetDataStore.ActivePack? active = store.ReadActive();
            bool ok = removed
                && left.Count == 1
                && string.Equals(left[0].InstanceId, firstName, StringComparison.Ordinal)
                && active is not null
                && string.Equals(active.InstanceId, firstName, StringComparison.Ordinal);
            check("ui-tray-pet-remove-drops-catalog", ok,
                $"removed={removed} left={left.Count} active={active?.InstanceId ?? "(none)"}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception)
            {
                // 清理失败不影响判据结论。
            }
        }
    }

    private static (string Id, string Folder, string Path) Stage(PetDataStore store, string root, string name)
    {
        string source = Path.Combine(root, "src-" + name);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "pack.json"), "{\"name\":\"" + name + "\"}");
        return store.StagePack(source);
    }
}
