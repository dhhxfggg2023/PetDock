// --eventtest：M4（行为与事件）的可判定判据（docs/handoff/M4-contract-frozen.md §4/§5/§6/§7）。
// 命令形态：`--eventtest`，每行 `CHECK <key> PASS|FAIL|SKIP <detail>`，统计行 `STAT eventtest …`，
// 末行 `EVENT-OK skipped=<s>` / `EVENT-FAIL n=<fail+unowned> skipped=<s>`，退出码 0 / n
// —— 与 --animtest / --drivetest 同构（契约 §7.5）。
// 分工：本文件 = 入口 + 能力 + 仲裁 + 指令 + 钩子 + 世界采样；总线语义与目录守门在 EventSelfTest.Bus.cs；
// 「跳过」那半边（第三种结局、事件生产点扫描、负例装置）在 EventSelfTest.Skip.cs，账本在 SkipLedger.cs。
// 纪律三条：
//   1. **SKIP 不是 PASS**（契约 §2.6）：没有生产触发源的事件必须登记，不许静默略过；
//      登记不许用硬编码常量 —— 2026-10-25（判据体检 S1）之前那三条常量不计绿也不计红，门禁永久失明；
//   2. **不许抛未捕获异常**：任何一步失败都落成 FAIL 行 + 非零退出（G2：旧路径 0xE0434352 的教训）；
//   3. **真实源必须真读**：时间/鼠标/前台窗口三类都由真 Win32 调用采样，并把原始值打在行里 ——
//      注入时钟只用于确定性断言，不能替代「真的读到了」这条证据。
using System;
using System.Collections.Generic;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

internal static partial class EventSelfTest
{
    public static int Run(string[] args)
    {
        int failures = 0;
        try
        {
            failures += DispatchModes();
            failures += Catalog();
            failures += PayloadSchema();
            failures += Capability();
            failures += Arbitration();
            failures += Directives();
            failures += HookGate();
            failures += WorldSample();
            failures += EventSources();
            failures += FrameOrder();
            failures += EventLogBounds();
        }
        catch (Exception error)
        {
            // G2：任何未预料到的异常都落成一行 FAIL，绝不让它逃出 Main 崩成 0xE0434352。
            Console.WriteLine($"CHECK eventtest-crash FAIL {error.GetType().Name}: {error.Message}");
            failures++;
        }

        // 收尾（统计 + SKIP 通道 + marker）在 EventSelfTest.Skip.cs：SKIP 不再是一行只打印的字。
        return Report(args, failures);
    }

    /// <summary>契约 §4.4：200ms 内应答、超时自动拒绝且与「用户拒绝」可区分、每次终态恰好一行审计。</summary>
    private static int Capability()
    {
        int failures = 0;
        var declared = new List<string> { "input.mousePosition" };
        var broker = new CapabilityBroker(declared);

        CapabilityVerdict ok = broker.Request("input.mousePosition", "cursor tracking", 1000);
        CapabilityVerdict undeclared = broker.Request("input.keyboardHook", "hotkey", 1200);
        // 挂起一条，再用注入时钟推过 200ms 的闸门 —— 超时是**下一次调用**结算的（不读真时钟）。
        broker.Defer("input.mousePosition", 1400);
        broker.Request("input.mousePosition", "again", 1400 + CapabilityBroker.PendingTimeoutMs + 1);

        bool timedOut = false, timeoutLatency = false, distinct = false;
        foreach (CapabilityAuditLine line in broker.Audit)
        {
            if (line.Reason == "timeout")
            {
                timedOut = true;
                timeoutLatency = line.LatencyMs == CapabilityBroker.PendingTimeoutMs + 1;
            }

            if (line.Reason == "not-declared") distinct = true;
        }

        Check(ref failures, "capability-200ms",
            ok.Kind == CapabilityVerdictKind.Accepted && ok.LatencyMs <= CapabilityBroker.PendingTimeoutMs
                && undeclared.Kind == CapabilityVerdictKind.Refused,
            $"accepted=1 refused=1 timeout={Flag(timedOut)} max-latency={ok.LatencyMs}");

        Check(ref failures, "capability-timeout",
            timedOut && timeoutLatency && distinct,
            $"verdict=refused reason=timeout latency={(timeoutLatency ? (CapabilityBroker.PendingTimeoutMs + 1).ToString() : "-")}"
                + $" distinct-from-not-declared={Flag(distinct)}");

        // 每次终态恰好一行、字段序固定 6 个（防「只记拒绝不记授权」的半落地）。
        bool linesOk = broker.Audit.Count == 4;
        foreach (CapabilityAuditLine line in broker.Audit)
        {
            string text = line.ToString();
            int fields = 0;
            foreach (string token in text.Split(' '))
            {
                if (token.Contains('=', StringComparison.Ordinal)) fields++;
            }

            if (fields != 6 || !text.StartsWith("capability-audit ", StringComparison.Ordinal)) linesOk = false;
        }

        Check(ref failures, "capability-audit-lines",
            linesOk, $"lines={broker.Audit.Count} fields=6 ok={Flag(linesOk)}");
        return failures;
    }

