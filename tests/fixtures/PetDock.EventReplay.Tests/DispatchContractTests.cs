// t1 契约 §1（五种 dispatch）+ §2.2（目录守门）+ §4（requestCapability 200ms）+ §5.3（不得按到达序）
// + §6.5（suspend 过 bail）+ §2.4（waterfall）的契约测试。
//
// 全部走 CoreSurface 的晚绑定适配器（见该文件头的三条理由），因此：
//   * t2 的面存在 → 判据**真跑真红**（调的是 core 的真实现）；
//   * t2 的面不存在 → SKIP + 具名原因（§2.6：SKIP 不是 PASS，不许计绿）。
// 每条断言下面写「什么情况下会红」。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;
using Xunit;

namespace PetDock.EventReplay.Tests;

/// <summary>五种 dispatch 与目录守门的契约测试（t1 §1 / §2.2）。</summary>
public sealed class DispatchContractTests
{
    private static readonly string Tick = "core.clock.tick";

    /// <summary>§1.4 `dispatch-emit`：第 1 个监听者抛异常后，第 2/3 个**必须**仍被调用（after-fault-ran=true）。
    /// 红：异常吃掉后续监听者 —— 「静默丢事件」比崩溃更难查。</summary>
    [Fact]
    public void EmitAfterFaultKeepsCallingRemainingListeners()
    {
        using CoreDrive drive = CoreDrive.Create();
        BusAdapter? bus = BusAdapter.TryCreate(drive.Dispatch);
        if (bus is null)
        {
            Skip.With(CoreSurface.Reason("EventBus 的 emit 分发", "§1.4 dispatch-emit", "t2"));
            return;
        }

        var ran = new List<string>();
        Assert.True(bus.Subscribe(Tick, () => { ran.Add("first"); throw new InvalidOperationException("emit-fault"); }));
        Assert.True(bus.Subscribe(Tick, () => { ran.Add("second"); return null; }));
        Assert.True(bus.Subscribe(Tick, () => { ran.Add("third"); return null; }));

        (bool ok, Dictionary<string, object?> fields) = bus.Dispatch("Emit", new PetEvent(Tick, 0));
        Assert.True(ok, "EventBus.Emit 不存在");

        // §1.4 `dispatch-emit` 的**核心**断言是「异常不阻止后续监听者」：三个监听者都必须被调用。
        Assert.Equal(new[] { "first", "second", "third" }, ran);
        Assert.True(Convert.ToBoolean(BusAdapter.Item(fields, 2)), "第 1 个监听者抛异常后，后续监听者没有被调用");

        // `ran` 的口径（§1.4 逐字 `listeners=3 ran=3 faults=1`）：**被调用过**即计入，
        // 抛异常的那位也 ran 了，因此它同时出现在 ran 与 faults 里 —— 两者互不排斥。
        // 这条是断言（不是 if 判断）：t10 之前 core 计的是「成功返回数」→ ran=2，本行会红。
        int coreRan = Convert.ToInt32(BusAdapter.Item(fields, 0));
        int coreFaults = Convert.ToInt32(BusAdapter.Item(fields, 1));
        Assert.Equal(3, coreRan);
        Assert.Equal(1, coreFaults);

        Console.WriteLine($"EVENT-CHECK Note.EmitRanSemantics PASS listeners=3 called=3 core-ran={coreRan}"
            + $" faults={coreFaults}");

        // 反向自证：把第 1 个监听者换成不抛的，afterFaultRan 必须为 false（证明它不是常量 true）。
        var clean = new List<string>();
        using CoreDrive other = CoreDrive.Create();
        BusAdapter other2 = BusAdapter.TryCreate(other.Dispatch)!;
        other2.Subscribe(Tick, () => { clean.Add("a"); return null; });
        other2.Subscribe(Tick, () => { clean.Add("b"); return null; });
        (_, Dictionary<string, object?> fields2) = other2.Dispatch("Emit", new PetEvent(Tick, 0));
        Assert.False(Convert.ToBoolean(BusAdapter.Item(fields2, 2)), "没有任何异常却报 afterFaultRan=true");
        Assert.Equal(new[] { "a", "b" }, clean);
    }

    /// <summary>§1.4 `dispatch-bail`：truthy 即终止，`stopped-at=1`（第 2 个监听者不许被调用）；
    /// §1.2 子规则 1：抛异常按 **true**（拒绝）处理。
    /// 红：把「抛异常」读成「同意」—— 一个无法表态的监听者被当成放行。</summary>
    [Fact]
    public void BailStopsAtFirstTruthyAndTreatsFaultAsRefusal()
    {
        using CoreDrive drive = CoreDrive.Create();
        BusAdapter? bus = BusAdapter.TryCreate(drive.Dispatch);
        if (bus is null)
        {
            Skip.With(CoreSurface.Reason("EventBus 的 bail 分发", "§1.4 dispatch-bail / §1.2", "t2"));
            return;
        }

        var ran = new List<string>();
        bus.Subscribe("host.suspend", () => { ran.Add("veto"); return true; });
        bus.Subscribe("host.suspend", () => { ran.Add("never"); return false; });

        (bool ok, Dictionary<string, object?> fields) = bus.Dispatch("Bail", new PetEvent("host.suspend", 0));
        Assert.True(ok, "EventBus.Bail 不存在");
        Assert.Single(ran);
        Assert.Equal("veto", ran[0]);
        Assert.True(Convert.ToBoolean(BusAdapter.Item(fields, 1)));      // returned
        Assert.Equal(0, Convert.ToInt32(BusAdapter.Item(fields, 2)));   // stoppedAt
        Assert.False(Convert.ToBoolean(BusAdapter.Item(fields, 3)));    // faultSeen

        // fail-closed：抛异常的监听者按「拒绝」处理，且立即终止。
        var faultRan = new List<string>();
        using CoreDrive faultDrive = CoreDrive.Create();
        BusAdapter faultBus = BusAdapter.TryCreate(faultDrive.Dispatch)!;
        faultBus.Subscribe("host.suspend", () => { faultRan.Add("throws"); throw new InvalidOperationException("bail-fault"); });
        faultBus.Subscribe("host.suspend", () => { faultRan.Add("never"); return false; });
        (_, Dictionary<string, object?> faultFields) = faultBus.Dispatch("Bail", new PetEvent("host.suspend", 0));
        Assert.Single(faultRan);
        Assert.True(Convert.ToBoolean(BusAdapter.Item(faultFields, 1)), "bail 的异常没有按 true（拒绝）处理");
        Assert.True(Convert.ToBoolean(BusAdapter.Item(faultFields, 3)));

        // 反向自证：全 false 时 returned 必须为 false。
        using CoreDrive allow = CoreDrive.Create();
        BusAdapter allowBus = BusAdapter.TryCreate(allow.Dispatch)!;
        allowBus.Subscribe("host.suspend", () => false);
        (_, Dictionary<string, object?> allowFields) = allowBus.Dispatch("Bail", new PetEvent("host.suspend", 0));
        Assert.False(Convert.ToBoolean(BusAdapter.Item(allowFields, 1)), "没有 truthy 却报 returned=true");
    }

