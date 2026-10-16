// --drivetest 的第二块：帧预算（docs/11 §7 / SM-04）与仲裁顺序（docs/11:162）。
// 拆自 PetDriveTest.cs（D-03c：单文件 ≤400 行）；判据与理由见各自方法头。
// 命令形态与总体判据见 PetDriveTest.Run。
using System;
using System.Collections.Generic;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

internal static partial class PetDriveTest
{
    /// <summary>组 1（docs/11 §7）：三个预算常量 + 超时记 skip + **连续 8 次触发挂起**。</summary>
    private static int Budget()
    {
        int failures = 0;
        Check(ref failures, "budget-frame", Same(FrameBudgetGuard.FrameBudgetMs, 8.3),
            $"frame={FrameBudgetGuard.FrameBudgetMs}");
        Check(ref failures, "budget-idle", Same(FrameBudgetGuard.IdleFrameBudgetMs, 1000.0 / 30.0),
            $"idle={PetPoint.Fmt(FrameBudgetGuard.IdleFrameBudgetMs)}");
        Check(ref failures, "budget-global", Same(FrameBudgetGuard.GlobalFrameBudgetMs, 25.0),
            $"global={FrameBudgetGuard.GlobalFrameBudgetMs}");
        Check(ref failures, "budget-limit", FrameBudgetGuard.ConsecutiveTimeoutLimit == 8,
            $"limit={FrameBudgetGuard.ConsecutiveTimeoutLimit}");

        // 脚本化时钟：每帧 40ms。为什么必须是 40 而不是「比 8.3 大就行」——
        // docs/11:225 的空闲档是 33.3ms：桩件按帧索引缓存位图，连续两帧常拿到同一个对象，
        // 于是宿主判为「无内容变化」，预算自动升到 33.3ms。20ms 的帧因此**合法地**不超预算。
        // 这条正是「同一个数在两档下含义不同」的实例，判据必须跨过两档里更宽的那个。
        var clock = new ScriptedClock();
        for (int i = 0; i < 7; i++)
        {
            clock.AppendFrame(40.0, 41.0);
        }

        var guard = new FrameBudgetGuard(clock.Now);
        PetInstance instance = MakeInstance(out StubView view, out _);
        instance.AttachAndStart(8, 8, 1.0);
        instance.Start();

        var outcomes = new List<FrameOutcome>();
        for (int i = 0; i < 7; i++)
        {
            outcomes.Add(guard.Drive(instance, (i + 1) * 33, World()));
        }

        Check(ref failures, "budget-skip-count",
            guard.FrameSkips == 7 && guard.ConsecutiveTimeouts == 7 && guard.TimeoutSuspends == 0
                && outcomes.TrueForAll(o => o == FrameOutcome.Skipped),
            $"skips={guard.FrameSkips} consecutive={guard.ConsecutiveTimeouts} suspends={guard.TimeoutSuspends}");
        Check(ref failures, "budget-running", instance.State == InstanceState.Running,
            $"state={instance.State} (第 7 帧还不该挂起)");

        // 第 8 帧：同一脚本时钟续上，连续计数到 8 → 挂起。
        clock.AppendFrame(40.0, 41.0);
        FrameOutcome eighth = guard.Drive(instance, 8 * 33, World());
        Check(ref failures, "budget-suspend-at-8",
            eighth == FrameOutcome.SuspendedByTimeout && guard.TimeoutSuspends == 1,
            $"outcome={eighth} suspends={guard.TimeoutSuspends}");
        Check(ref failures, "budget-suspended-state", instance.State == InstanceState.Suspended,
            $"state={instance.State}");

        // 挂起后不再调用插件：QueryFrame 计数冻结、dt 被零化（docs/11:134）。
        int queriesBefore = view.QueryCalls;
        long ticksBefore = instance.Ticks;
        FrameOutcome after = guard.Drive(instance, 9 * 33, World());
        Check(ref failures, "budget-frozen-after-suspend",
            after == FrameOutcome.NotRunning && view.QueryCalls == queriesBefore && instance.Ticks == ticksBefore,
            $"outcome={after} queries={view.QueryCalls} ticks={instance.Ticks}");

        instance.Resume("drive-resume");
        Check(ref failures, "budget-dt-zero-after-resume", instance.NextDeltaMs(1234) == 0,
            "恢复后第一帧 dt=0");

        // 全局预算（docs/11:226）：M2 只有一个实例，无「合计」可测 —— 它只计数、不阻断。
        // 判据：每帧耗时 1ms（远在 8.3ms 内），但帧**起点**之间隔 30ms（宿主循环自身的开销），
        // 于是第 2、3 帧的全局跨度是 31ms > 25ms。**第 1 帧不计入**：它没有上一帧起点，
        // 全局跨度只能取自身耗时 —— 这条差异本身就是判据的一部分（首帧的全局开销无从测量）。
        var globalClock = new ScriptedClock();
        for (int i = 0; i < 3; i++)
        {
            globalClock.AppendFrame(1.0, 30.0);
        }

        var globalGuard = new FrameBudgetGuard(globalClock.Now);
        PetInstance other = MakeInstance(out _, out _);
        other.AttachAndStart(8, 8, 1.0);
        other.Start();
        globalGuard.Drive(other, 33, World());
        globalGuard.Drive(other, 66, World());
        globalGuard.Drive(other, 99, World());
        Check(ref failures, "budget-global-counted", globalGuard.GlobalOverBudgetFrames == 2,
            $"global-over={globalGuard.GlobalOverBudgetFrames} span={globalClock.GlobalSpan}"
                + $" over-budget={FrameBudgetGuard.GlobalFrameBudgetMs}ms");
        Check(ref failures, "budget-global-not-blocking", other.State == InstanceState.Running,
            "单实例下全局预算只计数、不挂起（无「合计」对象可测）");

        Console.WriteLine($"CHECK-DISPLAY drive-budget {guard.Describe()}");
        instance.Dispose();
        other.Dispose();
        return failures;
    }