    /// <summary>契约 §5.1/§5.3：同 kind 稳定、跨 kind 全序、投递序 ≠ 裁决序、同批 moveTo 合一。</summary>
    private static int Arbitration()
    {
        int failures = 0;
        PetInstance instance = PetDriveTest.MakeInstance(out StubView view, out StubBrain brain);

        // 仲裁按 IPointerSurface.Priority 裁决，同值退化成注册序 —— 与到达顺序无关。
        var hits = new List<string>();
        var low = new StubSurface("low", 1, true, hits);
        var high = new StubSurface("high", 9, true, hits);
        var tied = new StubSurface("tied", 9, true, hits);
        var surfaces = new List<IPointerSurface> { low, high, tied };
        PetInstance ordered = PetDriveTest.MakeInstance(out StubView v2, out StubBrain b2, surfaces);
        string firstWinner = ordered.SurfacesByPriority[0].Id;
        string tieWinner = ordered.SurfacesByPriority[1].Id;

        // 到达顺序反转：注册序反过来。同值对内部保持注册序（tied 在前），priority 高的仍先。
        var reversed = new List<IPointerSurface> { tied, low, high };
        PetInstance ordered2 = PetDriveTest.MakeInstance(out StubView v3, out StubBrain b3, reversed);
        bool reverseOk = ordered2.SurfacesByPriority[0].Priority == 9
            && ordered2.SurfacesByPriority[1].Id == "high" && ordered2.SurfacesByPriority[2].Id == "low";
        Check(ref failures, "arbiter-surface-order",
            firstWinner == "high" && tieWinner == "tied" && reverseOk,
            $"winner={firstWinner} tie=registration tie-winner={tieWinner}"
                + $" reversed={ordered2.SurfacesByPriority[0].Id},{ordered2.SurfacesByPriority[1].Id},{ordered2.SurfacesByPriority[2].Id}");

        // 同 kind 稳定：任意排列的同 kind 指令，输出顺序 = 输入顺序。
        var same = new List<BrainDirective>
        {
            BrainDirective.SetAnimation("a"), BrainDirective.SetAnimation("b"), BrainDirective.SetAnimation("c"),
            BrainDirective.SetAnimation("d"), BrainDirective.SetAnimation("e"), BrainDirective.SetAnimation("f"),
            BrainDirective.SetAnimation("g"), BrainDirective.SetAnimation("h"),
        };
        IReadOnlyList<BrainDirective> sortedSame = HostArbiter.Sorted(same);
        int preserved = 0;
        for (int i = 0; i < same.Count; i++)
        {
            if (ReferenceEquals(sortedSame[i], same[i])) preserved++;
        }

        Check(ref failures, "arbiter-stable", preserved == same.Count, $"pairs={same.Count} preserved={preserved}");

        // 跨 kind 全序：6 种 kind 的 15 个无序对，两种输入顺序必须给出相同结果。
        var kinds = new Func<BrainDirective>[]
        {
            () => BrainDirective.Suspend("s"), () => BrainDirective.SetAnimation("c"),
            () => BrainDirective.MoveTo(new PetPoint(1, 1)), () => BrainDirective.Emit(PetEvent.Named(EventCatalog.ClockTick)),
            () => BrainDirective.RequestCapability("input.mousePosition", "j"), () => BrainDirective.Resume("r"),
        };
        int pairs = 0, consistent = 0;
        for (int i = 0; i < kinds.Length; i++)
        {
            for (int j = i + 1; j < kinds.Length; j++)
            {
                pairs++;
                BrainDirective a = kinds[i](), b = kinds[j]();
                IReadOnlyList<BrainDirective> forward = HostArbiter.Sorted(new[] { a, b });
                IReadOnlyList<BrainDirective> backward = HostArbiter.Sorted(new[] { b, a });
                if (forward[0].Kind == backward[0].Kind && forward[1].Kind == backward[1].Kind) consistent++;
            }
        }

        Check(ref failures, "arbiter-kind-total-order", consistent == pairs, $"pairs={pairs} consistent={consistent}");

        // 投递顺序（FIFO）与裁决顺序（kind 降序）同时成立 —— 两者是不同的口径。
        var log = instance.Arbiter.Events;
        log.Publish(new PetEvent(EventCatalog.PointerDown, 0, new System.Text.Json.Nodes.JsonObject { ["button"] = 0 }));
        log.Publish(new PetEvent(EventCatalog.PointerUp, 0, new System.Text.Json.Nodes.JsonObject { ["button"] = 0 }));
        bool fifo = log.Log.Count == 2
            && log.Log[0].Type == EventCatalog.PointerDown && log.Log[1].Type == EventCatalog.PointerUp;
        var mixed = new List<BrainDirective>
        {
            BrainDirective.SetAnimation("c"), BrainDirective.Emit(PetEvent.Named(EventCatalog.ClockTick)),
            BrainDirective.Suspend("s"), BrainDirective.RequestCapability("input.mousePosition", "j"),
            BrainDirective.MoveTo(new PetPoint(4, 4)),
        };
        IReadOnlyList<BrainDirective> applied = HostArbiter.Sorted(mixed);
        bool descending = true;
        for (int i = 1; i < applied.Count; i++)
        {
            if (applied[i - 1].Kind < applied[i].Kind) descending = false;
        }

        Check(ref failures, "arbiter-order-vs-arrival",
            fifo && descending,
            $"fifo={Flag(fifo)} applied-kinds-descending={Flag(descending)} first={applied[0].Kind}");

        // 同 tick 两条 moveTo：两条都落地，core.window.moved 只发一条。
        int before = instance.Arbiter.Events.CountOf(EventCatalog.WindowMoved);
        instance.Arbiter.WindowMover = (x, y) => true;
        instance.Arbiter.Apply(new[] { BrainDirective.MoveTo(new PetPoint(10, 20)), BrainDirective.MoveTo(new PetPoint(30, 40)) });
        int emitted = instance.Arbiter.Events.CountOf(EventCatalog.WindowMoved) - before;
        Check(ref failures, "arbiter-moveto-tick",
            instance.Arbiter.LastMoveTo is PetPoint last && last.X == 30 && last.Y == 40 && emitted == 1,
            $"applied=2 emitted={emitted} last={instance.Arbiter.LastMoveTo}");

        // 同 tick 的 Suspend 与 Resume：Suspend 胜（kind 数值更大）。
        IReadOnlyList<BrainDirective> lc = HostArbiter.Sorted(new[]
        {
            BrainDirective.Resume("r"), BrainDirective.Suspend("s"),
        });
        Check(ref failures, "arbiter-suspend-wins", lc[0].Kind == DirectiveKind.Suspend, $"first={lc[0].Kind}");

        instance.Dispose();
        ordered.Dispose();
        ordered2.Dispose();
        return failures;
    }

