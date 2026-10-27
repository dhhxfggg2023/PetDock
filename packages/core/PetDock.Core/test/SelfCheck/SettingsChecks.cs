// 设置持久化的判据（RFC-18 §4）。
//
// 判据走**生产对象**：真的 UserSettingsStore（真的落盘）、真的 StartupRegistration
// （真的读写注册表）、真的 ControlPanel（真的控件树）、真的 FramePump（真的改周期）。
// 唯一被替换的是注册表的**值名**：自检用自己专用的值名，绝不去动用户真的那个「PetDock」项。
//
// 覆盖的判据：
//   ui-settings-entry-present        设置页有可写控件（勾选框 / 阈值框 / 应用按钮）
//   ui-settings-round-trip           写入的设置能被读回来（值不丢）
//   ui-settings-corrupt-falls-back   损坏的设置文件回落到默认值而不是崩
//   ui-settings-clamped              越界的阈值被拉回合法区间
//   ui-settings-startup-registry     开机自启写进注册表、再读回来是真的开
//   ui-settings-registry-is-source   勾选状态来自注册表（设置文件不重复存这一项）
//   ui-settings-threshold-live       改完阈值立即推给帧泵（不用重启）
//   ui-settings-inline-status        成功/失败都写在内联标签上，不弹模态框
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Core.Ui;
using PetDock.Hosting;

namespace PetDock.Hosting;

internal static class SettingsChecks
{
    /// <summary>判据的输出与计数回调（与 UiSelfTest 的 Check 同形）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    /// <summary>自检专用的注册表值名（不碰用户真的那一项）。</summary>
    private const string ProbeValueName = "PetDock.SelfCheck.Probe";

    /// <summary>自检用的阈值（合法的区间内，80ms = 12.5fps）。</summary>
    private const double ProbePeriodMs = 80.0;

    /// <summary>位置判据用的实例 id：与 PetDriveTest.MakeInstance 造出来的那台一致，
    /// 状态文件名由它算出（StateStore.KeyFor）。</summary>
    private const string ProbeInstanceId = "drive.instance";

