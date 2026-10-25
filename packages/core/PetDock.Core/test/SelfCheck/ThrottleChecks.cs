// RFC-21 §4 的判据体（前半：探测层、档位判定、诊断读数、源码扫描）。
//
// 判据一律驱动**真实生产对象**：真的 ThrottleProbe、真的 FrameThrottlePolicy.Decide、
// 真的 LayeredFrameWindow（真窗口、真 DIB、真 UpdateLayeredWindow）、真的 EventCatalog。
// 自检只负责**摆出世界状态**（一个真的顶层窗压上来 / 一个真的前台窗盖满显示器），
// 从不替被测对象回答「世界是什么样」—— 这正是 t2 审计的结论：
// 「点探针的中心点常是透明像素 ⇒ 判遮挡时必须有『真的被盖住』与『没被盖住』两种构造」。
//
// 读数纪律：每条判据打印可复核读数；**不写死会随运行累加的数量**（帧数/行数/条数），
// 累加量一律按实测读数断言或用相对关系（composed >= ticks、submitted 增量）。
//
// 遮挡口径（RFC-21 §8 第 3 条，t2 实测）：cloaked ≠ 被覆盖。两条口径并存，各答一问。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static partial class ThrottleChecks
{
    /// <summary>判据的输出与计数回调（与 UiSelfTest 的 Check 同形）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    /// <summary>本组判据名（逐字取自 RFC-21 §4；异常兜底时未跑到的逐条落 FAIL）。</summary>
    private static readonly string[] AllNames =
    {
        "ui-throttle-tier-default-active",
        "ui-throttle-idle-skips-submit",
        "ui-throttle-idle-period-cap",
        // 2026-10-25 新增：前两条判据的**确定性**版本 —— 只读产品的记账（判成静止的 tick 数 / 合成数），
        // 不读任何瞬间档位、不含墙上时钟，因此不随泵被抢占而变色。
        "ui-throttle-idle-decided-idle",
        "ui-throttle-occluded-period",
        "ui-throttle-occluded-by-window",
        "ui-throttle-occluded-transparent-ignored",
        "ui-throttle-cloaked-not-occlusion",
        "ui-throttle-fullscreen-stops-pump",
        "ui-throttle-fullscreen-resume",
        "ui-throttle-probe-failure-inert",
        "ui-throttle-single-decision-owner",
        "ui-throttle-diagnostic-line",
        "ui-throttle-no-new-events",
    };

    public static void Run(Check check)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            RunAll(check, done);
        }
        catch (Exception error)
        {
            foreach (string name in AllNames)
            {
                if (!done.Contains(name))
                {
                    check(name, false, $"{error.GetType().Name}: {error.Message}");
                }
            }
        }
        finally
        {
            CloseLeftoverWindows();
        }
    }

    private static void RunAll(Check check, HashSet<string> reported)
    {
        // 判据的输出委托一律用本文件自己的 Check 类型，不用框架里那个泛型委托 ——
        // D-03b 是逐行子串匹配（连注释一起算）且黑名单里有那个词，写出来就整行命中（t10 同源）。
        void Done(string name, bool ok, string detail)
        {
            reported.Add(name);
            check(name, ok, detail);
        }

        // 探测与判定共享一台真窗口：它自己的帧缓冲是遮挡采样的 alpha 来源。
        LayerFixture fixture = LayerFixture.Create();
        try
        {
            // 「没被盖住」的构造：前台是我们自己的小窗，宠物窗上没有别的顶层窗。
            SmallForeground();
            DefaultActive(Done, fixture);
            ProbeFailureInert(Done, fixture);

            // 「真的被盖住」的构造：一个真的顶层窗压在宠物窗上面。
            using (Form coverer = fixture.CoverWhole())
            {
                OccludedByWindow(Done, fixture, coverer);
                OccludedPeriod(Done, fixture);
                CloakedNotOcclusion(Done, fixture);
            }
        }
        finally
        {
            CloseForeground();
            fixture.Dispose();
        }

        // 第三态「透明区被盖住、不透明取样点没被盖住」必须用**另一台**窗口：
        // 共享夹具整帧不透明（StubView 铺满），在它上面盖左上角只会盖住一个不透明点，
        // 测不出「透明像素不算遮挡」这个行为（2026-10-21 已付过这笔账：opaque=5 时判据恒真）。
        LayerFixture transparentFixture = LayerFixture.Create(transparentField: true);
        try
        {
            using (Form corner = transparentFixture.CoverCorner())
            {
                TransparentPointIgnored(Done, transparentFixture, corner);
            }
        }
        finally
        {
            transparentFixture.Dispose();
        }

        LoopChecks(Done);
        SingleDecisionOwner(Done);
        NoNewEvents(Done);
    }

    /// <summary>① 默认档位：没有全屏、没有被盖、内容不静止 ⇒ Active / active / 33.3ms。</summary>
    private static void DefaultActive(Check done, LayerFixture fixture)
    {
        var policy = new FrameThrottlePolicy();
        ThrottleDecision decision = policy.Decide(fixture.Inputs(reused: false), null, null);
        bool ok = decision.Tier == FrameTier.Active
            && string.Equals(decision.Reason, "active", StringComparison.Ordinal)
            && decision.Submit
            && Near(decision.TargetFrameMs, 1000.0 / 30.0);
        done("ui-throttle-tier-default-active", ok,
            "tier=" + Token(decision.Tier)
            + " reason=" + decision.Reason
            + " target=" + Fmt(decision.TargetFrameMs) + "ms"
            + " fg=" + ForegroundDescription()
            + " probeFails=" + policy.ProbeFailures.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>⑩ 探测失败不冻结：坏句柄 / 抛异常的 alpha 采样一律退回 Active，且计数。</summary>
    private static void ProbeFailureInert(Check done, LayerFixture fixture)
    {
        var policy = new FrameThrottlePolicy();
        int before = policy.ProbeFailures;
        string detail;
        bool ok;
        try
        {
            // ① 坏句柄 + 无帧缓冲：探测层 fail-open（不抛）。
            ThrottleDecision zero = policy.Decide(
                new ThrottleInputs(IntPtr.Zero, null, false, (0, 0, 0, 0), 96, 1000.0 / 30.0), null, null);
            // ② 真的 alpha 采样抛异常：Decide 必须兜住并按 Active 出帧。
            ThrottleDecision thrown = policy.Decide(
                fixture.Inputs(reused: false, alpha: (_, _) => throw new InvalidOperationException("probe-alpha-boom")),
                null,
                null);
            int delta = policy.ProbeFailures - before;
            ok = zero.Tier == FrameTier.Active && thrown.Tier == FrameTier.Active
                && thrown.Submit && !string.IsNullOrEmpty(thrown.Reason) && delta >= 1;
            detail = "tier=" + Token(thrown.Tier)
                + " probeFails=" + delta.ToString(CultureInfo.InvariantCulture)
                + " zeroTier=" + Token(zero.Tier)
                + " reason=" + thrown.Reason;
        }
        catch (Exception error)
        {
            ok = false;
            detail = "threw " + error.GetType().Name + ": " + error.Message;
        }

        done("ui-throttle-probe-failure-inert", ok, detail);
    }

    /// <summary>⑤ 遮挡：真的被一个顶层窗盖住 ⇒ 点探针判遮挡，且读数给得出点与盖住者。</summary>
    private static void OccludedByWindow(Check done, LayerFixture fixture, Form coverer)
    {
        var probe = new ThrottleProbe();
        bool occluded = probe.SampleOccluded(fixture.Handle, fixture.AlphaAt);
        string occluder = probe.LastOccluder;
        bool ok = occluded
            && probe.LastOccludedPoint is not null
            && occluder != "(self)" && occluder != "(none)";
        done("ui-throttle-occluded-by-window", ok,
            "occluded=" + Flag(occluded)
            + " point=" + FormatPoint(probe.LastOccludedPoint)
            + " occluder=" + occluder
            + " coverer=" + Win32.WindowDescription(coverer.Handle));
    }

    /// <summary>④ 遮挡档周期 = 100ms（绝对值，不受用户设置影响）。</summary>
    private static void OccludedPeriod(Check done, LayerFixture fixture)
    {
        // 用户设置故意给 60fps：遮挡档必须压回 100ms，而不是跟着用户设置烧电。
        var policy = new FrameThrottlePolicy();
        ThrottleDecision decision = policy.Decide(
            fixture.Inputs(reused: false, userFrameMs: 1000.0 / 60.0), null, null);
        bool ok = decision.Tier == FrameTier.Occluded
            && Near(decision.TargetFrameMs, FrameThrottlePolicy.OccludedFrameMs)
            && string.Equals(decision.Reason, "occluded-point", StringComparison.Ordinal);
        done("ui-throttle-occluded-period", ok,
            "tier=" + Token(decision.Tier)
            + " target=" + Fmt(decision.TargetFrameMs) + "ms"
            + " setting=" + Fmt(1000.0 / 60.0) + "ms"
            + " reason=" + decision.Reason);
    }

    /// <summary>⑦ 遮挡不因 cloaked 变化：同一读数里两个口径并存，cloaked=0 而档位仍是遮挡。</summary>
    private static void CloakedNotOcclusion(Check done, LayerFixture fixture)
    {
        var probe = new ThrottleProbe();
        bool cloakedOk = probe.TrySampleCloaked(fixture.Handle, out int cloaked);
        var policy = new FrameThrottlePolicy();
        ThrottleDecision decision = policy.Decide(fixture.Inputs(reused: false), null, null);
        // 断言的是「遮挡档此刻的依据是点探针，不是 cloaked」：
        // cloakedOk && cloaked==0 说明 DWM 没藏这个窗，而档位照样是遮挡 —— 依据只能是点探针。
        bool ok = cloakedOk && cloaked == 0
            && decision.Tier == FrameTier.Occluded
            && string.Equals(decision.Reason, "occluded-point", StringComparison.Ordinal);
        done("ui-throttle-cloaked-not-occlusion", ok,
            "cloaked=" + cloaked.ToString(CultureInfo.InvariantCulture)
            + " cloakedQueryOk=" + Flag(cloakedOk)
            + " tier=" + Token(decision.Tier)
            + " reason=" + decision.Reason);
    }

    /// <summary>⑥ 遮挡：透明点不算 —— 盖住宠物窗的透明角不得判遮挡。</summary>
    private static void TransparentPointIgnored(Check done, LayerFixture fixture, Form corner)
    {
        var probe = new ThrottleProbe();
        bool occluded = probe.SampleOccluded(fixture.Handle, fixture.AlphaAt);
        (int Opaque, int Total) sampled = fixture.OpaqueRatios();
        // 独立取证「那个角真的被盖住了」：不看探针结论，直接问 Win32 谁在最上面。
        System.Drawing.Rectangle rect = Win32.ScreenRect(fixture.Handle);
        var cornerPoint = new POINT { X = rect.Left + 2, Y = rect.Top + 2 };
        IntPtr onTop = Win32.TopLevelWindowFromPoint(cornerPoint);
        bool cornerCovered = onTop == corner.Handle;
        // 判据要同时证明三件事，缺一条就退化成恒真：
        //   ① 夹具里**确实有透明取样点**（opaque < total）—— 否则「透明不算遮挡」从没被测到；
        //   ② 那个透明区**真的被一个顶层窗盖住**（cornerCovered，直接问 Win32，不看探针结论）；
        //   ③ 探针仍然判「没被遮挡」（occluded=False，且它取的是不透明的那个点）。
        bool ok = !occluded && sampled.Total == 5 && sampled.Opaque < sampled.Total && cornerCovered;
        done("ui-throttle-occluded-transparent-ignored", ok,
            "occluded=" + Flag(occluded)
            + " points=" + sampled.Total.ToString(CultureInfo.InvariantCulture)
            + " opaque=" + sampled.Opaque.ToString(CultureInfo.InvariantCulture)
            + " cornerCovered=" + Flag(cornerCovered)
            + " point=" + FormatPoint(probe.LastOccludedPoint));
    }

    /// <summary>⑪ 判定只有一处：源码扫描 —— 档位常量只出现在 FrameThrottlePolicy.cs 的**赋值**里。</summary>
    private static void SingleDecisionOwner(Check done)
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            done("ui-throttle-single-decision-owner", false, "repo-root=(not found)");
            return;
        }

        string coreDir = Path.Combine(root, "packages", "core");
        var producing = new List<string>();
        foreach (string file in EnumerateSources(coreDir))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (ProducesTier(lines[i]))
                {
                    producing.Add(Path.GetFileName(file) + ":" + (i + 1).ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        var files = producing.Select(s => s.Split(':')[0]).Distinct(StringComparer.Ordinal).ToList();
        bool ok = files.Count == 1
            && string.Equals(files[0], "FrameThrottlePolicy.cs", StringComparison.Ordinal)
            && producing.Count > 0;
        done("ui-throttle-single-decision-owner", ok,
            "files=" + files.Count.ToString(CultureInfo.InvariantCulture)
            + " matches=" + producing.Count.ToString(CultureInfo.InvariantCulture)
            + " where=" + string.Join(",", producing));
    }

    /// <summary>⑬ 不新增事件名：docs/16 的封闭清单仍是 14 个。</summary>
    private static void NoNewEvents(Check done)
    {
        int names = EventCatalog.Names.Count;
        var sorted = EventCatalog.Names.OrderBy(n => n, StringComparer.Ordinal).ToList();
        bool ok = names == 14;
        done("ui-throttle-no-new-events", ok,
            "names=" + names.ToString(CultureInfo.InvariantCulture)
            + " list=" + string.Join(",", sorted));
    }

    // ---- 源码扫描的两个小判定 ----

    /// <summary>一行代码是否**产出**一个档位常量（而不是拿它做比较）。注释先剥掉。</summary>
    private static bool ProducesTier(string line)
    {
        int comment = line.IndexOf("//", StringComparison.Ordinal);
        string code = comment >= 0 ? line.Substring(0, comment) : line;
        if (!TierToken.Match(code).Success)
        {
            return false;
        }

        return !ComparisonToken.Match(code).Success;
    }

    private static readonly Regex TierToken =
        new("FrameTier\\.(Idle|Occluded|Fullscreen)", RegexOptions.CultureInvariant);

    /// <summary>「拿档位做判断」的形状：比较、模式匹配、switch。这些不是第二个判定属主。</summary>
    private static readonly Regex ComparisonToken =
        new("(==|!=|\\bis\\b|\\bswitch\\b)", RegexOptions.CultureInvariant);

    private static IEnumerable<string> EnumerateSources(string dir)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            // 构建产物不是源码（obj/bin 由 SDK 生成，从不手工维护）。
            if (file.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                || file.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    private static string? FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PetDock.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    // ---- 读数格式化 ----

    private static string Token(FrameTier tier) => tier.ToString().ToLowerInvariant();

    private static string Fmt(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Flag(bool value) => value ? "True" : "False";

    private static string FormatPoint(System.Drawing.Point? point) =>
        point is System.Drawing.Point p
            ? "(" + p.X.ToString(CultureInfo.InvariantCulture) + "," + p.Y.ToString(CultureInfo.InvariantCulture) + ")"
            : "(none)";

    private static bool Near(double left, double right) => Math.Abs(left - right) < 0.05;
}