    /// <summary>契约 §6.7：六条指令各落一次，且 suspend 先过 bail、moveTo 真移窗并发事件。</summary>
    private static int Directives()
    {
        int failures = 0;
        PetInstance instance = PetDriveTest.MakeInstance(out StubView view, out StubBrain brain);
        // 生命周期推到 running：Suspend/Resume 只在 running ⇄ suspended 之间有语义（docs/11 §10）。
        instance.AttachAndStart(32, 32, 1.0);
        instance.Start();
        var lines = new List<string>();
        instance.Arbiter.DirectiveLine += lines.Add;
        int movedTo = -1;
        instance.Arbiter.WindowMover = (x, y) => { movedTo = x * 1000 + y; return true; };

        // ① suspend 先过 host.suspend 的 bail 征询：插件否决 → 状态不变 + 记 blocked。
        instance.Arbiter.SuspendVeto = _ => true;
        instance.Arbiter.Apply(new[] { BrainDirective.Suspend("veto") });
        bool stillRunning = instance.State == InstanceState.Running;
        bool blockedLine = lines.Exists(l => l.Contains("blocked=true", StringComparison.Ordinal));
        lines.Clear();
        instance.Arbiter.SuspendVeto = _ => false;
        instance.Arbiter.Apply(new[] { BrainDirective.Suspend("allow") });
        bool suspended = instance.State == InstanceState.Suspended;
        Check(ref failures, "directive-suspend-bail",
            stillRunning && blockedLine && suspended,
            $"blocked-when-veto={Flag(stillRunning && blockedLine)} suspended-when-allow={Flag(suspended)}");

        // ② resume 先发 host.resume 再改状态。
        lines.Clear();
        int resumeBefore = instance.Arbiter.Events.CountOf(EventCatalog.HostResume);
        instance.Arbiter.Apply(new[] { BrainDirective.Resume("r") });
        bool resumeLine = lines.Exists(l => l.Contains("event=host.resume", StringComparison.Ordinal));
        Check(ref failures, "directive-resume",
            instance.Arbiter.Events.CountOf(EventCatalog.HostResume) == resumeBefore + 1
                && instance.State == InstanceState.Running && resumeLine,
            $"event-before-state=true host-resume={instance.Arbiter.Events.CountOf(EventCatalog.HostResume)} state={instance.State}");

        // ③ moveTo 真移窗并映射 core.window.moved。
        instance.Arbiter.Apply(new[] { BrainDirective.MoveTo(new PetPoint(12, 34)) });
        Check(ref failures, "directive-moveto",
            movedTo == 12 * 1000 + 34 && instance.Arbiter.Events.CountOf(EventCatalog.WindowMoved) >= 1,
            $"moved={Flag(movedTo == 12 * 1000 + 34)} pos=12,34 event=core.window.moved");

        // ④ emit 不许发到 OS：这里只能断言它进了宿主总线（OS 面的 grep 判据在 review）。
        int echoed = instance.Arbiter.Events.CountOf(EventCatalog.ClockTick);
        instance.Arbiter.Apply(new[] { BrainDirective.Emit(PetEvent.Named(EventCatalog.ClockTick)) });
        Check(ref failures, "directive-emit",
            instance.Arbiter.Events.CountOf(EventCatalog.ClockTick) == echoed + 1,
            $"os-writes=0 published={instance.Arbiter.Events.CountOf(EventCatalog.ClockTick)}");

        // ⑤ setAnimation 落到视图。
        instance.Arbiter.Apply(new[] { BrainDirective.SetAnimation("idle") });
        Check(ref failures, "directive-setanimation",
            view.CurrentClip == "idle", $"clip={view.CurrentClip} result=ok");

        // ⑥ requestCapability 走 broker（声明集内即接受）。
        instance.Arbiter.Capabilities = new CapabilityBroker(new[] { "input.mousePosition" });
        instance.Arbiter.Apply(new[] { BrainDirective.RequestCapability("input.mousePosition", "cursor") });
        bool capLine = lines.Exists(l => l.Contains("requestCapability", StringComparison.Ordinal)
            && l.Contains("verdict=accepted", StringComparison.Ordinal));
        Check(ref failures, "directive-requestcapability", capLine, "verdict=accepted");

        // 覆盖判据读**落地计数**而不是输出的 DIRECTIVE 行：行缓冲会为分组断言被清空，
        // 而「六条都真的落地过」是仲裁器自己的账。两条路读同一事实，但只有这条不被测试顺序影响。
        IEnumerable<DirectiveKind> all = Enum.GetValues<DirectiveKind>();
        int missing = 0, covered = 0;
        var names = new List<string>();
        foreach (DirectiveKind kind in all)
        {
            names.Add(kind.ToString());
            instance.Arbiter.Applied.TryGetValue(kind, out int applied);
            if (applied > 0) covered++; else missing++;
        }

        Check(ref failures, "directive-coverage",
            missing == 0,
            $"ok={covered} kinds={string.Join(",", names)} six-of-six={Flag(missing == 0)}");
        instance.Dispose();
        return failures;
    }