    /// <summary>组 5（docs/11:162）：仲裁顺序就是 ABI 语义 —— 直接断言排序结果，
    /// 且同档必须保持实现方给出的原序（稳定）。</summary>
    private static int Priority()
    {
        int failures = 0;
        var batch = new List<BrainDirective>
        {
            BrainDirective.RequestCapability("input.mousePosition", "drive"),
            BrainDirective.Emit(PetEvent.Named("pet.drive")),
            BrainDirective.MoveTo(new PetPoint(1, 1)),
            BrainDirective.SetAnimation("idle"),
            BrainDirective.Suspend("drive-priority"),
        };

        IReadOnlyList<BrainDirective> sorted = HostArbiter.Sorted(batch);
        string order = string.Join(">", Kinds(sorted));
        Check(ref failures, "arbiter-order",
            order == "Suspend>SetAnimation>MoveTo>Emit>RequestCapability", order);

        // 同档稳定：两条 setAnimation 必须保持给出顺序（docs/11:161 的幂等要求）。
        var same = new List<BrainDirective>
        {
            BrainDirective.SetAnimation("first"),
            BrainDirective.MoveTo(new PetPoint(2, 2)),
            BrainDirective.SetAnimation("second"),
        };
        IReadOnlyList<BrainDirective> stable = HostArbiter.Sorted(same);
        Check(ref failures, "arbiter-stable",
            stable[0].Clip == "first" && stable[1].Clip == "second" && stable[2].Kind == DirectiveKind.MoveTo,
            string.Join(",", Kinds(stable)));

        PetInstance instance = MakeInstance(out _, out StubBrain brain);
        instance.AttachAndStart(8, 8, 1.0);
        instance.Start();
        brain.Enqueue(
            BrainDirective.SetAnimation("idle"),
            BrainDirective.RequestCapability("not.a.capability", "drive"),
            BrainDirective.Emit(PetEvent.Named("pet.drive")));
        instance.TickBrain(33, World());
        Check(ref failures, "arbiter-applied",
            instance.Arbiter.Applied[DirectiveKind.SetAnimation] == 1
                && instance.Arbiter.Refused[DirectiveKind.RequestCapability] == 1
                && instance.Arbiter.Events.CountOf("pet.drive") == 1,
            instance.Arbiter.Describe());
        Check(ref failures, "arbiter-refusal-recorded",
            instance.Arbiter.LastRefusal.Contains("whitelist", StringComparison.Ordinal),
            instance.Arbiter.LastRefusal);

        // 白名单之外的能力不许被静默授予（docs/14 §2）。
        brain.Enqueue(BrainDirective.RequestCapability("assets.write:cas", "drive-evil"));
        instance.TickBrain(33, World());
        Check(ref failures, "arbiter-forbidden-capability",
            instance.Arbiter.GrantedCapabilities == 0
                && instance.Arbiter.Refused[DirectiveKind.RequestCapability] == 2,
            instance.Arbiter.Describe());

        // brain 抛异常：本 tick 的指令全部作废 + degraded（docs/11:259）。
        brain.ThrowOnTick = true;
        brain.Enqueue(BrainDirective.Emit(PetEvent.Named("pet.never")));
        instance.TickBrain(33, World());
        Check(ref failures, "arbiter-brain-degraded",
            instance.BrainDegraded && instance.Arbiter.Events.CountOf("pet.never") == 0,
            $"degraded={instance.BrainDegraded} never={instance.Arbiter.Events.CountOf("pet.never")}");

        // degraded 之后恢复：下一次成功 tick 必须清除标记（文档没说「永久 degraded」）。
        brain.ThrowOnTick = false;
        instance.TickBrain(33, World());
        Check(ref failures, "arbiter-brain-recovered", !instance.BrainDegraded,
            $"degraded={instance.BrainDegraded} faults={instance.BrainFaults}");
        instance.Dispose();
        return failures;
    }

    private static IEnumerable<string> Kinds(IReadOnlyList<BrainDirective> items)
    {
        foreach (BrainDirective directive in items)
        {
            yield return directive.Kind.ToString();
        }
    }
}