    public static void Run(Check check)
    {
        string root = Path.Combine(Path.GetTempPath(), "petdock-settings-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunAll(check, root);
        }
        catch (Exception error)
        {
            check("ui-settings-entry-present", false, $"{error.GetType().Name}: {error.Message}");
            check("ui-settings-round-trip", false, "exception");
            check("ui-settings-corrupt-falls-back", false, "exception");
            check("ui-settings-clamped", false, "exception");
            check("ui-settings-startup-registry", false, "exception");
            check("ui-settings-registry-is-source", false, "exception");
            check("ui-settings-threshold-live", false, "exception");
            check("ui-settings-inline-status", false, "exception");
            check("ui-position-round-trip", false, "exception");
            check("ui-position-absent-is-null", false, "exception");
            check("ui-position-bad-field-falls-back", false, "exception");
            check("ui-position-policy-visible", false, "exception");
            check("ui-position-save-on-move", false, "exception");
            check("ui-position-restore-applies", false, "exception");
            check("ui-position-state-round-trip", false, "exception");
            check("ui-position-migrates-legacy", false, "exception");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void RunAll(Check check, string root)
    {
        var store = new UserSettingsStore(root);

        // ① 控件入口：设置页确实有可写控件（含 2026-10-28 加的材质三选一）。
        using var panel = new ControlPanel(() => UiSnapshot.Empty, showWindow: false);
        IReadOnlyList<string> names = panel.SettingsControlNames;
        bool entryPresent = names.Count == 5
            && names.Contains("startup-box")
            && names.Contains("idle-frame-box")
            && names.Contains("apply-settings")
            && names.Contains("material-picker")
            && names.Contains("theme-picker");
        bool materialPreselected = panel.MaterialKey is "opaque" or "frosted" or "glass";
        bool themePreselected = panel.ThemeKey is "system" or "light" or "dark";
        check("ui-settings-entry-present", entryPresent, "controls=" + string.Join(",", names));
        check("ui-settings-material-selected", materialPreselected, $"material={panel.MaterialKey}");
        check("ui-settings-theme-selected", themePreselected, $"theme={panel.ThemeKey}");

        // ② 往返：写进去再读回来，值不丢。
        store.Save(new UserSettings(ProbePeriodMs, UserSettings.DefaultLanguage));
        UserSettings loaded = store.Load();
        bool roundTrip = Math.Abs(loaded.IdleFrameBudgetMs - ProbePeriodMs) < 0.05
            && string.Equals(loaded.Language, UserSettings.DefaultLanguage, StringComparison.Ordinal);
        check("ui-settings-round-trip", roundTrip, loaded.Describe());

        // ②b 材质往返（原规格 18 §2.7）：认得的值读回来一样，认不得的值当没设过。
        store.Save(new UserSettings(ProbePeriodMs, UserSettings.DefaultLanguage, Material: "glass"));
        string? glassBack = store.Load().Material;
        store.Save(new UserSettings(ProbePeriodMs, UserSettings.DefaultLanguage, Material: "bogus"));
        string? bogusBack = store.Load().Material;
        store.Save(new UserSettings(ProbePeriodMs, UserSettings.DefaultLanguage, Material: "opaque"));
        string? opaqueBack = store.Load().Material;
        check(
            "ui-settings-material-round-trip",
            glassBack == "glass" && bogusBack is null && opaqueBack is null,
            $"glass={glassBack ?? "(null)"} bogus={bogusBack ?? "(null)"} opaque={opaqueBack ?? "(null)"}");

        // ③ 损坏文件：回落默认值，不抛。
        File.WriteAllText(Path.Combine(root, UserSettingsStore.SettingsFile), "{ this is not json");
        UserSettings afterCorrupt = store.Load();
        bool fellBack = Math.Abs(afterCorrupt.IdleFrameBudgetMs - UserSettings.DefaultIdleFrameBudgetMs) < 0.05;
        check("ui-settings-corrupt-falls-back", fellBack, afterCorrupt.Describe());

        // ④ 越界值：被拉回 [1000/60, 1000/5]。
        store.Save(new UserSettings(0.01, UserSettings.DefaultLanguage));
        UserSettings clampedLow = store.Load();
        store.Save(new UserSettings(999999, UserSettings.DefaultLanguage));
        UserSettings clampedHigh = store.Load();
        bool clamped = Math.Abs(clampedLow.IdleFrameBudgetMs - UserSettings.MinIdleFrameBudgetMs) < 0.05
            && Math.Abs(clampedHigh.IdleFrameBudgetMs - UserSettings.MaxIdleFrameBudgetMs) < 0.05;
        check("ui-settings-clamped", clamped,
            $"low={clampedLow.IdleFrameBudgetMs:0.0} high={clampedHigh.IdleFrameBudgetMs:0.0}");

        // ⑤ 开机自启：写进注册表再读回来是真的开（用自检专用的值名）。
        string? enableFailure = StartupRegistration.EnableAt(ProbeValueName);
        bool registered = StartupRegistration.IsEnabledAt(ProbeValueName);
        check("ui-settings-startup-registry", enableFailure is null && registered,
            $"failure={enableFailure ?? "(none)"} registered={registered}");

        // ⑥ 事实源在注册表：关掉之后设置文件里仍然没有这一项，但读数跟着变。
        string? disableFailure = StartupRegistration.DisableAt(ProbeValueName);
        bool cleared = !StartupRegistration.IsEnabledAt(ProbeValueName);
        bool fileHasNoStartupField = !FileContainsStartupKey(root);
        check("ui-settings-registry-is-source", disableFailure is null && cleared && fileHasNoStartupField,
            $"cleared={cleared} fileHasNoStartupField={fileHasNoStartupField}");

        // ⑦ 阈值立即生效：改完不用重启，帧泵的目标周期就变了。
        using var pump = new FramePump();
        pump.SetTargetFrameMs(UserSettings.DefaultIdleFrameBudgetMs);
        double before = pump.TargetFrameMs;
        pump.SetTargetFrameMs(ProbePeriodMs);
        double after = pump.TargetFrameMs;
        check("ui-settings-threshold-live",
            Math.Abs(after - ProbePeriodMs) < 0.05 && Math.Abs(before - after) > 1.0,
            $"before={before:0.0}ms after={after:0.0}ms");

        // ⑧ 内联状态：宿主侧回一句话，面板显示成内联标签（不弹模态框）。
        int formsBefore = OwnedFormCount();
        panel.SetSettingsCommand((_, _, _, _) => "自检：假装失败");
        panel.TryApplySettingsForCheck(ProbePeriodMs, false);
        bool shown = panel.SettingsStatusShown && panel.SettingsStatusText.Length > 0;
        bool noModal = OwnedFormCount() == formsBefore;
        check("ui-settings-inline-status", shown && noModal,
            $"shown={shown} text={panel.SettingsStatusText} ownedForms={OwnedFormCount()}");

        // ⑨ 位置往返（RFC-22 §4）：写进去、读回来，读数里带 position。
        store.Save(new UserSettings(ProbePeriodMs, UserSettings.DefaultLanguage, PetPosition: new Point(120, 340)));
        UserSettings withPosition = store.Load();
        bool positionRoundTrip = withPosition.PetPosition == new Point(120, 340)
            && withPosition.Describe().Contains("position=120,340", StringComparison.Ordinal);
        check("ui-position-round-trip", positionRoundTrip, withPosition.Describe());

        // ⑩ 没存过 = null（不是 `0,0` —— 后者会把宠物吸到左上角）。
        File.Delete(Path.Combine(root, UserSettingsStore.SettingsFile));
        UserSettings fresh = store.Load();
        check("ui-position-absent-is-null", fresh.PetPosition is null, fresh.Describe());

        // ⑪ 坏值：坐标不是整数 ⇒ 当「没存过」，且留一行可查的日志。
        File.WriteAllText(
            Path.Combine(root, UserSettingsStore.SettingsFile),
            "{ \"petPosition\": { \"x\": \"abc\", \"y\": 1 } }");
        UserSettings broken = store.Load();
        check("ui-position-bad-field-falls-back", broken.PetPosition is null, broken.Describe());

        // ⑫ 可见性策略（纯函数）：屏内、跨边（贴着屏幕边缘）都算可见，完全在屏外算不可见。
        Rectangle[] areas =
        {
            new(0, 0, 1920, 1040),
            new(1920, 0, 1280, 1024),
        };
        var windowSize = new Size(128, 128);
        bool inside = ScreenPlacement.IsVisible(new Point(100, 100), windowSize, areas);
        bool straddling = ScreenPlacement.IsVisible(new Point(1900, 500), windowSize, areas);
        bool outside = ScreenPlacement.IsVisible(new Point(5000, 5000), windowSize, areas);
        check("ui-position-policy-visible", inside && straddling && !outside,
            $"inside={inside} straddling={straddling} outside={outside}");

        // ⑬⑭ 要造真窗口与真宿主：放在最后，因为它们会在真桌面上摆出一台宠物窗。
        RunPositionIoChecks(check, store);

        // ⑮⑯ 位置的事实源搬进状态层（RFC-24 §3.9）：也要真窗口与真宿主。
        RunStatePositionChecks(check, store, root);
    }

    /// <summary>
    /// 位置落点的两条判据（RFC-24 §3.9 / §4）：有状态层时位置只进状态文件，
    /// `settings.json` 里的旧键只在升级时出现一次然后消失。
    /// 这两条是「迁移没发生」和「旧位置被静默丢弃」两个症状的判据面。
    /// </summary>
    private static void RunStatePositionChecks(Check check, UserSettingsStore store, string root)
    {
        // ⑮ 落点是状态层：真宿主 + 真 store + 真状态文件；重开一个 store 读回同一坐标。
        string stateDir = Path.Combine(root, "state-round-trip");
        store.Save(new UserSettings(ProbePeriodMs, UserSettings.DefaultLanguage));
        using (var window = new LayeredFrameWindow())
        {
            PetInstance pet = PetDriveTest.MakeInstance(out _, out _);
            try
            {
                pet.InstallStateLayer(PersistentPetState.Attach(
                    ProbeInstanceId, new MemoryPetState(), stateDir, null, null));
                using var host = new UiHost(window, pet, null, null, null, store);
                host.SavePetPosition(320, 240);
            }
            finally
            {
                pet.Dispose();
            }

            Point? written = ReadPersistedPosition(stateDir);
            UserSettings afterWrite = store.Load();
            bool settingsClean = afterWrite.PetPosition is null && !SettingsFileHasPosition(root);
            check("ui-position-state-round-trip",
                written == new Point(320, 240) && settingsClean,
                $"state={Describe(written)} settings-position={(afterWrite.PetPosition is null ? "(none)" : "present")}");
        }

        // ⑯ 一次性迁移：旧 `settings.json` 有坐标、状态文件没有 → 恢复时搬进状态层并删旧键。
        string migrateDir = Path.Combine(root, "state-migrate");
        store.Save(new UserSettings(ProbePeriodMs, UserSettings.DefaultLanguage, PetPosition: new Point(200, 180)));
        using (var window = new LayeredFrameWindow())
        {
            PetInstance pet = PetDriveTest.MakeInstance(out _, out _);
            try
            {
                pet.InstallStateLayer(PersistentPetState.Attach(
                    ProbeInstanceId, new MemoryPetState(), migrateDir, null, null));
                using var host = new UiHost(window, pet, null, null, null, store);
                host.TryRestorePosition();
                (int restoredX, int restoredY) = window.CurrentPosition;
                Point? migrated = ReadPersistedPosition(migrateDir);
                bool legacyGone = store.Load().PetPosition is null && !SettingsFileHasPosition(root);
                check("ui-position-migrates-legacy",
                    restoredX == 200 && restoredY == 180 && migrated == new Point(200, 180) && legacyGone,
                    $"restored={restoredX},{restoredY} state={Describe(migrated)} legacyGone={legacyGone}");
            }
            finally
            {
                pet.Dispose();
            }
        }
    }

    /// <summary>重开一个 store 从磁盘读回保留键 `position`（不是复用宿主里那个对象）。</summary>
    private static Point? ReadPersistedPosition(string stateDir)
    {
        PersistentPetState reopened = PersistentPetState.Attach(
            ProbeInstanceId, new MemoryPetState(), stateDir, null, null);
        return reopened.Read(PersistentPetState.PositionKey) is JsonObject node
            && node["x"] is JsonValue x
            && node["y"] is JsonValue y
            ? new Point(x.GetValue<int>(), y.GetValue<int>())
            : null;
    }

    private static string Describe(Point? point) => point is Point value ? $"{value.X},{value.Y}" : "(none)";

    /// <summary>`settings.json` 里还有没有位置这一项（迁移完成后必须没有）。</summary>
    private static bool SettingsFileHasPosition(string root)
    {
        string path = Path.Combine(root, UserSettingsStore.SettingsFile);
        return File.Exists(path)
            && File.ReadAllText(path).Contains("petPosition", StringComparison.Ordinal);
    }

    /// <summary>窗口/宿主级的两个位置判据（⑬⑭）。</summary>
    private static void RunPositionIoChecks(Check check, UserSettingsStore store)
    {
        // ⑬ 移动落盘（端到端）：真窗口、真宿主、真 store —— 从窗口层的事件委托进，
        //    文件里必须出现那个坐标。「事件到了没人落盘」正是本 RFC 要修的症状。
        using (var window = new LayeredFrameWindow())
        {
            using var host = new UiHost(window, null, null, null, null, store);
            window.RaiseMovedForCheck(300, 420);
            UserSettings afterMove = store.Load();
            check("ui-position-save-on-move", afterMove.PetPosition == new Point(300, 420),
                afterMove.Describe());
        }

        // ⑭ 恢复接线（RFC-22 §3.3）：存一个屏幕内的位置，宿主必须真的把窗口摆过去；
        //    再存一个屏幕外的位置，宿主必须**不动**窗口（丢弃 = 保留默认位，而不是摆到 20000,20000）。
        using (var window = new LayeredFrameWindow())
        {
            using var host = new UiHost(window, null, null, null, null, store);
            store.Save(new UserSettings(ProbePeriodMs, UserSettings.DefaultLanguage, PetPosition: new Point(200, 180)));
            host.TryRestorePosition();
            (int restoredX, int restoredY) = window.CurrentPosition;
            bool restored = restoredX == 200 && restoredY == 180;

            store.Save(new UserSettings(ProbePeriodMs, UserSettings.DefaultLanguage, PetPosition: new Point(20000, 20000)));
            host.TryRestorePosition();
            (int keptX, int keptY) = window.CurrentPosition;
            check("ui-position-restore-applies", restored && keptX == 200 && keptY == 180,
                $"restored={restoredX},{restoredY} afterOffscreen={keptX},{keptY}");
        }
    }

    /// <summary>设置文件里不该出现开机自启这一项（事实源是注册表）。</summary>
    private static bool FileContainsStartupKey(string root)
    {
        string path = Path.Combine(root, UserSettingsStore.SettingsFile);
        if (!File.Exists(path))
        {
            return false;
        }

        string text = File.ReadAllText(path);
        return text.Contains("startup", StringComparison.OrdinalIgnoreCase)
            || text.Contains("autoStart", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前打开的窗体数（判据「不弹模态框」的面）。</summary>
    private static int OwnedFormCount()
    {
        int count = 0;
        foreach (Form form in Application.OpenForms)
        {
            if (form is ControlPanel)
            {
                count++;
            }
        }

        return count;
    }

    private static void Cleanup(string root)
    {
        try
        {
            StartupRegistration.DisableAt(ProbeValueName);
        }
        catch (Exception)
        {
            // 清不掉探针值不影响判据结论。
        }

        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响判据结论。
        }
    }
}
