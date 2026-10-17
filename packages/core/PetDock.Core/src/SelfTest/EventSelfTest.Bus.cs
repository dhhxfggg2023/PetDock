// --eventtest 的第二块（D-03c：入口文件超 400 行）：五种 dispatch 与目录守门。
// 分工：EventSelfTest.cs = 入口 + 能力 + 仲裁 + 指令 + 钩子 + 世界采样；
// 本文件 = 总线自身的语义（§1.4 七行判据 + §2.2 目录守门 + §2.3 载荷口径）。
// 拆分只是分组搬运，两类判据互不依赖，也不共享状态。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

internal static partial class EventSelfTest
{
    /// <summary>契约 §1.4：五种 dispatch 各自的机器判据 + 「异常不穿出宿主」。</summary>
    private static int DispatchModes()
    {
        int failures = 0;
        var log = new HostEventDispatch();
        var bus = new EventBus(log);
        string tick = EventCatalog.ClockTick;

        // emit：第 1 个监听者抛异常，第 2/3 个必须仍然被调用（after-fault-ran 是核心断言）。
        // ran 的口径是**被调用数**（契约 §1.4 逐字 `listeners=3 ran=3 faults=1`）：
        // 抛异常那位也 ran 了，所以 ran 是 3 而不是 2 —— 这条断言正是该口径的守门员。
        bus.Subscribe(tick, (_, _) => throw new InvalidOperationException("emit-boom"));
        bus.Subscribe(tick, (_, _) => "second");
        bus.Subscribe(tick, (_, _) => "third");
        (int ran, int faults, bool after) = bus.Emit(PetEvent.Named(tick));
        Check(ref failures, "dispatch-emit",
            ran == 3 && faults == 1 && after,
            $"listeners=3 ran={ran} faults={faults} after-fault-ran={Flag(after)}");

        // parallel：4 个监听者全跑完，一个抛异常不拦其余（ran 同样计被调用数）。
        var pbus = new EventBus(new HostEventDispatch());
        pbus.Subscribe(tick, (_, _) => null);
        pbus.Subscribe(tick, (_, _) => throw new InvalidOperationException("parallel-boom"));
        pbus.Subscribe(tick, (_, _) => null);
        pbus.Subscribe(tick, (_, _) => null);
        (int pran, int pfaults) = pbus.Parallel(PetEvent.Named(tick));
        Check(ref failures, "dispatch-parallel",
            pran == 4 && pfaults == 1,
            $"listeners=4 ran={pran} faults={pfaults}");

        // serial：第 2 个监听者必须看见第 1 个的返回值（只读快照，不是可变共享）。
        var sbus = new EventBus(new HostEventDispatch());
        sbus.Subscribe(tick, (_, _) => "hop-0");
        sbus.Subscribe(tick, (_, round) => round is not null && round.Returned.Count == 1 ? "hop-1" : null);
        sbus.Subscribe(tick, (_, round) => round is not null && round.Returned.Count == 2 ? "hop-2" : null);
        (int rounds, bool sawPrevious) = sbus.Serial(PetEvent.Named(tick));
        Check(ref failures, "dispatch-serial",
            rounds == 3 && sawPrevious, $"rounds={rounds} saw-previous={Flag(sawPrevious)}");

        // bail：第 2 个返回 true 即停，第 3 个不许被调用。
        var bbus = new EventBus(new HostEventDispatch());
        bbus.Subscribe(tick, (_, _) => false);
        bbus.Subscribe(tick, (_, _) => true);
        bbus.Subscribe(tick, (_, _) => false);
        (int bran, bool breturned, int stoppedAt, bool bfault) = bbus.Bail(PetEvent.Named(tick));
        Check(ref failures, "dispatch-bail",
            bran == 2 && breturned && stoppedAt == 1 && !bfault,
            $"listeners=3 ran={bran} returned={Flag(breturned)} stopped-at={stoppedAt}");

        // bail 的异常 = 拒绝（fail-closed）：抛异常的监听者不能被读成「同意」。
        var fbus = new EventBus(new HostEventDispatch());
        fbus.Subscribe(tick, (_, _) => false);
        fbus.Subscribe(tick, (_, _) => throw new InvalidOperationException("bail-boom"));
        fbus.Subscribe(tick, (_, _) => false);
        (int fran, bool freturned, int fstop, bool ffault) = fbus.Bail(PetEvent.Named(tick));
        Check(ref failures, "dispatch-bail-fault",
            freturned && ffault && fran == 2,
            $"fault-seen={Flag(ffault)} returned={Flag(freturned)} ran={fran} stopped-at={fstop}");

        // waterfall：三跳各改一个字段，最终 payload 必须含三处改动（mutated-each-hop）。
        var wbus = new EventBus(new HostEventDispatch());
        wbus.Subscribe(tick, (evt, _) => Mutate(evt, "a", 1));
        wbus.Subscribe(tick, (evt, _) => Mutate(evt, "b", 2));
        wbus.Subscribe(tick, (evt, _) => Mutate(evt, "c", 3));
        var seed = new PetEvent(tick, 0, new JsonObject { ["a"] = 0, ["b"] = 0, ["c"] = 0 });
        (int hops, JsonObject? final, bool mutated) = wbus.Waterfall(seed);
        int present = 0;
        if (final is not null)
        {
            foreach (string k in new[] { "a", "b", "c" })
            {
                if (final[k]?.GetValue<int>() == 1 + present) present++;
            }
        }

        Check(ref failures, "dispatch-waterfall",
            hops == 3 && mutated && present == 3,
            $"hops={hops} mutated-each-hop={Flag(mutated)} fields-set={present}");

        // 任何模式都不许让异常穿出宿主循环：上面五次分发各注入过异常，全部被吞并记账。
        long totalFaults = bus.DispatchFaults + pbus.DispatchFaults + fbus.DispatchFaults + sbus.DispatchFaults;
        Check(ref failures, "dispatch-no-escape", totalFaults == 3, $"all-modes-faults-contained=true faults={totalFaults}");
        return failures;
    }