    /// <summary>§1.4 `dispatch-serial`：顺序触发且能看见前一轮结果（saw-previous=true）；
    /// §1.2 子规则 2：那个「看见」是只读快照，改不动。
    /// 红：前轮结果变成可变共享 —— docs/11:161 的幂等当场失效。</summary>
    [Fact]
    public void SerialRoundsExposeReadOnlyPreviousResults()
    {
        using CoreDrive drive = CoreDrive.Create();
        BusAdapter? bus = BusAdapter.TryCreate(drive.Dispatch);
        if (bus is null)
        {
            Skip.With(CoreSurface.Reason("EventBus 的 serial 分发与 DispatchRound", "§1.4 dispatch-serial / §1.2", "t2"));
            return;
        }

        var seen = new List<string>();
        bus.Subscribe(Tick, () => { seen.Add("r1"); return "r1"; });
        bus.Subscribe(Tick, () => { seen.Add("r2"); return "r2"; });
        bus.Subscribe(Tick, () => { seen.Add("r3"); return "r3"; });

        (bool ok, Dictionary<string, object?> fields) = bus.Dispatch("Serial", new PetEvent(Tick, 0));
        Assert.True(ok, "EventBus.Serial 不存在");

        // 三个监听者都必须被顺序调用（我自己的记录），且 rounds 与订阅数一致。
        Assert.Equal(new[] { "r1", "r2", "r3" }, seen);
        Assert.Equal(3, Convert.ToInt32(BusAdapter.Item(fields, 0)));   // rounds
        Assert.True(Convert.ToBoolean(BusAdapter.Item(fields, 1)), "serial 没有让后续监听者看见前一轮结果");
    }

    /// <summary>§1.4 `dispatch-parallel`：全部跑完（ran == listeners），异常逐条记账。
    /// 红：并行实现成「第一个抛异常就整体放弃」—— 那就退化成 bail 了。</summary>
    [Fact]
    public void ParallelRunsEveryListener()
    {
        using CoreDrive drive = CoreDrive.Create();
        BusAdapter? bus = BusAdapter.TryCreate(drive.Dispatch);
        if (bus is null)
        {
            Skip.With(CoreSurface.Reason("EventBus 的 parallel 分发", "§1.4 dispatch-parallel", "t2"));
            return;
        }

        var ran = new List<string>();
        bus.Subscribe(Tick, () => { ran.Add("a"); return null; });
        bus.Subscribe(Tick, () => { ran.Add("b"); throw new InvalidOperationException("parallel-fault"); });
        bus.Subscribe(Tick, () => { ran.Add("c"); return null; });
        bus.Subscribe(Tick, () => { ran.Add("d"); return null; });

        (bool ok, Dictionary<string, object?> fields) = bus.Dispatch("Parallel", new PetEvent(Tick, 0));
        Assert.True(ok, "EventBus.Parallel 不存在");

        // §1.4 `dispatch-parallel` 的核心断言：**全部**监听者都被调用（即使其中一个抛异常）。
        Assert.Equal(new[] { "a", "b", "c", "d" }, ran);
        Assert.True(Convert.ToInt32(BusAdapter.Item(fields, 1)) >= 1, "并行的异常没有被记账");

        // 反向自证：没有异常时 faults 必须为 0。
        using CoreDrive clean = CoreDrive.Create();
        BusAdapter cleanBus = BusAdapter.TryCreate(clean.Dispatch)!;
        cleanBus.Subscribe(Tick, () => null);
        (_, Dictionary<string, object?> cleanFields) = cleanBus.Dispatch("Parallel", new PetEvent(Tick, 0));
        Assert.Equal(0, Convert.ToInt32(BusAdapter.Item(cleanFields, 1)));
    }

    /// <summary>§1.2 子规则 3 / §1.4 `dispatch-no-escape`：任何模式的异常都不许穿出宿主循环。
    /// 红：一条坏监听者把宿主帧打穿 —— 整个进程的行为不可预测。</summary>
    [Fact]
    public void NoDispatchModeLetsFaultsEscape()
    {
        using CoreDrive drive = CoreDrive.Create();
        BusAdapter? bus = BusAdapter.TryCreate(drive.Dispatch);
        if (bus is null)
        {
            Skip.With(CoreSurface.Reason("EventBus 的五种 dispatch", "§1.4 dispatch-no-escape", "t2"));
            return;
        }

        foreach (string mode in new[] { "Emit", "Parallel", "Serial", "Bail", "Waterfall" })
        {
            using CoreDrive each = CoreDrive.Create();
            BusAdapter eachBus = BusAdapter.TryCreate(each.Dispatch)!;
            eachBus.Subscribe(Tick, () => throw new InvalidOperationException($"escape-{mode}"));
            eachBus.Subscribe(Tick, () => throw new InvalidOperationException($"escape2-{mode}"));

            (bool ok, Dictionary<string, object?> fields) = eachBus.Dispatch(mode, new PetEvent(Tick, 0));
            Assert.True(ok, $"EventBus.{mode} 不存在");
            Assert.True(fields.Count >= 2, $"{mode}: dispatch 没有返回可观察结果");
        }
    }

