// RFC-37 §7 第 4 条（多宠物欠账）的判据：**位置按宠物**，不是按窗口索引。
//
// 三条判据各挡一种会让「我记得把它拖到那儿」变成假的写法：
//   1. 写入后重读一致 —— 存进去的坐标必须能被**另一个** store 对象从磁盘读回同一个值；
//      「同一个对象自己读自己的内存」不算存下来（这条挡的是「写在缓冲区里就当成落盘」）。
//   2. 缺条目回落 —— 没有条目的那只退回片 1b 的 140 间距布局，且**不**抛；
//      这条挡的是「为了记住位置而让缺条目的那只跑到屏幕左上角」或「跟着别的那只的坐标走」。
//   3. 未知 id 被忽略 —— 已被移除的宠物留下的条目不许影响在册那只的落点，更不许崩。
//
// 判据走真对象：真 UserSettingsStore（真落盘）、真 Screen 工作区、真 PetWindowSet（真窗口 + 真泵）。
// 位置到窗口那一跳不靠猜：读 window.CurrentPosition 的实测值。
// 三条各用自己的临时根目录：共用一份设置会让上一条存下的坐标把下一条的「缺条目」变成假前提。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class PetPositionChecks
{
    /// <summary>与其它判据文件同一形态：每个文件自带一份（这些文件之间不共享类型）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    /// <summary>判据体的形状（自声明理由同 HostingTypes.cs 的先例）。</summary>
    private delegate void RootBody(string root);

    private const string AlphaId = "user.alpha";
    private const string BetaId = "user.beta";
    private const string RemovedId = "user.gone";

    public static void Run(Check check)
    {
        WithRoot(check, "ui-pet-position-persist-round-trip", root => RoundTrip(check, new UserSettingsStore(root)));
        WithRoot(check, "ui-pet-position-persist-missing-falls-back", root => MissingFallsBack(check, new UserSettingsStore(root)));
        WithRoot(check, "ui-pet-position-persist-unknown-ignored", root => UnknownIdIgnored(check, new UserSettingsStore(root)));
        WithRoot(check, "ui-pet-position-persist-restart-restores", root => RestartRestores(check, root));
    }

    /// <summary>
    /// 起一个一次性数据区、跑一条判据、无论成败都收走它。
    /// 外面这层 catch 兜的是「连 store 都造不出来」—— 那种情形也必须落成一条 FAIL 行，
    /// 否则 --uitest 会静默少一条 CHECK（少判据比红判据更坏）。
    /// </summary>
    private static void WithRoot(Check check, string name, RootBody body)
    {
        string root = Path.Combine(
            Path.GetTempPath(), "petdock-position-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            body(root);
        }
        catch (Exception error)
        {
            check(name, false, $"setup-failed {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>① 写入后重读一致：另一个 store 对象读回同一坐标，且别的字段与其它条目都没被抹掉。</summary>
    private static void RoundTrip(Check check, UserSettingsStore store)
    {
        var set = (PetWindowSet?)null;
        LayeredFrameWindow? primary = null;
        LayeredFrameWindow? second = null;
        try
        {
            primary = new LayeredFrameWindow();
            second = new LayeredFrameWindow();
            set = new PetWindowSet(primary) { Settings = store };
            set.Add(new PetWindowSet.Slot(null, "pack-alpha", second, null, new ResidentFrameLoop(second), AlphaId));

            bool saved = set.SavePosition(AlphaId, new Point(411, 233));

            // 重开一份 store：读的是磁盘，不是刚才那个对象的内存。
            UserSettings loaded = new UserSettingsStore(RootOf(store)).Load();
            Point? readBack = loaded.PetPositions is { } map && map.TryGetValue(AlphaId, out Point hit) ? hit : null;
            bool roundTrip = saved
                && readBack == new Point(411, 233)
                && Math.Abs(loaded.IdleFrameBudgetMs - UserSettings.DefaultIdleFrameBudgetMs) < 0.5;

            // 再写第二只：第一条不许消失（读-改-写整份设置，不是覆盖整张表）。
            set.SavePosition(BetaId, new Point(97, 55));
            UserSettings both = new UserSettingsStore(RootOf(store)).Load();
            bool keptFirst = both.PetPositions is { } after
                && after.TryGetValue(AlphaId, out Point alpha) && alpha == new Point(411, 233)
                && after.TryGetValue(BetaId, out Point beta) && beta == new Point(97, 55);

            // 拖拽落地那条链本身（不经 SavePosition 直接调）：
            // 窗口抬 core.window.moved → 订阅里落盘。这条挡「接线只在一半路径上」。
            second.RaiseMovedForCheck(612, 344);
            UserSettings viaEvent = new UserSettingsStore(RootOf(store)).Load();
            Point? eventCoord = viaEvent.PetPositions is { } t && t.TryGetValue(AlphaId, out Point moved) ? moved : null;

            check("ui-pet-position-persist-round-trip",
                roundTrip && keptFirst && eventCoord == new Point(612, 344),
                $"saved={saved} read-back={Describe(readBack)} first-kept={Flag(keptFirst)}"
                + $" entries={(both.PetPositions?.Count ?? 0)} via-event={Describe(eventCoord)}");
        }
        catch (Exception error)
        {
            check("ui-pet-position-persist-round-trip", false, $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            set?.Dispose();
            primary?.Dispose();
            second?.Dispose();
        }
    }

    /// <summary>
    /// ② 缺条目回落：给主窗之外的某只**不**存位置，它必须落在片 1b 的布局上
    /// （主窗左侧第 140），而不是 (0,0)、也不是别的宠物的坐标。
    /// 纯函数与真窗口两半都断：前半挡「回落公式变了」，后半挡「接线上没走那条公式」。
    /// </summary>
    private static void MissingFallsBack(Check check, UserSettingsStore store)
    {
        Rectangle[] areas = WorkAreas();
        var size = new Size(128, 128);
        var primary = (14, 32);
        var saved = new Point(500, 400);

        (int hitX, int hitY) = PetWindowSet.ResolveStart(saved, primary, 1, size, areas);
        (int missX, int missY) = PetWindowSet.ResolveStart(null, primary, 1, size, areas);
        (int offX, int offY) = PetWindowSet.ResolveStart(new Point(90000, 90000), primary, 1, size, areas);

        bool savedWins = hitX == 500 && hitY == 400;
        bool fallback = missX == primary.Item1 - 140 && missY == primary.Item2;
        bool offscreenDiscarded = offX == missX && offY == missY;

        var set = (PetWindowSet?)null;
        LayeredFrameWindow? primaryWindow = null;
        LayeredFrameWindow? second = null;
        try
        {
            primaryWindow = new LayeredFrameWindow();
            second = new LayeredFrameWindow();
            set = new PetWindowSet(primaryWindow) { Settings = store };

            // 这件设置文件里一条逐宠物位置都没有（同一份 store 只被本判据用过）。
            bool emptyFile = store.Load().PetPositions is null or { Count: 0 };

            set.Add(new PetWindowSet.Slot(null, "pack-beta", second, null, new ResidentFrameLoop(second), BetaId));
            set.StartAll();

            (int px, int py) = primaryWindow.CurrentPosition;
            (int sx, int sy) = second.CurrentPosition;
            int gap = px - sx;
            bool placed = gap >= 120 && sy == py;

            check("ui-pet-position-persist-missing-falls-back",
                savedWins && fallback && offscreenDiscarded && emptyFile && placed,
                $"saved={hitX},{hitY} fallback={missX},{missY} offscreen={offX},{offY}"
                + $" empty={Flag(emptyFile)} window-gap={gap} window-y={sy}");
        }
        catch (Exception error)
        {
            check("ui-pet-position-persist-missing-falls-back", false, $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            set?.Dispose();
            primaryWindow?.Dispose();
            second?.Dispose();
        }
    }

    /// <summary>
    /// ③ 未知 id 被忽略：文件里留着已移除宠物的条目（可以留着），加载时不许拿它去摆任何窗口，
    /// 也不许让在册那只跟着它走。两半：加载不抛且条目仍在（允许留着）、落点只由在册的 id 决定。
    /// </summary>
    private static void UnknownIdIgnored(Check check, UserSettingsStore store)
    {
        var set = (PetWindowSet?)null;
        LayeredFrameWindow? primaryWindow = null;
        LayeredFrameWindow? second = null;
        try
        {
            // 正常途径的形状：它曾经被拖到那儿，之后被移出清单（移除不清理位置条目）。
            var positions = new Dictionary<string, Point>(StringComparer.Ordinal)
            {
                [RemovedId] = new Point(1200, 900),
            };
            store.Save(new UserSettings(
                UserSettings.DefaultIdleFrameBudgetMs, UserSettings.DefaultLanguage, PetPositions: positions));

            UserSettings loaded = new UserSettingsStore(RootOf(store)).Load();
            bool entryKept = loaded.PetPositions is { } map && map.ContainsKey(RemovedId);

            primaryWindow = new LayeredFrameWindow();
            second = new LayeredFrameWindow();
            set = new PetWindowSet(primaryWindow) { Settings = store };

            // 桌面上只有在册的 beta 一只（已移除的那只没有窗口，也不该被造出来）。
            set.Add(new PetWindowSet.Slot(null, "pack-beta", second, null, new ResidentFrameLoop(second), BetaId));
            set.StartAll();

            (int px, int py) = primaryWindow.CurrentPosition;
            (int bx, int by) = second.CurrentPosition;
            int gap = px - bx;
            bool notHijacked = Math.Abs(gap - 140) < 8 && by == py;
            bool noExtraWindow = set.Count == 2 && set.Slots.Count == 1;

            // 再断一次纯函数：拿未知 id 当键去查，命中的坐标绝不能被当作 beta 的落点。
            Point? unknown = loaded.PetPositions is { } table && table.TryGetValue(BetaId, out Point mine) ? mine : null;
            (int resolvedX, _) = PetWindowSet.ResolveStart(unknown, (px, py), 1, second.WindowSize, WorkAreas());
            bool unknownNotApplied = resolvedX == px - 140;

            check("ui-pet-position-persist-unknown-ignored",
                entryKept && notHijacked && noExtraWindow && unknownNotApplied,
                $"entry-kept={Flag(entryKept)} gap={gap} y={by} pets={set.Count}"
                + $" extras={set.Slots.Count} resolved={resolvedX} expected={px - 140}");
        }
        catch (Exception error)
        {
            check("ui-pet-position-persist-unknown-ignored", false, $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            set?.Dispose();
            primaryWindow?.Dispose();
            second?.Dispose();
        }
    }

    /// <summary>
    /// ④ 重启后回到各自位置：这是本片真正的验收面 —— 上面三条证明了「存得进、缺了就回落、陌生人被忽略」，
    /// 但都没证明「重启之后那只真的回到我拖到的地方」。
    /// 做法是**真重启那种形状**：第一个集合写出位置并释放（进程结束），第二个集合从同一个设置文件重新装配，
    /// 摆上桌之后读窗口的实测坐标。两件事都要对：存过的那只回到旧坐标，没存过的那只仍在 140 间距上。
    /// </summary>
    private static void RestartRestores(Check check, string root)
    {
        var target = new Point(721, 415);
        var store = new UserSettingsStore(root);

        // 第一趟（「上一次运行」）：把 alpha 拖到 target 并落盘，然后释放全部窗口。
        LayeredFrameWindow? firstPrimary = null;
        LayeredFrameWindow? firstSecond = null;
        PetWindowSet? first = null;
        try
        {
            firstPrimary = new LayeredFrameWindow();
            firstSecond = new LayeredFrameWindow();
            first = new PetWindowSet(firstPrimary) { Settings = store };
            first.Add(new PetWindowSet.Slot(null, "pack-alpha", firstSecond, null, new ResidentFrameLoop(firstSecond), AlphaId));
            first.SavePosition(AlphaId, target);
        }
        finally
        {
            first?.Dispose();
            firstPrimary?.Dispose();
            firstSecond?.Dispose();
        }

        // 第二趟（「这一次启动」）：另一份 store、另一批窗口，只按设置文件装配。
        LayeredFrameWindow? primary = null;
        LayeredFrameWindow? second = null;
        var set = (PetWindowSet?)null;
        try
        {
            primary = new LayeredFrameWindow();
            second = new LayeredFrameWindow();
            set = new PetWindowSet(primary) { Settings = new UserSettingsStore(root) };
            set.Add(new PetWindowSet.Slot(null, "pack-alpha", second, null, new ResidentFrameLoop(second), AlphaId));
            set.StartAll();

            (int sx, int sy) = second.CurrentPosition;
            bool restored = sx == target.X && sy == target.Y;

            // 缺条目的那只（beta）落在**它自己那一格**的 140 间距上（第 2 条槽位 ⇒ 280）。
            // 它不许跟着 alpha 的存档坐标走，这才是「缺条目回落」在重启之后的样子。
            var betaWindow = new LayeredFrameWindow();
            set.Add(new PetWindowSet.Slot(null, "pack-beta", betaWindow, null, new ResidentFrameLoop(betaWindow), BetaId));
            set.StartAll();
            (int px, int py) = primary.CurrentPosition;
            (int bx, int by) = betaWindow.CurrentPosition;
            bool betaFellBack = Math.Abs((px - bx) - 280) < 8 && by == py;

            check("ui-pet-position-persist-restart-restores", restored && betaFellBack,
                $"restored={sx},{sy} expected={target.X},{target.Y}"
                + $" beta-gap={px - bx} beta-y={by}");

            // betaWindow 归 set 所有（槽位里那台泵还在跑它）：释放交给 set.Dispose()，
            // 自己再 Dispose 一次会让泵线程在已释放的 DIB 上取帧（实测整进程 AccessViolation）。
        }
        catch (Exception error)
        {
            check("ui-pet-position-persist-restart-restores", false, $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            set?.Dispose();
            primary?.Dispose();
            second?.Dispose();
        }
    }

    /// <summary>store 指向的数据区根（`Path` 是设置文件本身，根是它的父目录）。</summary>
    private static string RootOf(UserSettingsStore store) =>
        Path.GetDirectoryName(store.Path) ?? Path.GetTempPath();

    private static Rectangle[] WorkAreas()
    {
        Screen[] screens = Screen.AllScreens;
        var areas = new Rectangle[screens.Length];
        for (int i = 0; i < screens.Length; i++)
        {
            areas[i] = screens[i].WorkingArea;
        }

        return areas;
    }

    private static string Describe(Point? point) => point is Point p ? $"{p.X},{p.Y}" : "(none)";

    private static string Flag(bool value) => value ? "true" : "false";

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
