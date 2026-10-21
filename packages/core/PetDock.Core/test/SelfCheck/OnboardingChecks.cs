// 首次运行引导的判据（RFC-16 §4）。
//
// 判据走**生产对象**：真的 OnboardingWindow（真的控件树）、真的 UserSettingsStore
// （真的落盘）、真的 PetDataStore（真的清单）、真的 PetImportService（真的复制与换源）。
// 唯一被替换的是「示例素材在哪儿」—— 判据自己造一个最小的示例包目录，
// 因为发行物里有没有示例包是**发布装配**的门禁（tools/release.mjs 的出口检查），
// 不是引导逻辑的判据。
//
// 覆盖的判据（RFC-16 §4 表里可机器判定的那几条）：
//   ui-onboarding-runs-once                  一次性标记写进 settings.json
//   ui-onboarding-not-repeated               标记存在时宿主不再想跑引导
//   ui-onboarding-marker-survives-settings-write  设置页写盘不抹掉标记
//   ui-onboarding-state-audit-fields         settings.json 含 agent_id + timestamp
//   ui-onboarding-language-persisted         选 en-US 后 language == en-US
//   ui-onboarding-language-is-choice-only    选 en-US 后界面文案仍是中文
//   ui-onboarding-sample-disclaimer          第 2 步文本含「占位素材，发行版替换为 CC0/自绘」
//   ui-onboarding-sample-placed              放置成功
//   ui-onboarding-sample-copied              导入后目标目录文件集合与源一致
//   ui-onboarding-sample-registered          清单有条目且指向拷贝目录
//   ui-onboarding-sample-replaced-pet        换源后内容源的实例是新实例
//   ui-onboarding-asset-root-reaches-view    素材根传到了视图实现的 init 上下文
//   ui-onboarding-inline-error               失败时内联错误条可见且有文字
//   ui-onboarding-no-modal-error             失败路径不弹模态框
//   ui-onboarding-old-pet-kept               失败时旧宠物原样不动
//   ui-onboarding-through-caption            第 3 步文本含穿透说明
//   ui-onboarding-animation-running          示意动画计时器已启动且帧计数推进
//   ui-onboarding-frame-alpha-transparent    帧角点 alpha=0、中心 alpha≠0
using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>自检用的示例包目录名（不是任何真实格式名 —— core 不该知道格式）。</summary>
    private const string SampleDirName = "sample-under-test";

    /// <summary>判据名清单（异常兜底时逐条落 FAIL —— 一条只打印不计数等于没有门禁）。</summary>
    private static readonly string[] AllNames =
    {
        "ui-onboarding-runs-once",
        "ui-onboarding-not-repeated",
        "ui-onboarding-marker-survives-settings-write",
        "ui-onboarding-state-audit-fields",
        "ui-onboarding-language-persisted",
        "ui-onboarding-language-is-choice-only",
        "ui-onboarding-sample-disclaimer",
        "ui-onboarding-sample-placed",
        "ui-onboarding-sample-copied",
        "ui-onboarding-sample-registered",
        "ui-onboarding-sample-replaced-pet",
        "ui-onboarding-asset-root-reaches-view",
        "ui-onboarding-inline-error",
        "ui-onboarding-no-modal-error",
        "ui-onboarding-old-pet-kept",
        "ui-onboarding-through-caption",
        "ui-onboarding-animation-running",
        "ui-onboarding-frame-alpha-transparent",
    };

    public static void Run(Check check)
    {
        string root = Path.Combine(Path.GetTempPath(), "petdock-onboarding-check-" + Guid.NewGuid().ToString("N"));
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
        string sample = Path.Combine(root, "bundled", SampleDirName);
        MakeSamplePack(sample);

        var store = new UserSettingsStore(root);
        var dataStore = new PetDataStore(root);
        var roster = new PetSurfaceRoster();
        roster.Add("petdock.stub.view", typeof(StubView).FullName!, typeof(IPetView), new StubView());

        var frames = new SwappableFrameSource(SolidFrameContent.KernelFallback(), roster);
        PetInstance? current = null;
        var import = new PetImportService(dataStore, frames, roster, () => current, next => current = next);

        // 写设置要「读出 → 改一个字段 → 整份写回」（RFC-16 §3.2）。
        bool WriteSettings(string? language, bool complete, out string error)
        {
            error = string.Empty;
            try
            {
                UserSettings previous = store.Load();
                store.Save(previous with
                {
                    Language = language ?? previous.Language,
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

        // 放置示例宠物：走真的导入服务（复制 → 登记 → 换源 → 释放旧实例）。
        string? PlaceSample()
        {
            ImportResult result = import.Import(sample);
            return result.Ok ? null : result.Message;
        }

        using (var window = new OnboardingWindow(PlaceSample, WriteSettings, hasPet: true))
        {
            // ① 第 1 步：选语言 → 点下一步。
            window.SelectLanguage("en-US");
            string beforeText = window.NextButtonText;
            window.GoNext();
            check("ui-onboarding-language-persisted",
                string.Equals(store.Load().Language, "en-US", StringComparison.Ordinal),
                $"language={store.Load().Language}");

            // 「只持久化选择」：界面文案不随选择变化（RFC-16 §3.3 第 1 步）。
            check("ui-onboarding-language-is-choice-only",
                string.Equals(beforeText, window.NextButtonText, StringComparison.Ordinal)
                && window.NextButtonText.Contains("下一步", StringComparison.Ordinal),
                $"before={beforeText} after={window.NextButtonText}");

            // ② 第 2 步：标注原样出现（读的是窗上的 Label 文本，不是常量）。
            string sampleText = window.CollectStepText(1);
            check("ui-onboarding-sample-disclaimer",
                sampleText.Contains(OnboardingWindow.SampleDisclaimerText, StringComparison.Ordinal),
                $"text={sampleText.Replace('\n', '|')}");

            // 放置成功。
            window.TryPlaceSample();
            check("ui-onboarding-sample-placed", window.SamplePlaced,
                $"placed={window.SamplePlaced} status={window.SampleStatusText}");

            // 拷贝目录：文件集合与源一致。
            string staged = Path.Combine(dataStore.PacksDir, SampleDirName);
            check("ui-onboarding-sample-copied",
                Directory.Exists(staged) && SameFileSet(sample, staged),
                $"staged={staged} exists={Directory.Exists(staged)}");

            // 清单：条目在，且指向拷贝目录。
            PetDataStore.ActivePack? active = dataStore.ReadActive();
            check("ui-onboarding-sample-registered",
                active is not null && active.Path == staged,
                $"active={active?.InstanceId ?? "(none)"} path={active?.Path ?? "(none)"} staged={staged}");

            // 换源：内容源是新实例。
            PetFrameSource? live = frames.Current as PetFrameSource;
            check("ui-onboarding-sample-replaced-pet",
                live is not null && current is not null && ReferenceEquals(live.Instance, current),
                $"live={live?.Instance.Id ?? "(none)"} current={current?.Id ?? "(none)"}");

            // 素材根到了视图实现的 init 上下文。
            bool rootsReach = StubView.ReceivedContexts.Any(ctx =>
                ctx.AssetRoots.Any(rootPath => string.Equals(rootPath, staged, StringComparison.Ordinal)));
            check("ui-onboarding-asset-root-reaches-view", rootsReach,
                $"contexts={StubView.ReceivedContexts.Count}");

            // ③ 第 3 步：穿透说明原样出现。
            window.GoNext();
            string throughText = window.CollectStepText(2);
            check("ui-onboarding-through-caption",
                throughText.Contains("透明区域可以点到下面的东西", StringComparison.Ordinal)
                && throughText.Contains("这是正常的", StringComparison.Ordinal),
                $"text={throughText.Replace('\n', '|')}");

            // 示意动画：计时器已启动且帧计数在推进（读数要在关窗之前取）。
            int framesBefore = window.AnimationFrameCount;
            PumpMessages(200);
            bool running = window.AnimationRunning;
            int framesAfter = window.AnimationFrameCount;
            check("ui-onboarding-animation-running",
                running && framesAfter > framesBefore,
                $"running={running} frames={framesAfter} before={framesBefore}");

            // ④ 走完 ⇒ 一次性标记写进 settings.json。
            window.GoNext();
            check("ui-onboarding-runs-once",
                window.MarkerWritten && store.Load().OnboardingCompletedVersion == UserSettings.OnboardingVersion,
                $"written={window.MarkerWritten} marker={store.Load().OnboardingCompletedVersion}");
        }

        // ⑤ 标记存在时宿主不再想跑引导（真的 UiHost 判定面）。
        using (var window = new LayeredFrameWindow())
        {
            window.Hide();
            using var host = new UiHost(window, null, null, frames, null, store);
            check("ui-onboarding-not-repeated", !host.NeedsOnboarding(),
                $"needs={host.NeedsOnboarding()} marker={store.Load().OnboardingCompletedVersion}");
        }

        // ⑥ 设置页写盘不抹掉标记（RFC-16 §3.2 的核心风险）。
        UserSettings before = store.Load();
        store.Save(before with { IdleFrameBudgetMs = 80.0 });
        UserSettings after = store.Load();
        check("ui-onboarding-marker-survives-settings-write",
            after.OnboardingCompletedVersion == UserSettings.OnboardingVersion
            && Math.Abs(after.IdleFrameBudgetMs - 80.0) < 0.05,
            $"marker={after.OnboardingCompletedVersion} idle={after.IdleFrameBudgetMs:0.0}");

        // ⑦ settings.json 的审计字段（docs/17 §4）。
        JsonNode? doc = JsonNode.Parse(File.ReadAllText(store.Path));
        bool audit = doc is JsonObject obj
            && obj.ContainsKey("agent_id")
            && obj.ContainsKey("timestamp")
            && (string?)obj["agent_id"] == "host";
        check("ui-onboarding-state-audit-fields", audit,
            $"keys={string.Join(",", doc is JsonObject o ? o.Select(k => k.Key) : Array.Empty<string>())}");

        // ⑧ 失败路径：内联错误条、不弹模态框、旧宠物原样不动。
        PetInstance? petBefore = current;
        using (var failing = new OnboardingWindow(
                   () => "放置示例宠物失败：示例目录不存在",
                   WriteSettings,
                   hasPet: true))
        {
            int formsBefore = OnboardingFormCount();
            failing.TryPlaceSample();
            int formsAfter = OnboardingFormCount();
            check("ui-onboarding-inline-error",
                failing.ErrorShown && failing.ErrorText.Length > 0,
                $"shown={failing.ErrorShown} text={failing.ErrorText}");

            check("ui-onboarding-no-modal-error", formsAfter == formsBefore,
                $"forms-before={formsBefore} forms-after={formsAfter}");
        }

        check("ui-onboarding-old-pet-kept",
            current is not null && ReferenceEquals(current, petBefore),
            $"current={current?.Id ?? "(none)"}");

        // ⑨ 帧透明区：角点 alpha=0、中心 alpha≠0（RFC-16 §3.5 的可测面）。
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

    /// <summary>造一个最小的示例包目录（core 只关心「这是一个目录」，不解析内容）。</summary>
    private static void MakeSamplePack(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "img"));
        Directory.CreateDirectory(Path.Combine(dir, "anim"));
        File.WriteAllText(Path.Combine(dir, "petdock-sample.json"), "{\"schemaVersion\":1}");
        File.WriteAllText(Path.Combine(dir, "img", "a.png"), "frame-a");
        File.WriteAllText(Path.Combine(dir, "anim", "b.json"), "clip-b");
    }

    /// <summary>源与拷贝的文件集合是否一致（相对路径 + 内容）。</summary>
    private static bool SameFileSet(string source, string staged)
    {
        List<string> a = ListFiles(source);
        List<string> b = ListFiles(staged);
        if (!a.SequenceEqual(b, StringComparer.Ordinal))
        {
            return false;
        }

        foreach (string rel in a)
        {
            if (!string.Equals(
                    File.ReadAllText(Path.Combine(source, rel)),
                    File.ReadAllText(Path.Combine(staged, rel)),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> ListFiles(string root)
    {
        var files = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        files.Sort(StringComparer.Ordinal);
        return files;
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

    /// <summary>泵一小会儿消息（让 WinForms 计时器推进）。</summary>
    private static void PumpMessages(int millis)
    {
        System.DateTime until = System.DateTime.UtcNow.AddMilliseconds(millis);
        while (System.DateTime.UtcNow < until)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(10);
        }
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
            // 临时目录删不掉不影响判据结论。
        }
    }
}