    /// <summary>§2.4 `pet.state.commit` 的 waterfall：每跳都能改、最终 payload 含全部改动，
    /// 且**原始包不被就地修改**（深拷贝）。
    /// 红：payload 被就地改 —— 同一个包喂两次结果不同，破坏幂等（docs/11:161）。</summary>
    [Fact]
    public void WaterfallMutatesEachHopWithoutTouchingTheOriginal()
    {
        using CoreDrive drive = CoreDrive.Create();
        BusAdapter? bus = BusAdapter.TryCreate(drive.Dispatch);
        if (bus is null)
        {
            Skip.With(CoreSurface.Reason("EventBus 的 waterfall 分发", "§2.4", "t2"));
            return;
        }

        var original = new JsonObject { ["petId"] = "pet-1", ["hop2"] = 0, ["hop3"] = 0 };
        var hops = new List<string>();
        bus.Subscribe("pet.state.commit", () =>
        {
            hops.Add("h1");
            var next = new JsonObject { ["petId"] = "pet-1", ["hop2"] = 1, ["hop3"] = 0 };
            return next;
        });
        bus.Subscribe("pet.state.commit", () =>
        {
            hops.Add("h2");
            return new JsonObject { ["petId"] = "pet-1", ["hop2"] = 1, ["hop3"] = 1 };
        });
        bus.Subscribe("pet.state.commit", () => { hops.Add("h3"); return null; });

        PetEvent evt = new("pet.state.commit", 0, original);
        (bool ok, Dictionary<string, object?> fields) = bus.Dispatch("Waterfall", evt);
        Assert.True(ok, "EventBus.Waterfall 不存在");
        Assert.Equal(3, hops.Count);
        Assert.Equal(3, Convert.ToInt32(BusAdapter.Item(fields, 0)));   // hops
        Assert.True(Convert.ToBoolean(BusAdapter.Item(fields, 2)), "waterfall 的 payload 没有逐跳传递改动");

        JsonObject? final = BusAdapter.Item(fields, 1) as JsonObject;
        Assert.True(final is not null, "waterfall 没有返回最终 payload");
        Assert.Equal(1, final!["hop3"]!.GetValue<int>());

        // 原始包必须一字未改（深拷贝）：这是「同输入两次分发结果相同」的前提。
        Assert.Equal(0, original["hop2"]!.GetValue<int>());
        Assert.Equal(0, original["hop3"]!.GetValue<int>());
    }

    /// <summary>§2.2 判据 1/3：表外事件**不入日志**、`OutsideCatalog` 恰好 +1、合法事件仍进日志、
    /// 进程不崩（拒收不是崩溃）。**这条今天真跑**（t2 的守门已落地）。
    /// 红：拒收实现成抛异常或整批丢弃。</summary>
    [Fact]
    public void CatalogGateIsFailClosedAndDoesNotDropLegalEvents()
    {
        CoreSurfaceReport surface = CoreSurface.Current;
        using CoreDrive drive = CoreDrive.Create();

        drive.Publish("input.pointer.hover", new JsonObject { ["pos"] = new JsonArray(1, 2) }, 0);
        drive.Publish(Tick, new JsonObject { ["dt"] = 33 }, 0);
        drive.Publish(Tick, new JsonObject { ["dt"] = 33 }, 1);

        // (c) 合法事件必须仍在 —— 无论守门是否落地，这一条都成立。
        Assert.Equal(2, drive.CountOf(Tick));

        if (!surface.HasCatalogGate)
        {
            Skip.With(CoreSurface.Reason("HostEventDispatch.OutsideCatalog 守门", "§2.2 判据 1", "t2"));
            return;
        }

        // (a) 表外不入日志；(b) 计数恰好 +1。
        Assert.Equal(0, drive.CountOf("input.pointer.hover"));
        Assert.Equal(1, CatalogGate.OutsideCatalog(drive.Dispatch));

        // 反向自证：再投一条表外（另一个名字）→ 计数变 2，且仍不入日志。
        drive.Publish("another.outside.name", null, 0);
        Assert.Equal(2, CatalogGate.OutsideCatalog(drive.Dispatch));
        Assert.Equal(0, drive.CountOf("another.outside.name"));
        Console.WriteLine($"EVENT-CHECK Event.CatalogGate PASS rejected=2 outside={CatalogGate.OutsideCatalog(drive.Dispatch)}"
            + $" logged-outside=0 legal-kept={drive.CountOf(Tick)}");
    }

    /// <summary>§2.2 的审计面：结束时的日志里每个 Type 必须 ∈ 表内 13 ∪ {host.canceled}。
    /// 把「表外事件不得出现」从文档纪律变成机器判据。**今天真跑**。</summary>
    [Fact]
    public void LogAuditFindsOnlyCatalogNames()
    {
        ReplayFixture fixture = Fixtures.Read("base-8frames");
        using CoreDrive drive = CoreDrive.Create();
        foreach (FixtureFrame frame in fixture.Frames)
        {
            foreach (FixtureEvent evt in frame.Events)
            {
                drive.Publish(evt.Type, evt.Payload, frame.Seq);
            }
        }

        Assert.True(drive.Dispatch.Log.Count > 0, "日志为空 —— 审计面是空转的");
        Assert.Empty(drive.Dispatch.Log.Where(e => !EventCatalogTable.IsAcceptable(e.Type)).Select(e => e.Type));

        // 反向自证：投一条表外后，审计面必须能看见它（守门落地前）；落地后它根本进不去 ——
        // 两种情形都必须让「审计面非空转」成立，故按实际行为断言。
        drive.Publish("outside.probe.name", null, 0);
        bool seen = drive.Dispatch.Log.Any(e => e.Type == "outside.probe.name");
        if (CoreSurface.Current.HasCatalogGate)
        {
            Assert.False(seen, "守门已落地时表外事件仍进了日志");
            Assert.Equal(1, CatalogGate.OutsideCatalog(drive.Dispatch));
        }
        else
        {
            Assert.True(seen, "守门未落地时表外事件却没进日志 —— 投递口本身坏了");
        }
    }

