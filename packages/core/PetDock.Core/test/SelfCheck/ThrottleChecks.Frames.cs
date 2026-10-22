// RFC-21 §4 的判据体（第二块：真窗口上的帧循环判据与诊断读数）。
//
// 这里驱动的是**真的生产对象**：真的 LayeredFrameWindow、真的 ResidentFrameLoop、
// 真的 PetFrameSource / PetInstance 链（LastFrameReused 的唯一更新点就在那条链上）。
// 世界状态（前台全屏窗、盖住宠物窗的顶层窗）由 ThrottleChecks.Windows.cs 摆出。
//
// 拆成四份（D-03c 的 400 行上限，与 UiHost / ResidentFrameLoop 拆 partial 同源）：
//   ThrottleChecks.cs          探测层与档位判定的判据（不需要窗口）
//   本文件                     帧循环与诊断读数的判据
//   ThrottleChecks.Windows.cs  世界状态的构造（前台抢占、覆盖窗、真窗口夹具）
//   ThrottleChecks.Sink.cs     日志取证口（CapturingSink）
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static partial class ThrottleChecks
{
    /// <summary>判据的等待窗：够 30fps 的泵出若干 tick，又不至于让自检变慢。</summary>
    private const int SpinMs = 600;

    /// <summary>等档位收敛的上限（泵可能正在睡一个周期，250ms 的滴答窗口之外还要留余量）。</summary>
    private const int ConvergeMs = 3000;

    private static void LoopChecks(Check done)
    {
        // ⑭ 泵意外退出后自愈（RFC-21 t14）：自带一台可故障夹具，不干扰下面的静止/全屏读数。
        PumpSelfHeal(done);

        // 静止档的夹具必须是**真静止**：单帧 ⇒ 每帧交付同一个位图对象 ⇒ LastFrameReused=true ⇒ Idle。
        // 为什么不能用默认的 3 帧 × 1fps 夹具：它每秒换一次帧，Idle 因此是瞬态，而本文件的判据
        // 分两次读「档位」与「周期」（中间泵还会再判一次），于是会拼出 Advance 从未写过的组合
        // —— 2026-10-22 实测同一份二进制第一次 ui-throttle-idle-period-cap FAIL、第二次 PASS（申报见 decisions）。
        LayerFixture fixture = LayerFixture.Create(clipFrames: 1);
        try
        {
            // 用户设置故意给 60fps：静止档的 33.3ms 上限只有这样才不是恒等式（RFC-21 §8 第 1 条）。
            const double userFrameMs = 1000.0 / 60.0;
            using var loop = new ResidentFrameLoop(fixture.Window);
            loop.SetTargetFrameMs(userFrameMs);
            loop.Start();

            // 静止档：内容源每帧交付同一个位图对象 ⇒ LastFrameReused=true ⇒ Idle。
            long ticks0 = loop.Ticks;
            long composed0 = loop.Composed;
            long submitted0 = loop.Submitted;
            Spin(SpinMs);
            long ticks = loop.Ticks - ticks0;
            long composed = loop.Composed - composed0;
            long submitted = loop.Submitted - submitted0;
            long skipped = loop.Skipped;
            FrameTier tier = loop.Tier;

            // 三个相对条件，都不写死会随运行累加的量：
            //   ticks >= 10 —— 等待窗内泵真的跑了足够多 tick（相对下界，不是帧数快照）；
            //   composed >= ticks —— 合成本身**不被档位跳过**（t11 的语义，动画能不能回来的判据面）；
            //   submitted <= 4 —— 真的上屏次数被压到极低（600ms 里 30fps 本该 ~18 次）。
            bool skipsOk = tier == FrameTier.Idle
                && ticks >= 10
                && submitted <= 4
                && composed >= ticks;
            done("ui-throttle-idle-skips-submit", skipsOk,
                "tier=" + Token(tier)
                + " ticks=" + ticks.ToString(CultureInfo.InvariantCulture)
                + " composed=" + composed.ToString(CultureInfo.InvariantCulture)
                + " submitted=" + submitted.ToString(CultureInfo.InvariantCulture)
                + " skipped=" + skipped.ToString(CultureInfo.InvariantCulture));

            // 静止档的周期上限：设置 16.7ms ⇒ 档位给 33.3ms（max(设置, 1000/30)）。
            double idleTarget = loop.TargetFrameMs;
            bool capOk = tier == FrameTier.Idle
                && Near(idleTarget, FrameThrottlePolicy.IdleCapMs)
                && idleTarget > userFrameMs;
            done("ui-throttle-idle-period-cap", capOk,
                "setting=" + Fmt(userFrameMs) + "ms"
                + " tier=" + Token(tier)
                + " target=" + Fmt(idleTarget) + "ms");

            DiagnosticLine(done, loop);

            // 全屏档：真的前台窗盖满显示器。
            FullscreenChecks(done, loop, fixture);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    /// ⑭ 泵意外退出后自愈（RFC-21 t14 / t13 的修复面）。
    ///
    /// 这一条钉住的是第二处**冻结级**缺陷：`FramePump.Loop()` 的三条自退路（tick 为 null /
    /// tick 抛异常 / tick 返回 false）都只 break、不通知任何人，而 `_pumping` 只在起停泵时写
    /// ⇒ 泵自退后 `Reevaluate()` 的第一道门永远短路 ⇒ 宠物永久停帧，且 `Pumping` 谎报「在跑」。
    ///
    /// 造的是**真的**退出，不是标志位：让内容源在下一次合成时抛异常，异常穿过
    /// `LayeredFrameWindow.ComposeFrame` 冒到 `FramePump.Loop()` 的 try/catch，
    /// 泵据此记 `tick-failed` 并 break —— 走的是生产代码里那条真路径。
    /// 自愈也走**真的**驱动源：`Reevaluate()`（UiHost 每 250ms 滴答调的就是它）。
    /// </summary>
    private static void PumpSelfHeal(Check done)
    {
        LayerFixture fixture = LayerFixture.CreateFailable();
        CornerBlobContent? content = fixture.FailableContent;
        var logs = new CapturingSink();
        IKernelLogSink? previousSink = KernelLog.Sink;
        KernelLog.Sink = logs;
        try
        {
            using var loop = new ResidentFrameLoop(fixture.Window);
            loop.SetTargetFrameMs(1000.0 / 30.0);
            loop.Start();

            bool startedPumping = WaitFor(ConvergeMs, () => loop.Pumping);
            long ticksBeforeExit = loop.Ticks;

            // ① 真的把泵打退：内容源故障 ⇒ 下一 tick 抛异常 ⇒ 泵自退。
            if (content is null)
            {
                done("ui-throttle-pump-self-heals", false, "no-failable-content (夹具构造失败)");
                return;
            }

            content.FailNextFrame = true;
            bool exited = WaitFor(ConvergeMs, () => !loop.Pumping);

            // ② 退出瞬间的读数不谎报 —— 必须在下面这一滴答起泵**之前**取，否则测的是自愈后的值。
            bool pumpingLied = loop.Pumping;
            long ticksAtExit = loop.Ticks;
            long composedAtExit = loop.Composed;

            // ③ 自愈：走生产路径的驱动源，下一滴答必须把泵重新起起来，且读数继续涨。
            //    **先滴答、后读日志**：`throttle-pump-exited` 这行是 `Reevaluate()` 自己写的
            //    （ResidentFrameLoop.Throttle.cs:162），泵线程只记 ExitReason、从不写日志。
            //    反过来读只会恒得空串 —— 本检查曾因此永远 FAIL（exitReason=(none)）。
            loop.Reevaluate();
            bool healed = WaitFor(ConvergeMs, () => loop.Pumping);
            Spin(SpinMs);
            long ticksAfter = loop.Ticks - ticksAtExit;
            long composedAfter = loop.Composed - composedAtExit;

            // ④ 退出被记录且读数不谎报。ExitReason 只接受那三个**真自退**取值之一：
            //    "stopped" 是正常 Stop() 走完 Loop 尾时的兜底值，拿它当「意外退出」的证据等于没证。
            string exitReason = logs.LastReason();
            bool reasonOk = string.Equals(exitReason, "tick-failed", StringComparison.Ordinal)
                || string.Equals(exitReason, "tick-returned-false", StringComparison.Ordinal)
                || string.Equals(exitReason, "no-tick", StringComparison.Ordinal);
            bool notStopped = !string.Equals(exitReason, "stopped", StringComparison.Ordinal);
            bool logged = logs.Contains("throttle-pump-exited");

            bool exitOk = startedPumping && exited && !pumpingLied && reasonOk && notStopped && logged;
            bool healOk = healed && loop.Pumping && ticksAfter > 0 && composedAfter > 0;

            // ④ 隐藏时不自愈：再打退一次泵，然后**用户把宠物藏起来**，再滴答 —— 不许起泵。
            //    这条防的是「用户按了隐藏，宠物自己又回来了」（可见性归 Start/Hide 独占）。
            content.FailNextFrame = true;
            bool exitedAgain = WaitFor(ConvergeMs, () => !loop.Pumping);
            loop.Hide();
            long ticksBeforeHidden = loop.Ticks;
            loop.Reevaluate();
            Spin(300);
            bool hiddenNoHeal = !loop.Pumping && loop.Ticks == ticksBeforeHidden;

            bool allOk = exitOk && healOk && exitedAgain && hiddenNoHeal;
            done("ui-throttle-pump-self-heals", allOk,
                "pumpingBeforeExit=" + Flag(startedPumping)
                + " pumpingAfterExit=" + Flag(pumpingLied)
                + " exitReason=" + (exitReason.Length == 0 ? "(none)" : exitReason)
                + " reasonIsSelfExit=" + Flag(reasonOk && notStopped)
                + " exitedLog=" + Flag(logged)
                + " healed=" + Flag(healed)
                + " ticks+" + ticksAfter.ToString(CultureInfo.InvariantCulture)
                + " composed+" + composedAfter.ToString(CultureInfo.InvariantCulture)
                + " hiddenExited=" + Flag(exitedAgain)
                + " hiddenPumpingAfter=" + Flag(loop.Pumping)
                + " hiddenTicks+" + (loop.Ticks - ticksBeforeHidden).ToString(CultureInfo.InvariantCulture)
                + " ticksBeforeExit=" + ticksBeforeExit.ToString(CultureInfo.InvariantCulture)
                + " paths=只验了 tick-failed（内容源抛异常）；tick-returned-false / no-tick 未造");
        }
        catch (Exception error)
        {
            done("ui-throttle-pump-self-heals", false, $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            KernelLog.Sink = previousSink;
            fixture.Dispose();
        }
    }

    /// <summary>⑧⑨ 全屏：停泵不藏窗 / 恢复时首帧立即提交。</summary>
    private static void FullscreenChecks(Check done, ResidentFrameLoop loop, LayerFixture fixture)
    {        SmallForeground();
        FullscreenForeground full = FullscreenForeground.Create(out string acquire);
        if (!full.Acquired)
        {
            // 抢不到前台就没有「真的全屏」这个状态 —— 判 FAIL 并打印原因，绝不假装成立。
            done("ui-throttle-fullscreen-stops-pump", false, "foreground-not-acquired " + acquire);
            done("ui-throttle-fullscreen-resume", false, "foreground-not-acquired " + acquire);
            full.Dispose();
            CloseForeground();
            return;
        }

        try
        {
            bool converged = WaitFor(ConvergeMs, () => loop.Tier == FrameTier.Fullscreen && !loop.Pumping);
            long ticksAtPause = loop.Ticks;
            long submittedAtPause = loop.Submitted;
            int framesAtPause = loop.Frames;

            // 泵真的停了：再等一个窗口，tick 与提交都不许再涨。
            Spin(300);
            long ticksAfterPause = loop.Ticks - ticksAtPause;
            long submittedAfterPause = loop.Submitted - submittedAtPause;

            bool pumpingNow = loop.Pumping;
            bool visibleNow = fixture.Window.Visible;
            // 全屏档 = 只停泵不藏窗：pumping=False 与 visible=True 必须同时成立（所有者裁定）。
            bool stoppedOk = converged
                && full.CoversMonitor
                && !pumpingNow
                && visibleNow
                && ticksAfterPause == 0
                && submittedAfterPause == 0;
            done("ui-throttle-fullscreen-stops-pump", stoppedOk,
                "pumping=" + Flag(pumpingNow)
                + " frames=" + framesAtPause.ToString(CultureInfo.InvariantCulture)
                + " visible=" + Flag(visibleNow)
                + " tier=" + Token(loop.Tier)
                + " ticksAfterPause=" + ticksAfterPause.ToString(CultureInfo.InvariantCulture)
                + " coversMonitor=" + Flag(full.CoversMonitor)
                + " foreground=" + ForegroundDescription());
        }
        finally
        {
            full.Dispose();
        }

        // 恢复：离开全屏，前台回到一个明显不盖满的小窗，然后走生产路径的恢复驱动（UiHost.TickOnce 调它）。
        SmallForeground();
        loop.Reevaluate();
        bool resumed = WaitFor(ConvergeMs, () => loop.Pumping);
        long submittedAfterResume = loop.Submitted;
        Spin(SpinMs);
        long submittedInWindow = loop.Submitted - submittedAfterResume;
        bool resumeOk = resumed
            && loop.Pumping
            && submittedAfterResume > 0
            && loop.Visible
            && loop.Tier != FrameTier.Fullscreen;
        done("ui-throttle-fullscreen-resume", resumeOk,
            "pumping=" + Flag(loop.Pumping)
            + " submitted=" + submittedAfterResume.ToString(CultureInfo.InvariantCulture)
            + " submittedAfter=" + submittedInWindow.ToString(CultureInfo.InvariantCulture)
            + " target=" + Fmt(loop.TargetFrameMs) + "ms"
            + " tier=" + Token(loop.Tier)
            + " visible=" + Flag(loop.Visible));
        CloseForeground();
    }

    /// <summary>⑫ 诊断包读到档位：一行文本里 tier/ticks/submitted 都是真读数，不是编的。</summary>
    private static void DiagnosticLine(Check done, ResidentFrameLoop loop)
    {
        string root = System.IO.Path.Combine(
            AppContext.BaseDirectory, "petdock-throttle-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new DiagnosticService(new PetDataStore(root), host: null, pet: null, log: null)
            {
                FrameLoop = loop,
            };
            DiagnosticReadings readings = service.CollectReadings();
            string summary = DiagnosticSections.BuildSummary(readings, RedactRules.ForCurrentMachine(root, null));
            bool hasLine = summary.Contains("节流档位:", StringComparison.Ordinal);
            string tier = readings.ThrottleTier;
            bool ok = readings.HasThrottle
                && hasLine
                && tier.Length > 0
                && summary.Contains("tier=" + tier, StringComparison.Ordinal)
                && summary.Contains("ticks=" + readings.ThrottleTicks.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                && summary.Contains("submitted=" + readings.ThrottleSubmitted.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                // 帧预算那一行也要拼上同一个档位读数（不新增记录字段，只补一行文本）。
                && readings.FrameBudgetLine.Contains("throttle(", StringComparison.Ordinal)
                // 读数必须是**这个**循环的，不是 0 占位。
                && readings.ThrottleTicks == loop.Ticks;
            done("ui-throttle-diagnostic-line", ok,
                "tier=" + tier
                + " ticks=" + readings.ThrottleTicks.ToString(CultureInfo.InvariantCulture)
                + " submitted=" + readings.ThrottleSubmitted.ToString(CultureInfo.InvariantCulture)
                + " loopTicks=" + loop.Ticks.ToString(CultureInfo.InvariantCulture)
                + " hasLine=" + Flag(hasLine)
                + " line=" + ExtractThrottleLine(summary));
        }
        finally
        {
            try
            {
                if (System.IO.Directory.Exists(root))
                {
                    System.IO.Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception)
            {
                // 临时目录删不掉不影响判据结论（先例：DiagnosticChecks）。
            }
        }
    }

    private static string ExtractThrottleLine(string summary)
    {
        foreach (string line in summary.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("节流档位:", StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return "(none)";
    }

    private static bool WaitFor(int timeoutMs, Func<bool> predicate)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate())
            {
                return true;
            }

            Spin(50);
        }

        return predicate();
    }

    private static void Spin(int ms)
    {
        Thread.Sleep(ms);
        Application.DoEvents();
    }
}
