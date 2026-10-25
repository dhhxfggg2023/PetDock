// 静止档判据的确定性探针（`--throttle-probe [窗口数]`；先例是 tests/m7 的 `--timing-probe`）。
//
// 为什么需要它：ui-throttle-idle-skips-submit 与 -period-cap 在**一次** --uitest 里各只量一个
// 600ms 窗，一次进程运行就一个样本 —— 想说「复现率 k/N」就必须能在同一份二进制上把同一个窗
// 重复 N 次。本探针复用**同一条真帧循环**（一台真分层窗 + 一个真 ResidentFrameLoop），逐窗读数，
// 每窗一行可复核读数，末行给 k/N。
//
// 读法与两条判据逐字一致（逐字段各读一次，档位与周期分两次读）：那正是被测的形状。
// 探针不判定门禁结论，只把「这一窗到底看见了什么」量出来 —— 结论仍由 --uitest 的判据下。
using System;
using System.Globalization;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static partial class ThrottleChecks
{
    /// <summary>`--throttle-probe [窗口数]` 的入口。args 从窗口数那一位开始（缺省 20）。</summary>
    internal static int Probe(string[] args)
    {
        int windows = 20;
        if (args.Length > 0
            && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            windows = Math.Clamp(parsed, 1, 200);
        }

        // 用户设置故意给 60fps：静止档的 33.3ms 上限只有这样才不是恒等式（RFC-21 §8 第 1 条）。
        const double userFrameMs = 1000.0 / 60.0;

        LayerFixture fixture = LayerFixture.Create(clipFrames: 1);
        try
        {
            // 起泵之前先把「前台不是全屏」这一世界立住（理由同 LoopChecks）。
            string? world = EnsureNotFullscreenWorld(fixture.Handle);
            if (world is not null)
            {
                Console.WriteLine("THROTTLE-PROBE-WORLD-FAIL " + world);
                return 1;
            }

            using var loop = new ResidentFrameLoop(fixture.Window);
            loop.SetTargetFrameMs(userFrameMs);
            loop.Start();
            long born = loop.Ticks;
            WaitFor(ConvergeMs, () => loop.Ticks > born);

            // 世界与产品的判定必须**当场对得上**：对不上时先报世界，不要拿一串 ticks=0 当读数。
            ThrottleReading first = loop.Reading;
            if (first.Tier != FrameTier.Idle && first.Tier != FrameTier.Active)
            {
                Console.WriteLine("PROBE-WORLD-DISAGREE tier=" + Token(first.Tier)
                    + " worldAgain=" + (EnsureNotFullscreenWorld(fixture.Handle) ?? "not-fullscreen"));
            }

            int skipsOk = 0;
            int capOk = 0;
            int composedBehind = 0;
            for (int i = 1; i <= windows; i++)
            {
                long ticks0 = loop.Ticks;
                long composed0 = loop.Composed;
                long submitted0 = loop.Submitted;

                Spin(SpinMs);

                long ticks = loop.Ticks - ticks0;
                long composed = loop.Composed - composed0;
                long submitted = loop.Submitted - submitted0;
                FrameTier tier = loop.Tier;
                double target = loop.TargetFrameMs;

                bool skips = tier == FrameTier.Idle
                    && ticks >= 10
                    && submitted <= 4
                    && composed >= ticks;
                bool cap = tier == FrameTier.Idle
                    && Near(target, FrameThrottlePolicy.IdleCapMs)
                    && target > userFrameMs;
                if (skips)
                {
                    skipsOk++;
                }

                if (cap)
                {
                    capOk++;
                }

                // 合成滞后（composed = ticks - 1）单列一列：它是「两次独立读」这一形状的直接证据。
                bool lag = composed < ticks;
                if (lag)
                {
                    composedBehind++;
                }

                Console.WriteLine("PROBE idle n=" + i.ToString("00", CultureInfo.InvariantCulture)
                    + " skips=" + (skips ? "PASS" : "FAIL")
                    + " cap=" + (cap ? "PASS" : "FAIL")
                    + " tier=" + Token(tier)
                    + " ticks=" + ticks.ToString(CultureInfo.InvariantCulture)
                    + " composed=" + composed.ToString(CultureInfo.InvariantCulture)
                    + " submitted=" + submitted.ToString(CultureInfo.InvariantCulture)
                    + " target=" + Fmt(target) + "ms"
                    + " composedBehind=" + Flag(lag));
            }

            Console.WriteLine("STAT throttle-probe windows=" + windows.ToString(CultureInfo.InvariantCulture)
                + " skips=" + skipsOk.ToString(CultureInfo.InvariantCulture)
                + " cap=" + capOk.ToString(CultureInfo.InvariantCulture)
                + " composedBehind=" + composedBehind.ToString(CultureInfo.InvariantCulture));
            bool allOk = skipsOk == windows && capOk == windows;
            Console.WriteLine(allOk
                ? "THROTTLE-PROBE-OK " + windows.ToString(CultureInfo.InvariantCulture) + "/"
                    + windows.ToString(CultureInfo.InvariantCulture)
                : "THROTTLE-PROBE-FAIL skips=" + skipsOk.ToString(CultureInfo.InvariantCulture) + "/"
                    + windows.ToString(CultureInfo.InvariantCulture)
                    + " cap=" + capOk.ToString(CultureInfo.InvariantCulture) + "/"
                    + windows.ToString(CultureInfo.InvariantCulture));
            return allOk ? 0 : 1;
        }
        finally
        {
            CloseForeground();
            fixture.Dispose();
        }
    }
}