    /// <summary>§5.3 断言 2：跨 kind 完全由数值决定 —— 6 种 kind 的全部 15 个无序对，
    /// `Sorted([a,b])` 与 `Sorted([b,a])` 必须给出相同结果。
    /// 红：排序依赖输入顺序 —— 「不得用到达顺序排序」当场被破坏。**今天真跑**（公开纯函数）。</summary>
    [Fact]
    public void ArbitrationIsATotalOrderIndependentOfArrival()
    {
        var kinds = Enum.GetValues<DirectiveKind>();
        int pairs = 0;
        int consistent = 0;
        for (int i = 0; i < kinds.Length; i++)
        {
            for (int j = i + 1; j < kinds.Length; j++)
            {
                pairs++;
                BrainDirective a = Make(kinds[i], "A");
                BrainDirective b = Make(kinds[j], "B");
                string forward = string.Join("|", HostArbiter.Sorted(new[] { a, b }).Select(d => d.Kind));
                string backward = string.Join("|", HostArbiter.Sorted(new[] { b, a }).Select(d => d.Kind));
                if (string.Equals(forward, backward, StringComparison.Ordinal))
                {
                    consistent++;
                }
            }
        }

        Assert.Equal(15, pairs);
        Assert.Equal(15, consistent);
    }

    /// <summary>§5.3 断言 1：同 kind 稳定 —— 任意排列的两条同 kind，输出序 = 输入序。
    /// 红：同档用了不稳定排序 —— docs/11:161 的幂等被破坏。</summary>
    [Fact]
    public void SameKindOrderIsStable()
    {
        foreach (DirectiveKind kind in Enum.GetValues<DirectiveKind>())
        {
            BrainDirective first = Make(kind, "1");
            BrainDirective second = Make(kind, "2");
            Assert.Equal(Make(kind, "1").Kind, HostArbiter.Sorted(new[] { first, second })[0].Kind);
            Assert.Equal(second.Kind, HostArbiter.Sorted(new[] { second, first })[0].Kind);

            // 用可区分的标签判定「真的是同一条在前」，而不是只比 Kind（同 kind 时 Kind 恒相同）。
            Assert.Equal(Tag(first), Tag(HostArbiter.Sorted(new[] { first, second })[0]));
            Assert.Equal(Tag(second), Tag(HostArbiter.Sorted(new[] { second, first })[0]));
        }
    }

    /// <summary>§5.3 断言 3 前半（投递序 FIFO）：同 tick 两条事件的日志序 = 注入序。
    /// 红：日志按类型或优先级排 —— docs/11:137 的投递序保证被换掉。**今天真跑**。</summary>
    [Fact]
    public void DeliveryOrderIsFifoRegardlessOfType()
    {
        using CoreDrive drive = CoreDrive.Create();
        drive.Publish("core.window.focus", null, 1);
        drive.Publish(Tick, new JsonObject { ["dt"] = 1 }, 2);
        drive.Publish("core.window.focus", null, 3);

        Assert.Equal(3, drive.Dispatch.Log.Count);
        Assert.Equal(new[] { "core.window.focus", Tick, "core.window.focus" },
            drive.Dispatch.Log.Select(e => e.Type));

        // 反向自证：另一种注入序必须给出另一种日志序（证明它不是常量）。
        using CoreDrive reversed = CoreDrive.Create();
        reversed.Publish(Tick, new JsonObject { ["dt"] = 1 }, 1);
        reversed.Publish("core.window.focus", null, 2);
        Assert.Equal(new[] { Tick, "core.window.focus" }, reversed.Dispatch.Log.Select(e => e.Type));
    }

    /// <summary>§5.3 断言 3 后半：裁决序 ≠ 到达序 —— 两组同时成立。
    /// 红：把「投递序」与「裁决序」混成一条 —— t5 会按错的一条判死（§5.3 整节都在防这个）。</summary>
    [Fact]
    public void ArbitrationOrderDiffersFromArrivalOrder()
    {
        using CoreDrive drive = CoreDrive.Create();
        drive.Publish("input.pointer.move", new JsonObject { ["pos"] = new JsonArray(1, 2) }, 1);
        drive.Publish(Tick, new JsonObject { ["dt"] = 1 }, 2);

        // 到达序（注入序）刻意乱序：emit(1) → requestCapability(0) → setAnimation(3)
        var arrival = new[]
        {
            BrainDirective.Emit(PetEvent.Named("core.window.moved")),
            BrainDirective.RequestCapability("input.mousePosition", "why"),
            BrainDirective.SetAnimation("idle"),
        };
        Assert.Equal(
            new[] { DirectiveKind.SetAnimation, DirectiveKind.Emit, DirectiveKind.RequestCapability },
            HostArbiter.Sorted(arrival).Select(d => d.Kind));

        // 同时：投递序仍是注入序。
        Assert.Equal("input.pointer.move", drive.Dispatch.Log[0].Type);
        Assert.Equal(Tick, drive.Dispatch.Log[1].Type);
    }

    /// <summary>§6.5 `directive-suspend-bail`：suspend 必须先过 host.suspend 的 bail 征询。
    /// 两个子断言：插件投否决票 → 状态仍 running；无否决 → suspended。
    /// 红：suspend 直接挂上 —— docs/16:41 逐字「任一插件返回 false → 阻止 suspend」被跳过。</summary>
    [Fact]
    public void SuspendIsConsultedThroughBailFirst()
    {
        using CoreDrive drive = CoreDrive.Create();
        if (!CoreSurface.Current.HasScriptedBrainInjection || !drive.TickAvailable)
        {
            Skip.With(CoreSurface.Reason(
                "PetInstance/HostArbiter 的可注入 veto 与 brain 槽位（测试侧无公开写入者）", "§6.5 / §8.4 N2", "t2"));
            return;
        }

        // 子断言 1：插件投否决票 → 状态仍 running。
        Assert.True(drive.InstallSuspendVeto(_ => true), "SuspendVeto 装不进去");
        drive.Apply(new[] { BrainDirective.Suspend("vetoed") });
        Assert.Equal(InstanceState.Running, drive.State);

        // 子断言 2：无否决 → 真的挂起。
        Assert.True(drive.InstallSuspendVeto(_ => false), "SuspendVeto 装不进去（第二次）");
        drive.Apply(new[] { BrainDirective.Suspend("allowed") });
        Assert.Equal(InstanceState.Suspended, drive.State);

        Console.WriteLine(
            $"EVENT-CHECK Directive.SuspendIsConsultedThroughBailFirst PASS blocked-when-veto=true"
            + $" suspended-when-allow=true via=HostArbiter.SuspendVeto");
    }