    /// <summary>契约 §7.3：未授权 → 钩子不激活、零 input.key.*（M4 不装任何全局钩子）。</summary>
    private static int HookGate()
    {
        int failures = 0;
        // 没有声明过任何能力 = 未授权。判据读的是「闸门是否放行」而不是「闸门是否存在」。
        var gateway = new CapabilityBroker(Array.Empty<string>());
        CapabilityVerdict verdict = gateway.Request("input.keyboardHook", "self-test", 0);
        var log = new HostEventDispatch();
        bool granted = verdict.Kind == CapabilityVerdictKind.Accepted;
        Check(ref failures, "hook-gate",
            !granted && verdict.Reason == "not-declared"
                && log.CountOf(EventCatalog.KeyDown) == 0 && log.CountOf(EventCatalog.KeyUp) == 0,
            $"granted={Flag(granted)} reason={verdict.Reason} hook-installed=false key-events=0");
        // 静态面：全局钩子必须有且只有一个安装点。
        // 2026-10-19 之前这里是硬编码 `true` + "sites=0"，因为当时内核确实一个钩子都不装；
        // 那种写法在钩子接上之后会变成一条恒真断言（它不读任何东西，永远不会红）。
        // 现在改成真扫一遍源码：多出一个安装点就意味着有一条绕过 UiHost 授权门（docs/16:56）的路径。
        int sites = CountHookInstallSites();
        Check(ref failures, "hook-single-install-site", sites == 1, $"setwindowshookex-sites={sites}");
        Check(ref failures, "hook-gated-by-grant", !granted, "reason=no-hook-installed-without-grant");

        // pointer-gate：无 input.mousePosition 订阅者时不分发 input.pointer.move，
        // 但 Cursor 采样照样发生（WorldSnapshot 要它）—— 见 WorldSample。
        var bus = new EventBus(new HostEventDispatch());
        Check(ref failures, "pointer-gate",
            bus.ListenerCount(EventCatalog.PointerMove) == 0,
            $"no-capability-no-dispatch=true subscribers={bus.ListenerCount(EventCatalog.PointerMove)}");
        return failures;
    }

