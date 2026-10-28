// 首次运行引导的判据（RFC-16 §4）。
//
// 2026-10-28 删掉「放置示例宠物」步与独立的穿透说明页（用户问的「第二个选择框」就是
// 那页的大示意框）。同一天语言选择搬进设置页，引导只剩一页「使用提示」。
// 判据跟着改：不再测示例包的复制/登记/换源（那是导入服务的判据，见 ImportChecks），
// 改为钉住「正好一页、没有语言选择」，并保留设置落盘、错误内联与帧透明区。
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class OnboardingChecks
{
    /// <summary>判据的输出与计数回调（与 UiSelfTest 的 Check 同形）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    /// <summary>判据名清单（异常兜底时逐条落 FAIL —— 一条只打印不计数等于没有门禁）。</summary>
    private static readonly string[] AllNames =
    {
        "ui-onboarding-one-step",
        "ui-onboarding-runs-once",
        "ui-onboarding-not-repeated",
        "ui-onboarding-marker-survives-settings-write",
        "ui-onboarding-state-audit-fields",
        "ui-onboarding-no-language-choice",
        "ui-onboarding-keeps-language",
        "ui-onboarding-through-caption",
        "ui-onboarding-inline-error",
        "ui-onboarding-no-modal-error",
        "ui-onboarding-frame-alpha-transparent",
    };

    public static void Run(Check check)
    {
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "petdock-onboarding-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunAll(check, root);
        }
        catch (Exception error)
        {
            foreach (string name in AllNames)
            {
                check(name, false, $"{error.GetType().Name}: {error.Message}");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void RunAll(Check check, string root)
    {
        var store = new UserSettingsStore(root);

        // 写设置要「读出 → 改一个字段 → 整份写回」（RFC-16 §3.2）。
        bool WriteSettings(bool complete, out string error)
        {
            error = string.Empty;
            try
            {
                UserSettings previous = store.Load();
                store.Save(previous with
                {
                    OnboardingCompletedVersion = complete
                        ? UserSettings.OnboardingVersion
                        : previous.OnboardingCompletedVersion,
                });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        using (var window = new OnboardingWindow(WriteSettings, hasPet: true))
        {
            check("ui-onboarding-one-step", window.StepCount == 1, $"steps={window.StepCount}");

            // 引导不再让用户选语言：整页没有单选框。语言是设置页的事实（默认中文），
            // 引导替用户改它才是越权 —— 页面只负责指路（「设置页里随时可以改」）。
            int radios = window.RadioButtonCount(0);
            string usageText = window.CollectStepText(0);
            bool pointsToSettings = usageText.Contains(I18n.Get("ui.onboarding.usage.note"), StringComparison.Ordinal);
            check("ui-onboarding-no-language-choice",
                radios == 0 && pointsToSettings,
                $"radios={radios} points-to-settings={pointsToSettings}");

            // 穿透说明原样出现在这一页（断言键解析出的那句，不是中文原文）。
            string throughText = window.CollectStepText(0);
            check("ui-onboarding-through-caption",
                throughText.Contains(I18n.Get(OnboardingWindow.ThroughCaptionKey), StringComparison.Ordinal),
                $"key={OnboardingWindow.ThroughCaptionKey} text={throughText.Replace('\n', '|')}");

            // 一次「完成」：只写一次性标记，语言保持默认中文不变（引导不越权改语言）。
            string languageBefore = store.Load().Language;
            window.GoNext();
            check("ui-onboarding-keeps-language",
                string.Equals(store.Load().Language, languageBefore, StringComparison.Ordinal),
                $"before={languageBefore} after={store.Load().Language}");
            check("ui-onboarding-runs-once",
                window.MarkerWritten && store.Load().OnboardingCompletedVersion == UserSettings.OnboardingVersion,
                $"written={window.MarkerWritten} marker={store.Load().OnboardingCompletedVersion}");
        }

        // ④ 标记存在时宿主不再想跑引导（真的 UiHost 判定面）。
        var frames = new SwappableFrameSource(SolidFrameContent.KernelFallback(), new PetSurfaceRoster());
        using (var window = new LayeredFrameWindow())
        {
            window.Hide();
            using var host = new UiHost(window, null, null, frames, null, store);
            check("ui-onboarding-not-repeated", !host.NeedsOnboarding(),
                $"needs={host.NeedsOnboarding()} marker={store.Load().OnboardingCompletedVersion}");
        }

        // ⑤ 设置页写盘不抹掉标记（RFC-16 §3.2 的核心风险）。
        UserSettings before = store.Load();
        store.Save(before with { IdleFrameBudgetMs = 80.0 });
        UserSettings after = store.Load();
        check("ui-onboarding-marker-survives-settings-write",
            after.OnboardingCompletedVersion == UserSettings.OnboardingVersion
            && Math.Abs(after.IdleFrameBudgetMs - 80.0) < 0.05,
            $"marker={after.OnboardingCompletedVersion} idle={after.IdleFrameBudgetMs:0.0}");

        // ⑥ settings.json 的审计字段（原规格 17 §4）。
        JsonNode? doc = JsonNode.Parse(System.IO.File.ReadAllText(store.Path));
        bool audit = doc is JsonObject obj
            && obj.ContainsKey("agent_id")
            && obj.ContainsKey("timestamp")
            && (string?)obj["agent_id"] == "host";
        check("ui-onboarding-state-audit-fields", audit,
            $"keys={string.Join(",", doc is JsonObject o ? o.Select(k => k.Key) : Array.Empty<string>())}");

        // ⑦ 写盘失败：内联错误条、不弹模态框（失败不弹模态是界面纪律）。
        using (var failing = new OnboardingWindow(
                   (bool complete, out string error) =>
                   {
                       error = "设置未能保存：判据注入的失败";
                       return false;
                   },
                   hasPet: true))
        {
            int formsBefore = OnboardingFormCount();
            failing.GoNext();
            int formsAfter = OnboardingFormCount();
            check("ui-onboarding-inline-error",
                failing.ErrorShown && failing.ErrorText.Length > 0,
                $"shown={failing.ErrorShown} text={failing.ErrorText}");
            check("ui-onboarding-no-modal-error", formsAfter == formsBefore,
                $"forms-before={formsBefore} forms-after={formsAfter}");
        }

        // ⑧ 帧透明区：角点 alpha=0、中心 alpha≠0（RFC-16 §3.5 的可测面）。
        FrameAlphaTransparent(check);
    }

    /// <summary>
    /// 帧透明区判据：真的取一帧，读它的角点与中心的 alpha。
    /// 走的是真的 LayeredFrameWindow + 真的内核兜底内容源（不是自己造一张位图）。
    /// </summary>
    private static void FrameAlphaTransparent(Check check)
    {
        using var window = new LayeredFrameWindow();
        window.Hide();
        window.PresentFirstFrame();
        Size size = window.FrameSize;
        (byte B, byte G, byte R, byte A)? corner = window.SampleFramePixel(2, 2);
        (byte B, byte G, byte R, byte A)? center = window.SampleFramePixel(size.Width / 2, size.Height / 2);
        check("ui-onboarding-frame-alpha-transparent",
            corner is not null && center is not null && corner.Value.A == 0 && center.Value.A != 0,
            $"size={size.Width}x{size.Height} corner-a={corner?.A.ToString() ?? "(null)"} center-a={center?.A.ToString() ?? "(null)"}");
    }

    /// <summary>当前打开的引导窗数量（判据「不弹模态框」的面）。</summary>
    private static int OnboardingFormCount()
    {
        int count = 0;
        foreach (Form form in Application.OpenForms)
        {
            if (form is OnboardingWindow)
            {
                count++;
            }
        }

        return count;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响判据结论。
        }
    }
}
