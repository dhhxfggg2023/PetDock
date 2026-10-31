// 宠物页「收起 / 放回」这一片的判据（2026-10-31）。
//
// 用户报：「宠物收起无法正常取出」。根因不在收起本身，在**面板快照**：
//   主窗那只被收起之后，UiHost.Snapshot() 把 _pet 与主 id 一起传成 null，
//   UiRoster 于是不再给它出行 —— 列表里没有那一行，也就没有「在桌面上显示」这个按钮。
//   而 BuildExtraPetLines 明确 continue 跳过主窗那只（注释写的是「由 UiRoster 出」），
//   两个函数都以为对方会出这一行，合起来谁都没出。
//   （2026-10-30 修「已导入的宠物没地方看」时只覆盖了非主窗那只。）
//
// 判据口径：**清单里的每一只都必须有一行，不管它现在摆没摆着** —— OnDesktop 说状态。
// 这条口径原本只写在 BuildExtraPetLines 的注释里，注释拦不住回归。
using System;
using System.IO;
using System.Linq;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal static class PetsDesktopChecks
 {
    internal delegate void Check(string name, bool ok, string detail);

    public static void Run(Check check)
    {
        string? previousRoot = Environment.GetEnvironmentVariable(PetDataStore.RootEnv);
        string root = Path.Combine(SelfTestScratch.Base, "pets-desktop-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        PetInstance? pet = null;
        try
        {
            // 数据区指到临时目录：这一条要真的走 UiHost 里那次 PetDataStore.Resolve()，
            // 但不许碰用户的 data\。
            Environment.SetEnvironmentVariable(PetDataStore.RootEnv, root);
            var store = new PetDataStore(root);
            (string id, string folder, _) = Stage(store, root);
            store.RegisterActive(id, folder, "测试桌宠");

            pet = PetDriveTest.MakeInstance(out _, out _);
            using var window = new LayeredFrameWindow();
            window.Hide();
            using var host = new UiHost(window, pet, null, null, null, null);

            // 与真实运行同形：先过一个滴答（_petShown 在这一刻为真），宠物才在桌上。
            host.TickOnce();

            // ① 收起：这一只必须**仍然在列表里**，只是状态变成「不在桌面上」。
            string? hideProblem = host.SetPetOnDesktopFromPanel(id, false);
            UiSnapshot hidden = host.Snapshot();
            PetLine? hiddenLine = Find(hidden, id);
            check("ui-pets-desktop-stays-listed",
                hideProblem is null && hiddenLine is not null && !hiddenLine.OnDesktop,
                $"problem={hideProblem ?? "none"} rows=[{Describe(hidden)}] listed={hiddenLine is not null} onDesktop={hiddenLine?.OnDesktop}");

            // ② 放回：要有入口（上一行就是入口），并且真的回到桌面上。
            string? showProblem = host.SetPetOnDesktopFromPanel(id, true);
            UiSnapshot shown = host.Snapshot();
            PetLine? shownLine = Find(shown, id);
            check("ui-pets-desktop-roundtrip",
                showProblem is null && shownLine?.OnDesktop == true && window.Visible,
                $"problem={showProblem ?? "none"} rows=[{Describe(shown)}] onDesktop={shownLine?.OnDesktop} visible={window.Visible}");

            // ③ 收起的这一只也要能删：身份（是不是主窗那只）与可见性（在不在桌上）必须分开判。
            //    合并判的话，「收起的行」一送到删除入口就会掉进「非主窗那只」那一支，
            //    而那一支找的是 _petSet 里的槽位 —— 主窗那只从来不在里面，用户得到的是「找不到」。
            string? hideAgain = host.SetPetOnDesktopFromPanel(id, false);
            string? deleteProblem = host.DeletePetFromPanel(id);
            int leftInCatalog = store.ReadPets().Count;
            // 不只是「那一行」不在了：删除之后整张列表都该是空的。
            // （只断言 Find(id)==null 会漏掉「换了个 id 的幽灵行」—— 这一条抓到过一次。）
            bool goneFromList = host.Snapshot().Pets.Count == 0;
            check("ui-pets-desktop-delete-while-hidden",
                hideAgain is null && deleteProblem is null && leftInCatalog == 0 && goneFromList,
                $"hide={hideAgain ?? "none"} delete={deleteProblem ?? "none"} left={leftInCatalog} rows=[{Describe(host.Snapshot())}]");
        }
        catch (Exception error)
        {
            check("ui-pets-desktop-stays-listed", false, $"{error.GetType().Name}: {error.Message}");
            check("ui-pets-desktop-roundtrip", false, $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            pet?.Dispose();
            Environment.SetEnvironmentVariable(PetDataStore.RootEnv, previousRoot);
            TryDelete(root);
        }
    }

    private static PetLine? Find(UiSnapshot snapshot, string id) =>
        snapshot.Pets.FirstOrDefault(line => string.Equals(line.Id, id, StringComparison.Ordinal));

    private static string Describe(UiSnapshot snapshot) =>
        snapshot.Pets.Count == 0
            ? "(none)"
            : string.Join(",", snapshot.Pets.Select(line => line.Id + (line.OnDesktop ? ":desktop" : ":hidden")));

    /// <summary>一个最小素材包：与 PetSetChecks 用同一条导入路径（StagePack + RegisterActive）。</summary>
    private static (string Id, string Folder, string Path) Stage(PetDataStore store, string root)
    {
        string source = Path.Combine(root, "src-pack");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "pack.json"), "{\"name\":\"测试桌宠\"}");
        return store.StagePack(source);
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 临时目录删不掉不影响结论。
        }
    }
}