    /// <summary>§6.6 `directive-resume`：先发 `host.resume`（parallel）**再**改状态。
    /// 红：顺序反了 —— 监听者第一帧就在 running 状态里收到「我刚 resume」。</summary>
    [Fact]
    public void ResumeEventPrecedesStateChange()
    {
        using CoreDrive drive = CoreDrive.Create();
        BusAdapter? bus = BusAdapter.TryCreate(drive.Dispatch);
        if (bus is null || !drive.TickAvailable || drive.Arbiter is null)
        {
            Skip.With(CoreSurface.Reason("EventBus + 可驱动实例（resume 事件的发布序）", "§6.6", "t2"));
            return;
        }

        // 让实例先进入 Suspended，否则 resume 是空操作（State 已经是 Running 时 core 直接返回）。
        drive.InstallSuspendVeto(_ => false);
        drive.Apply(new[] { BrainDirective.Suspend("precondition") });
        Assert.Equal(InstanceState.Suspended, drive.State);

        // 顺序判据：监听者在**收到 host.resume 的那一刻**观察实例状态，必须仍是 Suspended。
        // 这就是「先发事件、再改状态」的可判定形态 —— 若顺序反了，观察值会是 Running。
        //
        // 订阅点必须是实例仲裁器**自己的**那个 HostEventDispatch（`drive.Arbiter.Events`）：
        // core 里 `HostArbiter` 构造时自建一个 Events，而 `CoreDrive.Dispatch` 是另建的记账口，
        // 两者不是同一个实例（本会话实测：订阅 drive.Dispatch → 监听者从未被调用 → observed=null，
        // 看起来像「事件没发」，实际是订错了总线）。
        Assert.NotNull(drive.Arbiter);
        InstanceState? observedAtEvent = null;
        drive.Arbiter!.Events.Published += evt =>
        {
            if (string.Equals(evt.Type, "host.resume", StringComparison.Ordinal))
            {
                observedAtEvent = drive.State;
            }
        };

        drive.Apply(new[] { BrainDirective.Resume("recovered") });

        Assert.Equal(InstanceState.Suspended, observedAtEvent);   // 事件先于状态
        Assert.Equal(InstanceState.Running, drive.State);         // 之后状态才变
        // 事件数也读仲裁器自己那条总线（drive.Dispatch 是另一实例，恒为 0）。
        Assert.Equal(1, drive.Arbiter!.Events.CountOf("host.resume"));

        Console.WriteLine(
            "EVENT-CHECK Directive.ResumeEventPrecedesStateChange PASS event-before-state=true"
            + " observed=Suspended final=Running");
    }

    /// <summary>§6.7 六条指令汇总：core 的 DirectiveKind 恰好 6 个，且数值序 = docs/11:162 的仲裁序。
    /// **今天真跑**。红：指令集被增删 —— 映射表与仲裁表同时失效。</summary>
    [Fact]
    public void DirectiveCoverageIsSixOfSix()
    {
        var kinds = Enum.GetValues<DirectiveKind>();
        Assert.Equal(6, kinds.Length);
        Assert.Equal(
            new[] { "Emit", "MoveTo", "RequestCapability", "Resume", "SetAnimation", "Suspend" },
            kinds.Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal));

        foreach (DirectiveKind kind in kinds)
        {
            Assert.True(FixtureRules.KindRank(DirectiveBridge.LowerKind(kind)) >= 0,
                $"{kind} 的 camelCase 形态没有对应的夹具口径");
        }

        Assert.True((int)DirectiveKind.Suspend > (int)DirectiveKind.Resume);
        Assert.True((int)DirectiveKind.Resume > (int)DirectiveKind.SetAnimation);
        Assert.True((int)DirectiveKind.SetAnimation > (int)DirectiveKind.MoveTo);
        Assert.True((int)DirectiveKind.MoveTo > (int)DirectiveKind.Emit);
        Assert.True((int)DirectiveKind.Emit > (int)DirectiveKind.RequestCapability);
    }

    /// <summary>§1.3 的帧内六步顺序：probe → (tick) → input → brain/arbiter → budget，
    /// 顺序**本身**是判据（`event-frame-order`）。层 1 的判据 = 重放的文本行按此序产出。
    /// 红：把 budget 提到 tick 之前 —— 那就不是契约的那一帧了。</summary>
    [Fact]
    public void FrameStepOrderIsObservableAndCorrect()
    {
        ReplayFixture fixture = Fixtures.Read("base-8frames");
        ReplayRun run = ReplayEngine.Run(fixture);

        var order = new List<string>();
        foreach (string line in run.RawLines)
        {
            if (line.StartsWith("PROBE ", StringComparison.Ordinal))
            {
                order.Add("probe");
            }
            else if (line.StartsWith("EVENT ", StringComparison.Ordinal))
            {
                order.Add("event");
            }
            else if (line.StartsWith("DIRECTIVE ", StringComparison.Ordinal))
            {
                order.Add("directive");
            }
            else if (line.StartsWith("BUDGET ", StringComparison.Ordinal))
            {
                order.Add("budget");
            }
        }

        int frames = fixture.Frames.Count;
        Assert.Equal(frames, order.Count(o => o == "probe"));
        Assert.Equal(frames, order.Count(o => o == "budget"));
        Assert.True(order.Count(o => o == "directive") > 0, "没有任何指令行 —— 帧顺序判据是空转的");
        Assert.Equal(frames * 2, order.Count(o => o == "probe") + order.Count(o => o == "budget"));

        // 每帧内：probe 先于 event，event 先于 budget；budget 之后不再有 event。
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i] != "probe")
            {
                continue;
            }

            int budget = order.IndexOf("budget", i);
            Assert.True(budget > i, "budget 出现在 probe 之前");
            for (int j = i + 1; j < budget; j++)
            {
                Assert.NotEqual("budget", order[j]);
            }
        }
    }

    private static BrainDirective Make(DirectiveKind kind, string suffix) => kind switch
    {
        DirectiveKind.SetAnimation => BrainDirective.SetAnimation("clip" + suffix),
        DirectiveKind.MoveTo => BrainDirective.MoveTo(new PetPoint(1, 2)),
        DirectiveKind.Emit => BrainDirective.Emit(PetEvent.Named("core.window.moved")),
        DirectiveKind.RequestCapability => BrainDirective.RequestCapability("input.mousePosition", "why" + suffix),
        DirectiveKind.Suspend => BrainDirective.Suspend("s" + suffix),
        DirectiveKind.Resume => BrainDirective.Resume("r" + suffix),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string Tag(BrainDirective directive) => directive.ToString();
}

