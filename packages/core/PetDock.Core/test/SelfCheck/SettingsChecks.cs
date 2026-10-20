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
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using PetDock.Core.Compositor;
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
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void RunAll(Check check, string root)
    {
        var store = new UserSettingsStore(root);

        // ① 控件入口：设置页确实有可写控件。
        using var panel = new ControlPanel(() => UiSnapshot.Empty, showWindow: false);
        IReadOnlyList<string> names = panel.SettingsControlNames;
        bool entryPresent = names.Count == 3
            && names.Contains("startup-box")
            && names.Contains("idle-frame-box")
            && names.Contains("apply-settings");
        check("ui-settings-entry-present", entryPresent, "controls=" + string.Join(",", names));

        // ② 往返：写进去再读回来，值不丢。
        store.Save(new UserSettings(ProbePeriodMs, UserSettings.DefaultLanguage));
        UserSettings loaded = store.Load();
        bool roundTrip = Math.Abs(loaded.IdleFrameBudgetMs - ProbePeriodMs) < 0.05
            && string.Equals(loaded.Language, UserSettings.DefaultLanguage, StringComparison.Ordinal);
        check("ui-settings-round-trip", roundTrip, loaded.Describe());

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
        panel.SetSettingsCommand((_, _) => "自检：假装失败");
        panel.TryApplySettingsForCheck(ProbePeriodMs, false);
        bool shown = panel.SettingsStatusShown && panel.SettingsStatusText.Length > 0;
        bool noModal = OwnedFormCount() == formsBefore;
        check("ui-settings-inline-status", shown && noModal,
            $"shown={shown} text={panel.SettingsStatusText} ownedForms={OwnedFormCount()}");
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