    private static JsonObject Mutate(PetEvent evt, string key, int value)
    {
        var next = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> pair in evt.Payload ?? new JsonObject())
        {
            next[pair.Key] = pair.Value?.DeepClone();
        }

        next[key] = value;
        return next;
    }

    /// <summary>契约 §2.2：目录守门 fail-closed —— 表外名字不入 Log、非静默、合法事件不受影响。</summary>
    private static int Catalog()
    {
        int failures = 0;
        Check(ref failures, "catalog-size", EventCatalog.Names.Count == 14, $"names={EventCatalog.Names.Count}");

        var log = new HostEventDispatch();
        log.Publish(PetEvent.Named("input.pointer.hover")); // 表外：不入 Log
        log.Publish(PetEvent.Named(EventCatalog.ClockTick)); // 同批次合法事件必须仍然进 Log
        bool rejected = log.OutsideCatalog == 1 && log.CountOf("input.pointer.hover") == 0;
        Check(ref failures, "event-catalog-closed",
            rejected && log.CountOf(EventCatalog.ClockTick) == 1 && log.Log.Count == 1,
            $"rejected={log.OutsideCatalog} outside=0 logged-outside={log.CountOf("input.pointer.hover")} legal-logged={log.CountOf(EventCatalog.ClockTick)}");

        // 审计面：Log 里每个 Type 必须 ∈ 目录。
        int outside = 0;
        foreach (PetEvent evt in log.Log)
        {
            if (!EventCatalog.Contains(evt.Type)) outside++;
        }

        Check(ref failures, "catalog-audit", outside == 0, $"log={log.Log.Count} outside-names={outside}");
        return failures;
    }

    /// <summary>契约 §2.3：每个事件真实发布一次后逐字段比对（表里的允许字段集）。</summary>
    private static int PayloadSchema()
    {
        int failures = 0;
        var log = new HostEventDispatch();
        int violations = 0;
        foreach (KeyValuePair<string, JsonObject> pair in SamplePayloads())
        {
            log.Publish(new PetEvent(pair.Key, 0, pair.Value));
            violations += EventCatalog.CountPayloadViolations(pair.Key, pair.Value);
        }

        Check(ref failures, "event-payload-schema",
            violations == 0 && log.Log.Count == 14 && log.OutsideCatalog == 0,
            $"names=14 published={log.Log.Count} violations={violations}");
        return failures;
    }

    /// <summary>14 个事件各一份合法载荷（camelCase，整型不写 long/double —— 契约 §2.3）。</summary>
    private static Dictionary<string, JsonObject> SamplePayloads() => new()
    {
        [EventCatalog.ClockTick] = new() { ["dt"] = 16 },
        [EventCatalog.WindowDpiChanged] = new() { ["scale"] = 1.5, ["rect"] = new JsonArray(0, 0, 1, 1) },
        [EventCatalog.WindowMoved] = new() { ["windowId"] = "s", ["position"] = new JsonArray(1, 2) },
        [EventCatalog.WindowFocus] = new() { ["focused"] = true },
        [EventCatalog.HostSuspend] = new() { ["reason"] = "r" },
        [EventCatalog.HostResume] = new() { ["reason"] = "r" },
        [EventCatalog.PetStateCommit] = new() { ["petId"] = "p" },
        [EventCatalog.PetStateMigrated] = new() { ["from"] = "a", ["to"] = "b" },
        [EventCatalog.HostCanceled] = new() { ["sessionId"] = "s", ["reason"] = "r" },
        [EventCatalog.PointerMove] = new() { ["pos"] = new JsonArray(1, 2) },
        [EventCatalog.PointerDown] = new() { ["button"] = 0, ["windowPoint"] = new JsonArray(1, 2) },
        [EventCatalog.PointerUp] = new() { ["button"] = 0, ["windowPoint"] = new JsonArray(1, 2) },
        [EventCatalog.KeyDown] = new() { ["vkey"] = 65, ["modifiers"] = 0 },
        [EventCatalog.KeyUp] = new() { ["vkey"] = 65, ["modifiers"] = 0 },
    };
}