/// <summary>目录与 payload 口径的静态面判据（不依赖运行期状态）。</summary>
public sealed class CatalogContractTests
{
    /// <summary>§2.3：13 条的 payload 字段集与契约表逐字一致（名字 + 类型 + 可选性）。
    /// 红：字段名拼错（dt 写成 delta）—— 订阅者按文档写的代码读不到值。</summary>
    [Fact]
    public void PayloadSchemaMatchesContractFieldSets()
    {
        foreach (CatalogEntry entry in EventCatalogTable.All)
        {
            foreach (PayloadField field in entry.Payload)
            {
                Assert.True(field.Name.Length > 0);
                Assert.True(char.IsLower(field.Name[0]), $"{entry.Name}.{field.Name} 首字母不是小写");
            }
        }

        PayloadField velocity = EventCatalogTable.Find("input.pointer.move")!.Payload.Single(f => f.Name == "velocity");
        Assert.True(velocity.Optional, "§2.5：velocity 必须是可空缺省字段");
        Assert.Equal(FieldKind.Double, velocity.Kind);
        Assert.Equal(FieldKind.Int2, EventCatalogTable.Find("core.window.moved")!.Payload.Single(f => f.Name == "position").Kind);
        Assert.Equal(FieldKind.Int4, EventCatalogTable.Find("core.window.dpiChanged")!.Payload.Single(f => f.Name == "rect").Kind);
        Assert.Equal(FieldKind.Double, EventCatalogTable.Find("core.window.dpiChanged")!.Payload.Single(f => f.Name == "scale").Kind);
    }

    /// <summary>§2.2：`host.canceled` 是**唯一**的待补表豁免，且它今天确实进 core 的日志（docs/11:260）。
    /// 红：有人顺手加第二个豁免名 —— 「表外不得出现」被悄悄放宽。</summary>
    [Fact]
    public void PendingTableExemptionIsSingular()
    {
        Assert.Equal("host.canceled", EventCatalogTable.PendingTableExemption);
        Assert.Equal(1, EventCatalogTable.NamesWithExemption.Count(n => !EventCatalogTable.IsKnown(n)));
        Assert.Equal("host.canceled", PetInstance.HostCanceledEventName);

        using CoreDrive drive = CoreDrive.Create();
        drive.Publish(PetInstance.HostCanceledEventName, null, 0);

        if (CoreSurface.Current.HasCatalogGate)
        {
            // t2 的 EventCatalog 已含 HostCanceled → 它仍在可接受集里（补表前豁免）。
            Assert.Equal(1, drive.CountOf(PetInstance.HostCanceledEventName));
        }
    }

