// --animtest：docs/50:21「动画可循环」的可判定判据（t1 契约 §3.3 冻结的 C1/C2/C3）。
//
// 命令形态（契约 §3.3 与 §5 命令 5）：`--animtest idle walk hover`，期望三行 + ANIM-OK + exit 0。
// **expected clip 列表只从命令行来** —— core 源码里不写任何素材/插件名（docs/01 P-02 第 4 条），
// 也不读任何素材文件（P-02 第 2 条，素材清单的文件名含被禁子串）。
//
// 三断言（逐字照契约 §3.3 的表格）：
//   C1 clip-frames：一个周期内**实际观察到的不同帧数**必须等于实现方声明的帧数。
//      声明值来自 IClipCatalog（可选接口）；不实现它时 C1 无法判定，命令按 FAIL 处理
//      （判据要么可判定、要么如实报不可判定 —— 不许把「问不出来」当成通过）。
//   C2 frame-stable：同一 now 连调 QueryFrame 两次，**像素签名**必须相同（docs/11:120 逐字）。
//   C3 loop-wrap：`queryFrame(now + period)` 的签名 == `queryFrame(now)` 的签名，
//      且中途至少出现过不同的签名 —— 只断言首尾相等无法区分「真的在循环」与「永远第 0 帧」，
//      这条直接防 M1 t5 把「夹具空操作」当成 PASS 那一类错误。
//
// 「帧索引」在本文件里一律用**整帧像素的哈希**表达，而不是实现方的私有帧号：
// 帧号是内部状态，而 docs/11:120 承诺的是「同一 now 返回同一帧（或语义上等价的帧）」——
// 像素相同才是可观察、且实现方无从作弊的判据。
//
// 没有任何视图实现 → ANIM-FAIL（「没装上」必须让命令非 0 退出，M0 教训的直接要求）。
using System;
using System.Collections.Generic;
using System.Linq;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

internal static class PetAnimTest
{
    /// <summary>判据入口：返回失败数（0 = 全过）。</summary>
    public static int Run(string[] clips)
    {
        PetSurfaceRoster roster = PetSurfaceRoster.Discover(PluginHost.ProbePluginsRoot());
        Console.WriteLine($"CHECK-DISPLAY animtest-roster {roster.Describe()}");

        IPetView? view = roster.View;
        if (view is null)
        {
            Console.WriteLine("CHECK animtest-surface FAIL no-view-implementation-in-roster");
            Console.WriteLine("ANIM-FAIL surface");
            return 1;
        }

        Console.WriteLine($"CHECK animtest-surface PASS type={roster.ViewTypeName}");

        if (clips.Length == 0)
        {
            Console.WriteLine("CHECK animtest-cliplist FAIL no-clip-requested");
            Console.WriteLine("ANIM-FAIL cliplist");
            return 1;
        }

        view.InitAsync(new PetViewContext("animtest", Array.Empty<string>(), BudgetFor(128, 128)))
            .GetAwaiter().GetResult();
        PetDescription description = view.Describe();
        Console.WriteLine($"CHECK-DISPLAY animtest-describe {description}");

        // 画板按实现方声明的自然尺寸给（不是拍一个 8x8 —— 实现方按画板尺寸出帧，
        // 给错尺寸会让它交付与自己声明不符的帧，那是判据自身的错，不是实现方的错）。
        int width = Math.Max(1, (int)Math.Round(description.NaturalSize.W));
        int height = Math.Max(1, (int)Math.Round(description.NaturalSize.H));
        ResourceBudget budget = BudgetFor(width, height);
        var bitmaps = new BitmapRegistry();
        var target = new RenderTarget(width, height, 1.0, budget, bitmaps);
        view.Attach(target);
        Console.WriteLine($"CHECK-DISPLAY animtest-target {target}");

        IClipCatalog? catalog = view as IClipCatalog;
        if (catalog is null)
        {
            Console.WriteLine("CHECK-DISPLAY animtest-declared declared-unavailable (implementation is not IClipCatalog)");
        }

        int failures = 0;
        foreach (string clip in clips)
        {
            failures += RunOneClip(view, catalog, clip);
        }

        view.Detach();
        Console.WriteLine(failures == 0 ? "ANIM-OK" : "ANIM-FAIL");
        return failures == 0 ? 0 : 1;
    }

    private static ResourceBudget BudgetFor(int width, int height) =>
        new(FrameBudgetGuard.FrameBudgetMs, FrameBudgetGuard.IdleFrameBudgetMs, width * height);

