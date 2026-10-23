// --drivetest：把 docs/11 §10（生命周期）、§9（降级链）、§7（帧预算）、§5（会话预算）
// 与 §2.2（命中仲裁）从「代码里写了」变成「一条命令能观察到」。
// 用桩件 + 脚本化时钟把每一条做成确定性事件（不靠把机器跑爆），输出 `CHECK <name> PASS/FAIL <证据>`，
// 末尾 DRIVE-OK / DRIVE-FAIL + 退出码。
// 它同时是 t1 契约 §8.2「两者各自可测」中 A 路（帧超时挂起）的那条：B 路（沙箱 8 连故障）由
// --hosttest 的预算探针驱动（M1 已有）；两条路汇到同一终态，第 4 组用 SuspendPort 证明这一接缝。
// partial（D-03c）：这里 = 入口 + 名册 + 生命周期 + 降级 + 挂起转发 + 指针 + 命中 + 状态；
// 帧预算与仲裁顺序在 PetDriveTest.Budget.cs。
using System;
using System.Collections.Generic;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

internal static partial class PetDriveTest
{
    /// <summary>判据入口：返回失败数（0 = 全过）。</summary>
    public static int Run(string[] args)
    {
        int failures = 0;
        Console.WriteLine($"CHECK-DISPLAY drive-abi {PetRuntimeAbi.InterfaceName} {PetRuntimeAbi.Version}");
        Console.WriteLine($"CHECK-DISPLAY drive-abi-types {PetRuntimeAbiTypes.Describe()}");
        failures += Surface();
        failures += Budget();
        failures += Lifecycle();
        failures += Degradation();
        failures += SuspendForwarding();
        failures += Priority();
        failures += Pointer();
        failures += Hitmap();
        failures += StateSemantics();
        failures += StateChecks.Run();
        failures += PluginLogChecks();
        Console.WriteLine(failures == 0 ? "DRIVE-OK" : "DRIVE-FAIL");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>组 0：名册探测按 manifest 的 kind 选接口（「装上了」判据的对照面）。</summary>
    private static int Surface()
    {
        int failures = 0;
        PetSurfaceRoster roster = PetSurfaceRoster.Discover(PluginHost.ProbePluginsRoot());
        Console.WriteLine($"CHECK-DISPLAY drive-roster {roster.Describe()}");
        Console.WriteLine($"CHECK-DISPLAY drive-ambiguity {roster.AmbiguityReport()}");
        Check(ref failures, "roster-dirs", roster.DiscoveredDirs >= 1, $"dirs={roster.DiscoveredDirs}");
        Check(ref failures, "roster-valid", roster.ValidManifests >= 1, $"valid={roster.ValidManifests}");
        // 名册必须能把「实现不了任何接口」的类型如实列出来（不静默跳过）。
        foreach (SurfaceRejection rejection in roster.Rejected)
        {
            Console.WriteLine($"CHECK-DISPLAY drive-rejected {rejection}");
        }

        // 已知的文档越界必须**显式可见**（队长 2026-10-16 要求：不许让它看起来像合规）：
        // tier 0 + entry 是托管 .dll 与 docs/13 §2「Tier 0 = 声明式资源包，无展开代码」字面冲突。
        // 这条**不是 FAIL**（方向未定，由 t6 按 D-08c 判），而是每次跑都打印出来的实况计数 ——
        // 判据 FAIL 会误导人以为实现坏了；静默会让人以为它合规。两者之间只有「显式记账」这一条路。
        foreach (string overrun in roster.Overruns)
        {
            Console.WriteLine($"CHECK-DISPLAY drive-overrun {overrun} t6-D-08c-pending");
        }

        Console.WriteLine($"CHECK-DISPLAY drive-overrun-count {roster.Overruns.Count} (tier0-with-managed-entry; docs13§2 conflict; t6 decides)");
        return failures;
    }

    /// <summary>组 2（docs/11 §10）：六阶段可达；转移痕迹逐段可读；**非法转移被拒**。</summary>
    private static int Lifecycle()
    {
        int failures = 0;
        PetInstance instance = MakeInstance(out _, out _);
        Check(ref failures, "lifecycle-start", instance.State == InstanceState.Discovered,
            $"state={instance.State}");

        instance.MarkImported("manifest-validated");
        instance.AttachAndStart(8, 8, 1.0);
        instance.Start();
        instance.Suspend("drive-suspend");
        instance.Resume("drive-resume");

        bool illegalRefused = false;
        try
        {
            // Running → Running 不在 docs/11 §10 的箭头集合里，必须被拒（乱序当场暴露）。
            instance.MarkImported("illegal-second-import");
        }
        catch (InvalidOperationException)
        {
            illegalRefused = true;
        }

        Check(ref failures, "lifecycle-illegal-refused", illegalRefused, "Running→Imported 被拒");
        Console.WriteLine($"CHECK-DISPLAY lifecycle-transitions {instance.DescribeTransitions()}");
        string path = instance.DescribeTransitions();
        Check(ref failures, "lifecycle-path",
            path.Contains("Discovered->Imported", StringComparison.Ordinal)
                && path.Contains("Imported->Instantiated", StringComparison.Ordinal)
                && path.Contains("Instantiated->Running", StringComparison.Ordinal)
                && path.Contains("Running->Suspended", StringComparison.Ordinal)
                && path.Contains("Suspended->Running", StringComparison.Ordinal),
            path);

        instance.Dispose();
        Check(ref failures, "lifecycle-disposed",
            instance.State == InstanceState.Disposed
                && instance.DescribeTransitions().Contains("Running->Disposed", StringComparison.Ordinal),
            instance.DescribeTransitions());

        // 转移表本身就是 docs/11 §10 的箭头集合，逐条对账。
        Check(ref failures, "lifecycle-table",
            InstanceStateMachine.Order.Count == 6
                && InstanceStateMachine.CanTransition(InstanceState.Discovered, InstanceState.Imported)
                && InstanceStateMachine.CanTransition(InstanceState.Running, InstanceState.Suspended)
                && InstanceStateMachine.CanTransition(InstanceState.Suspended, InstanceState.Running)
                && !InstanceStateMachine.CanTransition(InstanceState.Discovered, InstanceState.Running)
                && InstanceStateMachine.CanTransition(InstanceState.Imported, InstanceState.Disposed),
            InstanceStateMachine.Describe());
        return failures;
    }

    /// <summary>组 3（docs/11 §9）：降级链只许向前、逆序被拒且留证；QueryFrame 抛异常 → 该帧
    /// dropped；连续 8 次 → 降级到占位图，且占位帧的像素与真内容不同。</summary>
    private static int Degradation()
    {
        int failures = 0;
        PetInstance instance = MakeInstance(out _, out _);
        instance.AttachAndStart(8, 8, 1.0);
        instance.Start();

        var steps = new List<string>();
        instance.Display.Changed += step => steps.Add($"{step.Tier}:{step.Reason}");

        // 对照：另一只实例取真实帧 —— 降级链**不允许回退**，所以不能靠「先降再升回 Real」来比。
        var instanceReal = MakeInstance(out _, out _);
        instanceReal.AttachAndStart(8, 8, 1.0);
        instanceReal.Start();
        long real = PetCheckSupport.Signature(instanceReal.QueryFrame(0));

        bool forward = instance.Display.Advance(DisplayTier.Placeholder, "drive-degrade");
        bool backward = instance.Display.Advance(DisplayTier.Real, "drive-illegal-rollback");
        long placeholder = PetCheckSupport.Signature(instance.QueryFrame(0));

        Check(ref failures, "degrade-forward", forward, "Real→Placeholder 允许");
        Check(ref failures, "degrade-backward-refused", !backward,
            $"Refused 记录数={instance.Display.Refused.Count}");
        Check(ref failures, "degrade-forward-only", instance.Display.IsForwardOnly(),
            instance.Display.Describe());
        Check(ref failures, "degrade-observed", steps.Count >= 1,
            $"observed={string.Join("|", steps)}");
        Check(ref failures, "degrade-pixels-differ", placeholder != real,
            $"placeholder={placeholder:x} real={real:x}");

        // 真内容 → 抛异常路径：连续 8 次 → 降级到占位图。
        var instance2 = MakeInstance(out StubView view2, out _);
        instance2.AttachAndStart(8, 8, 1.0);
        instance2.Start();
        view2.ThrowOnQuery = true;
        var guard = new FrameBudgetGuard();
        int drops = 0;
        for (int i = 1; i <= 8; i++)
        {
            FrameOutcome outcome = guard.Drive(instance2, i * 33, World());
            if (outcome is FrameOutcome.Dropped or FrameOutcome.Completed)
            {
                drops++;
            }
        }

        Check(ref failures, "degrade-drop-count",
            instance2.FrameDrops == 8 && drops == 8,
            $"drops={instance2.FrameDrops} outcomes={drops} failures={instance2.LastFailure}");
        Check(ref failures, "degrade-placeholder-after-8",
            instance2.Display.Tier == DisplayTier.Placeholder,
            instance2.Display.Describe());
        Check(ref failures, "degrade-placeholder-serves",
            instance2.QueryFrame(9 * 33) is not null,
            "降级后仍交付占位帧（真实展示 → 占位图，不是黑屏）");
        instance.Dispose();
        instanceReal.Dispose();
        instance2.Dispose();
        return failures;
    }

    /// <summary>组 4（t1 契约 §8.2）：实例挂起必须转到插件层 —— 这一口就是「同一条 Suspended 路径」的接缝。</summary>
    private static int SuspendForwarding()
    {
        int failures = 0;
        var port = new NoopSuspendPort();
        PetInstance instance = MakeInstance(out _, out _);
        instance.SuspendPort = port;
        instance.AttachAndStart(8, 8, 1.0);
        instance.Start();
        instance.Suspend("drive-forward-check");
        Check(ref failures, "suspend-port-called", port.Calls == 1, port.LastCall);

        // 重复挂起是幂等的：计数不该涨（docs/11 §10 的 suspended 只有一个态）。
        instance.Suspend("drive-forward-check-again");
        Check(ref failures, "suspend-idempotent", port.Calls == 1, $"calls={port.Calls}");

        instance.Resume("drive-resume");
        instance.Suspend("drive-forward-check-2");
        Check(ref failures, "suspend-port-count", port.Calls == 2, $"calls={port.Calls}");
        instance.Dispose();
        return failures;
    }

    /// <summary>组 6（docs/11 §5 + §9:260）：优先级顺序、穿透优先、拒绝转下一个、会话 10s 强制取消。</summary>
    private static int Pointer()
    {
        int failures = 0;
        var log = new List<string>();
        var low = new StubSurface("low", 0, hit: true, log);
        var high = new StubSurface("high", 5, hit: true, log);
        var declining = new StubSurface("decline", 9, hit: true, log) { Decline = true };
        PetInstance instance = MakeInstance(
            out _, out _, new List<IPointerSurface> { low, high, declining });
        instance.AttachAndStart(64, 64, 1.0);
        instance.Start();
        instance.QueryFrame(0); // 先出一帧：命中判定要有帧可判

        bool taken = instance.PointerDown(32, 32, PointerButton.Left, 0);
        Check(ref failures, "pointer-highest-first",
            taken && instance.ActiveSurfaceId == "high",
            $"surface={instance.ActiveSurfaceId} order={string.Join(",", log)}");
        // 「拒绝转下一个」的完整证据是两条合起来：decline 优先级最高（9）会被**先问**，
        // 它返回 null 之后会话必须由下一个（high）开启。只断言「谁拿到了会话」测不出被跳过的那一步。
        Check(ref failures, "pointer-decline-skipped",
            log.IndexOf("decline:down") >= 0
                && log.IndexOf("high:down") > log.IndexOf("decline:down")
                && instance.ActiveSurfaceId != "decline",
            string.Join(",", log));

        instance.PointerMove(33, 33);
        instance.PointerUp(33, 33);
        Check(ref failures, "pointer-session-closed",
            instance.ActiveSession is null && instance.SessionsEnded == 1,
            $"ended={instance.SessionsEnded}");

        // 穿透优先：alpha=0 的点不许被任何 surface 变成可点（docs/11:190）。
        bool transparentTaken = instance.PointerDown(0, 0, PointerButton.Left, 0);
        Check(ref failures, "pointer-alpha-zero-passes-through", !transparentTaken,
            "alpha=0 的点即使 surface 命中也不开会话");

        // 未命中任何 surface 时不开会话（HitTest 全 false）。
        low.Hits = false;
        high.Hits = false;
        declining.Hits = false;
        Check(ref failures, "pointer-no-hit-no-session", !instance.PointerDown(32, 32, PointerButton.Left, 0),
            "无 surface 命中 → 不开会话");
        low.Hits = true;
        high.Hits = true;

        // 会话超过 10s 无 up / cancel → 强制取消（docs/11:260）。
        instance.PointerDown(32, 32, PointerButton.Left, 1000);
        bool forced = instance.EnforceSessionBudget(1000 + PetInstance.SessionCancelBudgetMs + 1);
        Check(ref failures, "pointer-session-budget",
            forced && instance.SessionsForcedCancel == 1
                && instance.Arbiter.Events.CountOf(PetInstance.HostCanceledEventName) == 1,
            $"forced={forced} cancels={instance.SessionsForcedCancel}"
                + $" canceled-events={instance.Arbiter.Events.CountOf(PetInstance.HostCanceledEventName)}");
        instance.Dispose();
        return failures;
    }

    internal static WorldSnapshot World() => WorldSnapshot.Empty(new PetRect(0, 0, 128, 128));

    internal static PetInstance MakeInstance(out StubView view, out StubBrain brain)
        => MakeInstance(out view, out brain, new List<IPointerSurface>());

    internal static PetInstance MakeInstance(
        out StubView view, out StubBrain brain, IReadOnlyList<IPointerSurface> surfaces)
    {
        view = new StubView();
        brain = new StubBrain();
        return new PetInstance("drive.instance", view, brain, surfaces, new StubState(), "drive.plugin", "stub");
    }

    internal static bool Same(double left, double right) => Math.Abs(left - right) < 1e-9;

    internal static void Check(ref int failures, string name, bool ok, string detail)
    {
        Console.WriteLine($"CHECK drive-{name} {(ok ? "PASS" : "FAIL")} {detail}");
        if (!ok)
        {
            failures++;
        }
    }
}

/// <summary>
/// ABI 类型面的自述（--drivetest 的对照行）：四接口在哪个命名空间、以及**哪个名字对应 docs/11 的哪个接口**。
/// 对照表的完整版在 docs/handoff/M2-name-map.md（core 之外，因为那几行本身会撞 D-03b 黑名单）。
/// </summary>
internal static class PetRuntimeAbiTypes
{
    public static string Describe() =>
        $"ns={typeof(IPetView).Namespace} view={nameof(IPetView)} brain={nameof(IBrain)}"
        + $" pointer={nameof(IPointerSurface)} state={nameof(IPetState)}"
        + $" count={typeof(IPetView).Assembly.GetTypes().Count(Counted)}";

    private static bool Counted(Type type) =>
        type.Namespace == typeof(IPetView).Namespace && type.IsInterface;
}

/// <summary>
/// 判据共用的像素签名（--animtest 与 --drivetest 都读「帧内容变了没有」）。
/// 用整帧像素的 FNV-1a 而不是实现方的私有帧号：像素相同才是 docs/11:120 的可观察等价物。
/// </summary>
internal static class PetCheckSupport
{
    public static long Signature(Frame frame)
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
}