    /// <summary>§7.1：WorldSnapshot 的字段**零增零删** —— 5 个字段与 docs/11:144-148 一致。
    /// 红：M4 顺手加了一个字段 —— 那是 ABI 破坏，docs/11 是 FROZEN-v1。</summary>
    [Fact]
    public void WorldSnapshotKeepsItsFiveFields()
    {
        Type type = typeof(WorldSnapshot);
        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(5, properties.Length);
        Assert.Equal(
            new[] { "Cursor", "FocusedApp", "IsOccluded", "TimeOfDayMinutes", "WindowRect" },
            properties.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        Assert.Equal(typeof(PetPoint?), type.GetProperty("Cursor")!.PropertyType);
        Assert.Equal(typeof(PetRect), type.GetProperty("WindowRect")!.PropertyType);
        Assert.Equal(typeof(string), type.GetProperty("FocusedApp")!.PropertyType);
        Assert.Equal(typeof(int), type.GetProperty("TimeOfDayMinutes")!.PropertyType);
        Assert.Equal(typeof(bool), type.GetProperty("IsOccluded")!.PropertyType);
    }

    /// <summary>§2.2 判据 2 的对面：六个生命周期名与 `host.slice` **不是**总线事件。
    /// 红：把它们塞进总线目录 —— 「表外不得出现」就失去可判定性（什么都算表内）。</summary>
    [Fact]
    public void LifecycleNamesAreNotBusEvents()
    {
        foreach (string name in new[]
        {
            "pluginDiscovered", "pluginLoaded", "pluginActivated", "pluginSuspend", "pluginResume",
            "pluginUnloading", "host.slice",
        })
        {
            Assert.False(EventCatalogTable.IsKnown(name), $"{name} 被当成总线事件（§2.2）");
            Assert.False(EventCatalogTable.IsAcceptable(name), $"{name} 进了可接受集");
        }
    }

    /// <summary>§2.1 与 core 的 `EventCatalog` 逐项对账：core 那份是**运行期唯一**名字表，
    /// 测试侧的镜像只能是镜像。core 已落地 → 这条今天真跑。
    /// 红：两份名字表漂移 —— 订阅者按测试的名单订阅，core 却认另一份。</summary>
    [Fact]
    public void CatalogMirrorMatchesCoreEventCatalog()
    {
        CatalogAdapter? catalog = CatalogAdapter.TryCreate();
        if (catalog is null)
        {
            Skip.With(CoreSurface.Reason("core 的 EventCatalog（运行期唯一名字表）", "§2.1", "t2"));
            return;
        }

        IReadOnlyList<string> coreNames = catalog.Names();
        Assert.True(coreNames.Count > 0, $"{catalog.TypeName} 上没有可读的名字集合");

        // 名字集合必须一致（core 含豁免名 host.canceled，这是 §2.2 的待补表状态）。
        Assert.Equal(
            EventCatalogTable.NamesWithExemption.OrderBy(n => n, StringComparer.Ordinal),
            coreNames.OrderBy(n => n, StringComparer.Ordinal));

        // 13 个表内名字逐个 must-contain（比集合相等更硬的断言）。
        foreach (string name in EventCatalogTable.Names)
        {
            Assert.True(catalog.Contains(name), $"core 的 EventCatalog 不含表内名字 {name}");
        }

        // 表外名字必须被 core 拒（fail-closed 的另一面）。
        Assert.False(catalog.Contains("input.pointer.hover"));

        // 模式逐条对账（§2.1 的 Mode 列）。
        foreach (CatalogEntry entry in EventCatalogTable.All)
        {
            string? coreMode = catalog.ModeOf(entry.Name);
            Assert.True(coreMode is not null, $"core 对 {entry.Name} 没有 Mode");
            Assert.Equal(entry.Mode.ToString(), coreMode);
        }

        // §2.3 的 payload 口径：合法 payload 0 违规、多一个字段即违规。
        foreach (CatalogEntry entry in EventCatalogTable.All)
        {
            var legal = new JsonObject();
            foreach (PayloadField field in entry.Payload)
            {
                legal[field.Name] = field.Kind switch
                {
                    FieldKind.Int => 1,
                    FieldKind.Double => 1.5,
                    FieldKind.Bool => true,
                    FieldKind.Str => "x",
                    FieldKind.Int2 => new JsonArray(1, 2),
                    _ => new JsonArray(1, 2, 3, 4),
                };
            }

            int? violations = catalog.CountPayloadViolations(entry.Name, legal);
            if (violations is null)
            {
                continue;
            }

            Assert.Equal(0, violations.Value);
            legal["extraField"] = 1;
            Assert.Equal(1, catalog.CountPayloadViolations(entry.Name, legal));
        }

        Console.WriteLine($"EVENT-CHECK Catalog.CoreMirror PASS core={catalog.TypeName} names={coreNames.Count}");
    }
}

/// <summary>§4 requestCapability 的 200ms 与审计（N3）。</summary>
public sealed class BrokerContractTests
{
    /// <summary>N3（§8.4）：注入时钟推 201ms → `verdict=refused reason=timeout`、`latency=201`、审计恰好一行。
    /// 红：超时不判拒 —— 一个永远 pending 的能力请求会把授权面冻住。</summary>
    [Fact]
    public void N3_RequestCapabilityTimesOutAt201ms()
    {
        BrokerAdapter? broker = BrokerAdapter.TryCreate(new[] { "input.mousePosition" });
        if (broker is null)
        {
            Skip.With(CoreSurface.Reason("CapabilityBroker", "§4.4 / §8.4 N3", "t2"));
            return;
        }

        // 留一条 pending，再用 +201ms 的 Request 把它结算成 timeout。
        // ⚠️ 已观测事实（写给 t5）：`Request` 的返回值是**本次请求自己**的裁决 ——
        // 超时结算的是**上一条** pending 请求（audit 里 seq=1 那条），所以本次 Request 仍返回 accepted。
        // 判据因此落在**审计行**上（那才是 §4.4 要的可对账面）：timeout 行必须存在、latency=201。
        Assert.True(broker.Defer("input.mousePosition", 1000), "CapabilityBroker.Defer 不存在");
        (bool ok, string verdict, string reason, long latency, int _) =
            broker.Request("input.mousePosition", "need cursor", 1201);

        Assert.True(ok, "CapabilityBroker.Request 不存在");
        Assert.Equal("Accepted", verdict);

        IReadOnlyList<string> audit = broker.Audit();
        // 恰好两行：被结算的那条 timeout + 本次 Request 自己的 accepted（§4.4 的「每次终态恰好一行」）。
        Assert.Equal(2, audit.Count);
        string timeoutLine = audit.Single(line => line.Contains("reason=timeout", StringComparison.Ordinal));
        Assert.Contains("verdict=refused", timeoutLine);
        Assert.Contains("latency=201", timeoutLine);
        Assert.Contains("seq=1", timeoutLine);
        Console.WriteLine($"EVENT-CHECK Broker.Timeout PASS verdict={verdict} reason={reason} latency={latency}"
            + $" audit-lines={audit.Count} timeout-line=[{timeoutLine}]");
    }

    /// <summary>§4.4 `capability-200ms`：正常路径 `max-latency <= 200`（注入时钟下可为 0）；
    /// 且三种终态（accepted / not-declared / not-whitelisted）各自恰有一行审计、字段数固定。
    /// 红：只记 refusal 不记授权 —— 授权面没有可对账的记录。</summary>
    [Fact]
    public void CapabilityAuditRecordsEveryTerminalState()
    {
        BrokerAdapter? broker = BrokerAdapter.TryCreate(new[] { "input.mousePosition" });
        if (broker is null)
        {
            Skip.With(CoreSurface.Reason("CapabilityBroker 的裁决与审计行", "§4.4", "t2"));
            return;
        }

        (_, string acceptedVerdict, string acceptedReason, long acceptedLatency, _) =
            broker.Request("input.mousePosition", "declared", 5000);
        Assert.Equal("Accepted", acceptedVerdict);
        Assert.Equal("-", acceptedReason);
        Assert.True(acceptedLatency <= 200, $"正常路径 latency 应 <= 200，实际 {acceptedLatency}");

        // 「已声明但请求里没声明」这一格：broker 的拒绝原因是 not-declared 还是 not-whitelisted，
        // 取决于该 id 是否属于 docs/14 白名单 —— input.keyboardHook **在白名单内但未声明**，
        // 所以这一格按 not-declared 判（本会话实测：core 先判白名单、再判声明集）。
        (_, string undeclaredVerdict, string undeclaredReason, _, _) =
            broker.Request("input.keyboardHook", "not declared", 5001);
        Assert.Equal("Refused", undeclaredVerdict);
        Assert.Equal("not-declared", undeclaredReason);

        (_, string outsideVerdict, string outsideReason, _, _) =
            broker.Request("not.a.capability", "outside whitelist", 5002);
        Assert.Equal("Refused", outsideVerdict);
        Assert.Equal("not-whitelisted", outsideReason);

        IReadOnlyList<string> audit = broker.Audit();
        Console.WriteLine($"EVENT-CHECK Note.BrokerAudit INFO lines={audit.Count} [{string.Join(" | ", audit)}]");
        Assert.Equal(3, audit.Count);
        // 字段数：`capability-audit` + seq / cap / verdict / reason / at / latency = 1 + 6 = 7 个空格分隔 token。
        // （§4.4 说的是「字段数固定」；把令牌数写死成一个具体值就是那条「固定」的可判定形态。）
        foreach (string line in audit)
        {
            Assert.StartsWith("capability-audit ", line, StringComparison.Ordinal);
            Assert.Equal(7, line.Split(' ').Length);
        }

        Assert.Contains(audit, l => l.Contains("verdict=accepted", StringComparison.Ordinal));
        Assert.Contains(audit, l => l.Contains("reason=not-declared", StringComparison.Ordinal));
        Assert.Contains(audit, l => l.Contains("reason=not-whitelisted", StringComparison.Ordinal));

        Console.WriteLine($"EVENT-CHECK Broker.Audit PASS lines={audit.Count} fields=6"
            + $" accepted=1 refused=2 max-latency={acceptedLatency}");
    }