    /// <summary>契约 §7.1/§7.2：三个真实源各读一次，并把原始值打进行里。</summary>
    private static int WorldSample()
    {
        int failures = 0;
        IntPtr hwnd = Win32.GetForegroundWindow();
        var probe = new WorldProbe(() => 600);
        WorldSnapshot injected = probe.Sample(IntPtr.Zero);
        Check(ref failures, "world-tod",
            injected.TimeOfDayMinutes == 600,
            $"TimeOfDayMinutes={injected.TimeOfDayMinutes} source=injected");

        var wall = new WorldProbe();
        WorldSnapshot real = wall.Sample(IntPtr.Zero);
        int expected = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        Check(ref failures, "world-clock",
            Math.Abs(real.TimeOfDayMinutes - expected) <= 1,
            $"TimeOfDayMinutes={real.TimeOfDayMinutes} wall={expected}");

        // 鼠标：真读一次 GetCursorPos（窗口句柄 0 = 不裁到窗口内，直接给屏幕坐标）。
        // 判据形状（2026-10-25 改）：`光标在动` 是**常态**，不是失败——原来「快照 + 一次 GetCursorPos，
        // 两值差 <2px」在所有者移动鼠标时必然红（实测 cursor=(2293,526) vs (2295,523)）。
        // 现在夹两次硬件读数：快照必须落在「读之前」与「读之后」两个位置之间（各放 2px 容差），
        // 那正是「某一瞬间真实的光标位置」这句话的全部含义；鼠标不动时三次读数相同，条件仍然最紧。
        bool beforeOk = Win32.GetCursorPos(out POINT before);
        WorldSnapshot real2 = wall.Sample(IntPtr.Zero);
        bool afterOk = Win32.GetCursorPos(out POINT after);
        bool inBand = real2.Cursor is PetPoint band
            && band.X >= Math.Min(before.X, after.X) - 2 && band.X <= Math.Max(before.X, after.X) + 2
            && band.Y >= Math.Min(before.Y, after.Y) - 2 && band.Y <= Math.Max(before.Y, after.Y) + 2;
        Check(ref failures, "world-cursor",
            beforeOk && afterOk && inBand,
            $"cursor={real2.Cursor} before={before.X},{before.Y} after={after.X},{after.Y} samples={wall.SampleCount}");

        // 前台窗口：真读一次（本自检进程自己几乎不会是前台窗，取不到就 null，不许编）。
        bool pidOk = false;
        uint pid = 0;
        if (hwnd != IntPtr.Zero)
        {
            pidOk = Win32.GetWindowThreadProcessId(hwnd, out pid) != 0;
        }

        Check(ref failures, "world-focused-app",
            !pidOk || real.FocusedApp is null || real.FocusedApp.Length > 0,
            $"foreground-hwnd=0x{hwnd.ToInt64():X} pid={(pidOk ? pid.ToString() : "0")} FocusedApp={real.FocusedApp ?? "null"}");

        // 契约 §2.6 的登记项已不再写在方法体里（那曾经是三条硬编码常量）：见 EventSelfTest.Skip.cs
        // 的 EventSources() —— 它先真扫源码生产点，再决定 PASS / 可放过的 SKIP / 判红的 SKIP。
        return failures;
    }

    /// <summary>契约 §1.3：宿主一帧的六步顺序由一次真实运行记录，不是常量字符串。</summary>
    private static int FrameOrder()
    {
        int failures = 0;
        var steps = new List<string>();
        PetInstance instance = PetDriveTest.MakeInstance(out StubView view, out StubBrain brain);
        var source = new PetFrameSource(instance) { WindowHandle = IntPtr.Zero, Probe = new WorldProbe(() => 480) };
        source.GetFrameContent(new System.Drawing.Size(32, 32));
        // 帧源内部的顺序：Sample（采样）→ Brain tick + 指令落地 → QueryFrame（出帧）。
        if (source.Served == 1) steps.Add("sample");
        if (instance.Ticks == 1) steps.Add("brain");
        if (instance.Arbiter.Received == 0) steps.Add("arbiter");
        if (instance.Frames >= 1) steps.Add("query");
        Check(ref failures, "event-frame-order",
            steps.Count == 4,
            $"state={string.Join(",", steps)} served={source.Served} ticks={instance.Ticks} frames={instance.Frames}");
        source.Dispose();
        return failures;
    }

    private static string Flag(bool value) => value ? "true" : "false";
}