    private static int RunOneClip(IPetView view, IClipCatalog? catalog, string clip)
    {
        int failures = 0;

        if (!view.SetAnimation(clip, restart: true))
        {
            Console.WriteLine($"CHECK animtest-set-animation FAIL clip={clip} not-accepted");
            return 1;
        }

        AnimationClip? declared = FindClip(catalog, clip);
        if (declared is null)
        {
            // 声明值问不出来 → 判据不可判定。按 FAIL 处理并说明原因，绝不「默认通过」。
            Console.WriteLine(
                $"CHECK animtest-declared-clip FAIL clip={clip}"
                + $" reason={(catalog is null ? "no-IClipCatalog" : "clip-not-declared")}");
            return 1;
        }

        double periodMs = declared.PeriodMs;
        if (periodMs <= 0 || declared.Frames <= 0)
        {
            Console.WriteLine(
                $"CHECK animtest-period FAIL clip={clip} frames={declared.Frames}"
                + $" fps={PetPoint.Fmt(declared.Fps)}");
            return 1;
        }

        // —— C1：一个周期内实际观察到的不同帧数 == 声明的帧数 ——
        var observed = new List<long>();
        int steps = Math.Max(2, (int)Math.Round(periodMs / 4.0));
        for (int step = 0; step < steps; step++)
        {
            int now = (int)Math.Round(periodMs * step / steps);
            observed.Add(SignatureOf(view.QueryFrame(now)));
        }

        int distinct = observed.Distinct().Count();
        Console.WriteLine(
            $"CHECK-DISPLAY clip-frames clip={clip} declared={declared.Frames} observed-distinct={distinct}"
            + $" fps={PetPoint.Fmt(declared.Fps)} period={PetPoint.Fmt(periodMs)}ms");
        Check(ref failures, $"clip-frames[{clip}]", distinct == declared.Frames,
            $"declared={declared.Frames} distinct={distinct}");

        // —— C2：同一 now 必须返回同一帧（docs/11:120）——
        int probeNow = (int)Math.Round(periodMs / 3.0);
        long first = SignatureOf(view.QueryFrame(probeNow));
        long second = SignatureOf(view.QueryFrame(probeNow));
        Console.WriteLine($"CHECK-DISPLAY frame-stable clip={clip} now={probeNow} first={first:x} second={second:x}");
        Check(ref failures, $"frame-stable[{clip}]", first == second,
            $"first={first:x} second={second:x}");

        // —— C3：周期绕回，且中途至少出现过不同帧 ——
        // 采样点用 **Ceiling(periodMs)**，不是 Round：周期常常不是整数（hover = 2*1000/6 = 333.33ms），
        // Round 会取到 333 —— 那还在周期**之内**，实现按 floor(now*fps/1000) 算得帧 1 是**正确的**，
        // 而判据会把它读成「没有绕回」。Ceiling 取到 334 = 周期结束后的第一个毫秒，
        // 此时任何合理的相位映射（Round 或 Floor）都落在帧 0。
        // 这是判据自身的义务：采样点落在语义边界上时，不许把实现的合法取值判成失败。
        long atZero = SignatureOf(view.QueryFrame(0));
        int periodNow = (int)Math.Ceiling(periodMs);
        long atPeriod = SignatureOf(view.QueryFrame(periodNow));
        var midway = new List<long>();
        for (int step = 1; step < 8; step++)
        {
            midway.Add(SignatureOf(view.QueryFrame((int)Math.Round(periodMs * step / 8.0))));
        }

        bool wrapped = atZero == atPeriod;
        bool varied = midway.Any(signature => signature != atZero) || midway.Distinct().Count() > 1;
        Console.WriteLine(
            $"CHECK-DISPLAY loop-wrap clip={clip} at-0={atZero:x} at-period={atPeriod:x}"
            + $" period-now={periodNow} midway-distinct={midway.Distinct().Count()}"
            + $" wrapped={wrapped} varied={varied}");
        Check(ref failures, $"loop-wrap[{clip}]", wrapped && varied,
            $"wrapped={wrapped} varied={varied}");

        return failures;
    }

    private static AnimationClip? FindClip(IClipCatalog? catalog, string clip)
    {
        if (catalog is null)
        {
            return null;
        }

        foreach (AnimationClip candidate in catalog.Clips)
        {
            if (string.Equals(candidate.Id, clip, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 整帧像素的 64 位签名（FNV-1a）。直接用像素而不是实现方的帧号：
    /// 帧号是实现方的私有状态，读不到也没有约束力；像素相同才是 docs/11:120 的可观察等价物。
    /// </summary>
    private static long SignatureOf(Frame frame)
    {
        System.Drawing.Bitmap bitmap = frame.BitmapHandle.Bitmap;
        System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            long hash = unchecked((long)1469598103934665603UL);
            int bytes = data.Stride * bitmap.Height;
            unsafe
            {
                byte* p = (byte*)data.Scan0.ToPointer();
                for (int i = 0; i < bytes; i++)
                {
                    hash = unchecked((hash ^ p[i]) * 1099511628211L);
                }
            }

            return hash;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void Check(ref int failures, string name, bool ok, string detail)
    {
        Console.WriteLine($"CHECK-DISPLAY animtest {name} {(ok ? "PASS" : "FAIL")} {detail}");
        if (!ok)
        {
            failures++;
        }
    }
}