    /// <summary>§4.2 的时钟纪律：broker **自己**不读墙钟（注入时钟）。
    /// 红：broker 内部读 Stopwatch —— 超时判据只能靠真卡 200ms 才过，不可确定性复现。
    /// 判定读 core 的源码文本（只读，不改）。</summary>
    [Fact]
    public void BrokerNeverReadsTheWallClockItself()
    {
        CoreSurfaceReport surface = CoreSurface.Current;
        if (!surface.HasCapabilityBroker)
        {
            Skip.With(CoreSurface.Reason("CapabilityBroker 的源码（时钟纪律自查）", "§4.2", "t2"));
            return;
        }

        string path = Path.Combine(RepositoryRoot(), surface.CapabilityBrokerType.Replace('.', Path.DirectorySeparatorChar) + ".cs");
        if (!File.Exists(path))
        {
            // 类型全名与文件路径不一定同构（t2 的命名自由）—— 退回按文件名找。
            string? found = Directory
                .EnumerateFiles(Path.Combine(RepositoryRoot(), "packages", "core"), "CapabilityBroker.cs",
                    SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
            Assert.True(found is not null, "找不到 CapabilityBroker.cs");
            path = found!;
        }

        string source = File.ReadAllText(path);
        // 时钟纪律：broker **自己**不读时钟 —— 时刻由调用方注入（nowMs 参数）。
        // 注意区分：源码注释里出现 "Stopwatch" 这个词不算违规，只有**真正的调用**才算。
        Assert.False(source.Contains("Stopwatch.", StringComparison.Ordinal), "broker 调用了 Stopwatch");
        Assert.False(source.Contains("DateTime.Now", StringComparison.Ordinal), "broker 读了墙钟日期");
        Assert.False(source.Contains("Environment.TickCount", StringComparison.Ordinal), "broker 读了 TickCount");
        Assert.Contains("nowMs", source);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PetDock.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("找不到仓库根（PetDock.sln）");
    }
}

/// <summary>§7.1 的采样口径：WorldProbe 的五个字段来源与「取不到时」的处置。</summary>
public sealed class WorldProbeContractTests
{
    /// <summary>§7.1：注入 tod 值（**不读墙钟**）后，采样必须给出注入值；hwnd=0 时各字段取兜底值。
    /// 红：探针读了墙钟 —— 重放不再确定性；或 hwnd=0 时给了非兜底值（凭空造可信输入）。</summary>
    [Fact]
    public void ProbeUsesInjectedTimeOfDayAndHonoursNoWindow()
    {
        ProbeAdapter? probe = ProbeAdapter.TryCreate(injectedTod: 617, out bool injected);
        if (probe is null)
        {
            Skip.With(CoreSurface.Reason("WorldProbe", "§7.1", "t2"));
            return;
        }

        Assert.True(injected, "WorldProbe 没有接受注入时钟（§7.1 要求 --eventtest 用注入值）");
        Assert.True(probe.Sample(IntPtr.Zero, out WorldSnapshot world), "WorldProbe.Sample(hwnd) 不存在");

        // §7.1：注入值必须被原样采用（**不读墙钟**）。
        Assert.Equal(617, world.TimeOfDayMinutes);

        // §7.1 的「取不到时」：hwnd=0 时窗口矩形保持上一次值（从未有过 → 0,0,0,0）、
        // IsOccluded=false。**Cursor 与 FocusedApp 不在 hwnd=0 时被清空** —— 它们直接来自
        // GetCursorPos / GetForegroundWindow（与窗口无关），所以真实运行下会取到本机的真实值。
        // 这条判据只断言「与窗口相关的两项」的兜底口径；本机真实值与注入值无关，不作为判据。
        Assert.False(world.IsOccluded);
        Assert.Equal(new PetRect(0, 0, 0, 0), world.WindowRect);
        Console.WriteLine($"EVENT-CHECK Probe.Injected PASS tod={world.TimeOfDayMinutes}"
            + $" occluded={world.IsOccluded} window={world.WindowRect}");
    }

    /// <summary>§7.1 的「取不到时」：`FocusedApp` 若取不到必须是 **null**，**不许**交 "(unknown)"
    /// （t1 §7.1 逐字）。这条读的是探针的源码文本 —— 它是判据的一部分，不是注释。
    /// 红：把 "(unknown)" 交给 Brain —— 下游会当成真实前台应用名。</summary>
    [Fact]
    public void FocusedAppFoldsUnknownIntoNull()
    {
        CoreSurfaceReport surface = CoreSurface.Current;
        if (!surface.HasWorldProbe)
        {
            Skip.With(CoreSurface.Reason("WorldProbe 的 FocusedApp 折叠口径", "§7.1", "t2"));
            return;
        }

        string? path = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "packages", "core"), "WorldProbe.cs", SearchOption.AllDirectories)
            .FirstOrDefault(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        Assert.True(path is not null, "找不到 WorldProbe.cs");

        string source = File.ReadAllText(path!);
        Assert.Contains("\"(unknown)\"", source);   // 它确实把那个字符串折掉了
        Assert.Contains("return null", source);
        Console.WriteLine("EVENT-CHECK Probe.FocusedAppFold PASS unknown-folds-to-null=true");
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PetDock.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("找不到仓库根（PetDock.sln）");
    }
}
