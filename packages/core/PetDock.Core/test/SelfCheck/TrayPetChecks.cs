// RFC-37 §4（片 3）：托盘的**逐只**判据（ui-tray-pet-*）。
//
// 要证的只有两件事：① docs/18 §1 的「每宠物一条子菜单」真的按桌上有几只长出来；
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
        }
        catch (Exception error)
        {
            check("ui-tray-pet-per-item", false, "exception: " + error.GetType().Name);
            check("ui-tray-pet-command-targets-own", false, "exception: " + error.GetType().Name);
        }

        CatalogRemoval(check);
    }

    /// <summary>三只宠物 ⇒ 三条子菜单（pet-1..pet-3），每条四个子项、顺序照 §1。</summary>
    private static void TrayPerItem(Check check)
    {
        UiCommand noop = () => { };
        var pets = new List<PetMenuItem>
        {
            new("alpha", noop, noop, noop, noop),
            new("beta", noop, noop, noop, noop),
            new("gamma", noop, noop, noop, noop),
        };

        using var tray = new TrayHost(pets, noop, noop, noop, noop);
        IReadOnlyList<string> order = tray.ItemOrder;
        bool orderOk = order.Count == 7
            && order[0] == "toggle-all"
            && order[1] == "panel"
            && order[2] == "pet-1"
            && order[3] == "pet-2"
            && order[4] == "pet-3"
            && order[5] == "pause-hooks"
            && order[6] == "exit";

        IReadOnlyList<string> children = tray.PetChildTitles(1);
        bool childrenOk = children.Count == 4
            && children[0] == "移动"
            && children[1] == "暂停"
            && children[2] == "隐藏"
            && children[3] == "移除";

        check("ui-tray-pet-per-item", orderOk && childrenOk && tray.PetItemCount == 3,
            $"items={string.Join(",", order)} children={string.Join("/", children)} count={tray.PetItemCount}");
    }

    /// <summary>点第二条的「隐藏」，只有第二只的命令被调用。</summary>
    private static void TrayCommandTargetsOwn(Check check)
    {
        int alpha = 0;
        int beta = 0;
        int gamma = 0;
        var pets = new List<PetMenuItem>
        {
            new("alpha", () => alpha++, () => alpha++, () => alpha++, () => alpha++),
            new("beta", () => beta++, () => beta++, () => beta++, () => beta++),
            new("gamma", () => gamma++, () => gamma++, () => gamma++, () => gamma++),
        };

        using var tray = new TrayHost(pets, () => { }, () => { }, () => { }, () => { });
        bool clicked = tray.ClickPetChild(1, 2); // beta 的「隐藏」
        bool onlyBeta = clicked && alpha == 0 && beta == 1 && gamma == 0;
        check("ui-tray-pet-command-targets-own", onlyBeta,
            $"clicked={clicked} alpha={alpha} beta={beta} gamma={gamma}");
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
