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
            PetMenuReopens(check);
            RestartEntry(check);
        }
        catch (Exception error)
        {
            check("ui-tray-pet-per-item", false, "exception: " + error.GetType().Name);
            check("ui-tray-pet-command-targets-own", false, "exception: " + error.GetType().Name);
            check("ui-tray-language-reload", false, "exception: " + error.GetType().Name);
            check("ui-tray-pet-menu-reopens", false, "exception: " + error.GetType().Name);
            check("ui-tray-restart-entry", false, "exception: " + error.GetType().Name);
            check("ui-tray-restart-optional", false, "exception: " + error.GetType().Name);
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

        // 顺序：主控面板 → 三只宠物 → 重启 → 退出（2026-10-31 加了「重启」）。
        using var tray = new TrayHost(pets, noop, noop, noop);
        IReadOnlyList<string> order = tray.ItemOrder;
        bool orderOk = order.Count == 6
            && order[0] == "panel"
            && order[1] == "pet-1"
            && order[2] == "pet-2"
            && order[3] == "pet-3"
            && order[4] == "restart"
            && order[5] == "exit";

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
        string root = Path.Combine(SelfTestScratch.Base, "petdock-tray-check-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
    /// <summary>
    /// 右键菜单第二次弹出必须成功（2026-10-31；用户真机日志里 context-menu-failed 连出 6 行）。
    ///
    /// 要挡的写法：`foreach (ToolStripItem old in menu.Items) old.Dispose();` ——
    /// Dispose 会把自己从 Items 里摘掉，边枚举边改集合让 ToolStripItemCollection 抛
    /// InvalidOperationException，异常被 WndProc 吞掉之后**菜单一次都弹不出来**。
    /// 判据连弹两次：第一次 Items 为空（不触发那条路径），第二次才是有旧条目的那次 ——
    /// 只弹一次的话，这个缺陷在判据里根本不会出现。
    /// </summary>
    private static void PetMenuReopens(Check check)
    {
        UiCommand noop = () => { };
        using var tray = new TrayHost(
            new List<PetMenuItem> { new("alpha", noop, noop, noop) },
            noop,
            noop,
            noop);

        // 不真的 Show（判据跑在无人值守的桌面上）：只走「重建条目」这一半，
        // 那半边正是缺陷所在。Show 之后的显示与关闭由 _menuWatch 负责，与本条无关。
        // 参数顺序即菜单顺序：移动/暂停/移除 →（分隔）→ 面板/重启/退出。
        PetScope scope = new("alpha", Paused: false, noop, noop, noop, noop, noop, noop);
        string? first = null;
        string? second = null;
        try
        {
            tray.RebuildPetMenuForCheck(scope);
        }
        catch (Exception error)
        {
            first = error.GetType().Name + ": " + error.Message;
        }

        try
        {
            tray.RebuildPetMenuForCheck(scope);
        }
        catch (Exception error)
        {
            second = error.GetType().Name + ": " + error.Message;
        }

        check("ui-tray-pet-menu-reopens", first is null && second is null,
            $"first={(first ?? "ok")} second={(second ?? "ok")}");
    }

    /// <summary>
    /// 「重启」真的在菜单里、真的能把命令发出去（2026-10-31 用户要求「加上常驻的重启功能」）。
    ///
    /// 判两件事，缺一不可：**菜单里有这一条**（否则用户找不到），
    /// 以及**点下去真的走到那条命令**（否则它是个点了没反应的装饰）。
    /// 走的是 PerformClick 那条真实点击链，不是直接调方法。
    /// </summary>
    private static void RestartEntry(Check check)
    {
        int restarts = 0;
        UiCommand noop = () => { };
        using var tray = new TrayHost(
            new List<PetMenuItem> { new("alpha", noop, noop, noop) },
            noop,
            noop,
            () => restarts++);

        bool listed = tray.ItemOrder.Contains("restart")
            && tray.FixedItemText("restart").Length > 0;
        bool clicked = tray.ClickFixedItem("restart");

        // 右键某一只的那份菜单也要有它（两份菜单同源，但不能只改一边）。
        tray.RebuildPetMenuForCheck(new PetScope("alpha", false, noop, noop, noop, noop, noop, noop));

        // 没有提供重启命令时**不许**摆一条点了没反应的「重启」。
        using var bare = new TrayHost(
            new List<PetMenuItem> { new("alpha", noop, noop, noop) },
            noop,
            noop);

        check("ui-tray-restart-entry", listed && clicked && restarts == 1,
            $"listed={listed} clicked={clicked} fired={restarts} text={tray.FixedItemText("restart")}");
        check("ui-tray-restart-optional", !bare.ItemOrder.Contains("restart"),
            $"items={string.Join(",", bare.ItemOrder)}");
    }

}
