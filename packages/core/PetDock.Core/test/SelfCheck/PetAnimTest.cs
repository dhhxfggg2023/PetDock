// --animtest：docs/50:21「动画可循环」的可判定判据（t1 契约 §3.3 冻结的 C1/C2/C3）。
// 命令形态：`--animtest idle walk hover`，期望三行 + ANIM-OK + exit 0。expected clip 列表只从命令行来
// （docs/01 P-02：core 不写素材/插件名，也不读任何素材文件）。
// 三断言：C1 clip-frames = 一个周期内实际观察到的不同帧数 == 声明帧数（声明值来自可选 IClipCatalog，
//   不实现它时按「不可判定」FAIL，绝不默认通过）；C2 frame-stable = 同一 now 连调两次像素签名相同
//   （docs/11:120 逐字）；C3 loop-wrap = now+period 的签名 == now 的签名且中途出现过不同签名
//   （防「永远第 0 帧」假 PASS）。帧索引用整帧像素哈希表达：帧号是实现方私有状态，
//   像素相同才是 docs/11:120 的可观察等价物。无视图实现 → ANIM-FAIL 非零退出（M0 教训）。
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
            return Fail("surface no-view-implementation-in-roster");
        }

        Console.WriteLine($"CHECK animtest-surface PASS type={roster.ViewTypeName}");

        if (clips.Length == 0)
        {
            Console.WriteLine("CHECK animtest-cliplist FAIL no-clip-requested");
            return Fail("cliplist no-clip-requested");
        }

        // G2：素材包缺帧/坏 clip 在 Init/Attach 阶段就会抛 —— 必须结构化失败（ANIM-FAIL + 非零退出），
        // 绝不让未捕获异常逃出 Main 崩进程（旧路径 0xE0434352）。
        PetDescription description;
        RenderTarget target;
        // 自检在这里扮演宿主：插件日志要有归属（RFC-23）。插件的 id 从名册里那个视图记录取，
        // 不是从命令行、也不是猜的 —— 名册是唯一发现路径，它说的才是这个实现属于哪个插件。
        string viewPluginId = roster.Records
            .Where(r => r.Interface == typeof(IPetView))
            .Select(r => r.PluginId)
            .FirstOrDefault() ?? string.Empty;
        try
        {
            using (PluginLog.Scope(viewPluginId))
            {
                view.InitAsync(new PetViewContext("animtest", Array.Empty<string>(), BudgetFor(128, 128)))
                    .GetAwaiter().GetResult();
            }
            description = view.Describe();
            Console.WriteLine($"CHECK-DISPLAY animtest-describe {description}");
            // 画板按实现方声明的自然尺寸给：给错尺寸会让它交付与自己声明不符的帧（判据自身的错）。
            int width = Math.Max(1, (int)Math.Round(description.NaturalSize.W));
            int height = Math.Max(1, (int)Math.Round(description.NaturalSize.H));
            var bitmaps = new BitmapRegistry();
            target = new RenderTarget(width, height, 1.0, BudgetFor(width, height), bitmaps);
            view.Attach(target);
        }
        catch (Exception error)
        {
            return Fail($"init error={error.GetType().Name} message={error.Message}");
        }

        Console.WriteLine($"CHECK-DISPLAY animtest-target {target}");

        IClipCatalog? catalog = view as IClipCatalog;
        if (catalog is null)
        {
            Console.WriteLine("CHECK-DISPLAY animtest-declared declared-unavailable (implementation is not IClipCatalog)");
        }

        int failures = 0;
        var failedClips = new List<string>();
        foreach (string clip in clips)
        {
            try
            {
                failures += RunOneClip(view, catalog, clip);
            }
            catch (Exception error)
            {
                // G2：单 clip 内异常（缺帧、渲染失败）降级为该 clip 的结构化失败；其余 clip 继续判。
                Console.WriteLine($"CHECK animtest-clip-error FAIL clip={clip} error={error.GetType().Name} message={error.Message}");
                failedClips.Add(clip);
                failures++;
            }
        }

        view.Detach();
        if (failures == 0)
        {
            Console.WriteLine("ANIM-OK");
            return 0;
        }

        return Fail(failedClips.Count > 0 ? $"clips={string.Join(",", failedClips)}" : $"failures={failures}");
    }

    /// <summary>结构化失败出口：ANIM-FAIL 必须带原因，退出码非 0（G2）。</summary>
    private static int Fail(string reason)
    {
        Console.WriteLine($"ANIM-FAIL {reason}");
        return 1;
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
            // 声明值问不出来 → 判据不可判定，按 FAIL 处理并说明原因。
            Console.WriteLine($"CHECK animtest-declared-clip FAIL clip={clip}"
                + $" reason={(catalog is null ? "no-IClipCatalog" : "clip-not-declared")}");
            return 1;
        }

        double periodMs = declared.PeriodMs;
        if (periodMs <= 0 || declared.Frames <= 0)
        {
            Console.WriteLine($"CHECK animtest-period FAIL clip={clip} frames={declared.Frames}"
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
        Console.WriteLine($"CHECK-DISPLAY clip-frames clip={clip} declared={declared.Frames}"
            + $" observed-distinct={distinct} fps={PetPoint.Fmt(declared.Fps)} period={PetPoint.Fmt(periodMs)}ms");
        Check(ref failures, $"clip-frames[{clip}]", distinct == declared.Frames,
            $"declared={declared.Frames} distinct={distinct}");

        // —— C2：同一 now 必须返回同一帧（docs/11:120）——
        int probeNow = (int)Math.Round(periodMs / 3.0);
        long first = SignatureOf(view.QueryFrame(probeNow));
        long second = SignatureOf(view.QueryFrame(probeNow));
        Console.WriteLine($"CHECK-DISPLAY frame-stable clip={clip} now={probeNow} first={first:x} second={second:x}");
        Check(ref failures, $"frame-stable[{clip}]", first == second,
            $"first={first:x} second={second:x}");

        // —— C3：按 clip 的 loop 语义分流（M3 契约勘误 E2）——
        // loop 类：周期绕回（采样点用 Ceiling(periodMs)，理由见下）；once 类：到达末帧并夹住。
        // 采样点用 Ceiling(periodMs)（不是 Round）：周期常非整数（hover=333.33ms），Round 取 333 仍在周期内，
        // floor 型相位实现此刻返回末帧是正确的，判据会误读成「没有绕回」；Ceiling=334 是周期后第一个毫秒，
        // 任何合理相位映射都落回帧 0。判据自身义务：采样点落在语义边界上时不许把合法取值判成失败。
        long atZero = SignatureOf(view.QueryFrame(0));
        int periodNow = (int)Math.Ceiling(periodMs);
        long atPeriod = SignatureOf(view.QueryFrame(periodNow));
        var midway = new List<long>();
        for (int step = 1; step < 8; step++)
        {
            midway.Add(SignatureOf(view.QueryFrame((int)Math.Round(periodMs * step / 8.0))));
        }

        bool varied = midway.Any(signature => signature != atZero) || midway.Distinct().Count() > 1;
        // once：末帧夹住 = 周期后的第一毫秒仍是末帧（帧数-1 的采样点），且中途出现过不同帧。
        int tailNow = (int)Math.Round(periodMs * (declared.Frames - 1) / declared.Frames);
        long atTail = SignatureOf(view.QueryFrame(tailNow));
        bool looping = declared.Loop != ClipLoop.Once;
        bool wrapped = looping ? atZero == atPeriod : atTail == atPeriod;
        Console.WriteLine($"CHECK-DISPLAY loop-wrap clip={clip} mode={(looping ? "loop" : "once")} at-0={atZero:x}"
            + $" at-period={atPeriod:x} at-tail={atTail:x} period-now={periodNow}"
            + $" midway-distinct={midway.Distinct().Count()} wrapped={wrapped} varied={varied}");
        Check(ref failures, $"loop-wrap[{clip}]", wrapped && varied,
            $"mode={(looping ? "loop" : "once")} wrapped={wrapped} varied={varied}");

        return failures;
    }

    private static AnimationClip? FindClip(IClipCatalog? catalog, string clip) =>
        catalog?.Clips.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, clip, StringComparison.Ordinal));

    /// <summary>整帧像素的 64 位签名（FNV-1a）：像素相同才是 docs/11:120 的可观察等价物。</summary>
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
