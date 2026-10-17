// 重放引擎：把一个夹具逐帧喂进 core，产出**规范行**（§8.3 的比较对象）与原始行。
//
// 帧内六步照 t1 §1.3 的固定序（顺序本身是判据）：
//   1. WorldProbe.Sample   → 层 1 由夹具的 world 构造 WorldSnapshot（真采样器归 t2）
//   2. QueryFrame          → 层 1 真走 PetInstance 的公开链（装不上实例时如实记 unavailable）
//   3. dispatch core.clock.tick (serial)
//   4. dispatch 输入事件（固定类型序：pointer.move → pointer.down → pointer.up → key.down → key.up）
//   5. Brain.Tick(dt, world) → HostArbiter.Apply(directives)
//   6. EnforceSessionBudget
//
// 三件刻意的设计（都不是实现细节，是判据的前提）：
//   a. **原始行里带 `run=` 与 `elapsed=`**。`run` 是本进程内的重放序号（第二次必是另一个数），
//      `elapsed` 是本机耗时 —— 两者一起保证 run1 与 run2 的原始文本**真的不同**，从而 §8.3 的
//      抹除器有东西可抹、「两次规范行相等」不是同义反复。`run=` 是确定性的相异来源（不靠计时抖动），
//      `elapsed=` 是真实但会抖的那个；两条都在 Canonicalizer.StrippedFieldNames 里逐个列出。
//   b. **规范行与期望用同一个渲染函数**：core 指令走 `DirectiveBridge.Render(#)`，夹具期望也走
//      `Render(#)` —— 两侧共用一套渲染，「两份渲染各自漂移」这个坑就不会出现。
//   c. **变体注入（<see cref="RunMutation"/>）是引擎的一等输入**：N5 元判据要「故意让两次不同」，
//      改 payload 字段的负例要「只改一个字段」，都在这里表达，而不是在测试里偷偷改字符串。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.EventReplay.Tests;

/// <summary>一次重放的变体（N5 的元判据 + 「改一条 payload 字段」的负例注入点）。</summary>
public sealed record RunMutation(
    int TodOffset = 0,
    int AtSeq = -1,
    string? EventType = null,
    string? Field = null,
    JsonNode? Value = null)
{
    /// <summary>不变体（正向判据的两次重放都用它）。</summary>
    public static RunMutation None => new();

    public bool HasPayloadPatch => AtSeq >= 0 && EventType is not null && Field is not null;

    public override string ToString() => HasPayloadPatch
        ? $"tod+{TodOffset} patch=seq{AtSeq}:{EventType}.{Field}->{Value?.ToJsonString() ?? "null"}"
        : $"tod+{TodOffset}";
}

/// <summary>重放引擎。</summary>
public static class ReplayEngine
{
    /// <summary>进程内的重放序号（原始行的**确定性**相异来源；Canonicalizer 会抹掉它）。</summary>
    private static int _runCounter;

    /// <summary>§1.3 第 4 步的固定类型序（输入事件按它分发，与到达序无关）。</summary>
    public static readonly string[] InputTypeOrder =
    {
        "input.pointer.move", "input.pointer.down", "input.pointer.up", "input.key.down", "input.key.up",
    };

    /// <summary>「输入事件」= 由帧内第 4 步分发的那些（§1.3），**不含**指令派生的事件
    /// （§6.2/§6.5/§6.6 的 core.window.moved / host.suspend / host.resume 在指令落地时发布）。
    /// 这条区分是 §5.3「投递序 ≠ 裁决序」判据的前提。</summary>
    public static bool IsInputEvent(string type) =>
        Array.IndexOf(InputTypeOrder, type) >= 0
        || string.Equals(type, "core.clock.tick", StringComparison.Ordinal)
        || string.Equals(type, "core.window.focus", StringComparison.Ordinal)
        || string.Equals(type, "core.window.dpiChanged", StringComparison.Ordinal);

    /// <summary>该帧由**指令**派生的事件类型（§6.1 的映射表；R4 判定的允许集要和它并起来）。</summary>
    public static IReadOnlyList<string> EventsDerivedFromDirectives(FixtureFrame frame)
    {
        var derived = new List<string>();
        foreach (FixtureDirective directive in frame.Brain)
        {
            switch (directive.Kind)
            {
                case "emit":
                    if (directive.EventType is not null)
                    {
                        derived.Add(directive.EventType);
                    }

                    break;
                case "moveTo":
                    derived.Add("core.window.moved");   // §6.2：moveTo 映射到 core.window.moved
                    break;
                case "suspend":
                    derived.Add("host.suspend");        // §6.5：先征询 host.suspend
                    break;
                case "resume":
                    derived.Add("host.resume");         // §6.6：先发 host.resume
                    break;
                default:
                    break;
            }
        }

        return derived;
    }

    /// <summary>跑一次夹具，产出原始行与规范行。</summary>
    public static ReplayRun Run(ReplayFixture fixture, RunMutation? mutation = null)
    {
        RunMutation variant = mutation ?? RunMutation.None;
        int runId = ++_runCounter;
        var raw = new List<string>();
        using CoreDrive drive = CoreDrive.Create();

        raw.Add($"RUN fixture={fixture.Name} frames={fixture.Frames.Count} run={runId}"
            + $" variant={variant} core={typeof(PetInstance).Assembly.GetName().Name}"
            + $" driven={string.Join(",", drive.DrivenSurface)}");
        if (drive.KernelLogNote.Length > 0)
        {
            raw.Add($"NOTE {drive.KernelLogNote}");
        }

        int nowMs = 0;
        foreach (FixtureFrame frame in fixture.Frames)
        {
            double frameStart = Volatile.ElapsedMs;
            nowMs += frame.DtMs;
            WorldSnapshot world = WorldOf(frame, frame.Tod + variant.TodOffset);

            // 1/2. 世界快照 + 取帧（层 1：真走 core 公开链；装不上实例时如实记 unavailable）。
            raw.Add($"PROBE seq={frame.Seq} world={RenderWorld(world)} now={nowMs}"
                + $" query={(drive.TickAvailable ? "ok" : "unavailable")}");

            // 3/4. 事件分发：先 clock.tick（serial），再输入事件（按 §1.3 的固定类型序）。
            int dispatched = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (FixtureEvent evt in frame.Events)
                {
                    bool isTick = string.Equals(evt.Type, "core.clock.tick", StringComparison.Ordinal);
                    if ((pass == 0) != isTick)
                    {
                        continue;
                    }

                    JsonObject? payload = PayloadOf(evt, frame, variant);
                    drive.Publish(evt.Type, payload, nowMs);
                    dispatched++;
                    raw.Add($"EVENT seq={frame.Seq} type={evt.Type} order={InputRank(evt.Type)}"
                        + $" payload={DirectiveBridge.RenderPayload(payload)} at={nowMs}");
                }
            }

            // 5. Brain.Tick → HostArbiter.Apply（层 1：PetInstance.TickBrain 与 HostArbiter 都是真的）。
            var coreDirectives = new List<BrainDirective>();
            foreach (FixtureDirective directive in frame.Brain)
            {
                coreDirectives.Add(DirectiveBridge.ToCore(directive));
            }

            IReadOnlyList<BrainDirective> ordered = drive.Apply(coreDirectives);
            foreach (BrainDirective directive in ordered)
            {
                raw.Add($"DIRECTIVE seq={frame.Seq} {DirectiveBridge.Render(directive)}");
            }

            // 6. EnforceSessionBudget（docs/11:260 的时间触发面）。
            bool forced = drive.EnforceSessionBudget(nowMs);
            raw.Add($"BUDGET seq={frame.Seq} forced-cancel={Lower(forced)}");

            raw.Add($"FRAME seq={frame.Seq} run={runId} dispatched={dispatched} directives={ordered.Count}"
                + $" ticks={drive.Ticks} logged={drive.LoggedEvents}"
                + $" elapsed={Round(Volatile.ElapsedMs - frameStart)}");
        }

        raw.Add($"SUMMARY frames={fixture.Frames.Count} logged={drive.LoggedEvents}"
            + $" ticks={drive.Ticks} driven={string.Join(",", drive.DrivenSurface)}");

        return new ReplayRun(
            fixture.Name,
            raw,
            Canonicalizer.Canonicalize(raw),
            Canonicalizer.StrippedFieldNames);
    }

    /// <summary>§8.3 的确定性判据：同夹具跑两次，第二次带（可选的）变体。
    /// 变体为空 = 正向判据（必须 PASS）；变体非空 = 元判据（必须 FAIL）。</summary>
    public static DeterminismVerdict Judge(
        ReplayFixture fixture, RunMutation? mutation = null, int minimumLines = 8)
    {
        RunMutation variant = mutation ?? RunMutation.None;
        ReplayRun first = Run(fixture, RunMutation.None);
        ReplayRun second = Run(fixture, variant);

        bool equal = string.Equals(first.CanonicalSha, second.CanonicalSha, StringComparison.Ordinal);
        bool rawDiffers = !string.Equals(first.RawText, second.RawText, StringComparison.Ordinal);
        int lines = Math.Min(first.CanonicalLineCount, second.CanonicalLineCount);

        // 单帧夹具（13 条 single-* 与 N1..N4）结构上只有 6~7 行，所以「非空」的下限按帧数缩放：
        // 每帧至少 5 行（PROBE + ≥1 EVENT + ≥0 DIRECTIVE + BUDGET + FRAME）加 1 行 RUN 头。
        // 硬编码 8 会把合法的单帧夹具判成红（本会话实测踩过），而 §8.3 的意图是**防两次都空**，
        // 不是给短夹具设一个它结构上到不了的数。
        int floor = Math.Min(minimumLines, 1 + (fixture.Frames.Count * 5));
        return new DeterminismVerdict(
            fixture.Name,
            first.CanonicalSha,
            second.CanonicalSha,
            equal,
            rawDiffers,
            lines,
            lines >= floor);
    }

    /// <summary>夹具的 world → core 的 WorldSnapshot（§7.1 五字段，零增零删；cursor 越出窗口 → null）。
    /// <paramref name="todOverride"/> 是**夹具给的**注入值 —— 本函数绝不读墙钟（§8.2 规则 2）。</summary>
    public static WorldSnapshot WorldOf(FixtureFrame frame, int? todOverride = null)
    {
        WorldSnapshotView view = frame.World;
        int[] rect4 = view.WindowRect.Length >= 4 ? view.WindowRect : new[] { 0, 0, 0, 0 };
        PetRect rect = new(rect4[0], rect4[1], rect4[2], rect4[3]);

        PetPoint? cursor = null;
        if (view.Cursor is { Length: >= 2 } point)
        {
            bool inside = point[0] >= rect.X && point[0] <= rect.X + rect.W
                && point[1] >= rect.Y && point[1] <= rect.Y + rect.H;
            if (inside)
            {
                cursor = new PetPoint(point[0], point[1]);
            }
        }

        return new WorldSnapshot(cursor, rect, view.FocusedApp, todOverride ?? frame.Tod, view.Occluded);
    }

    /// <summary>该帧该事件的 payload；变体命中时覆盖**一个**字段（「改一条 payload 字段」的负例）。</summary>
    private static JsonObject? PayloadOf(FixtureEvent evt, FixtureFrame frame, RunMutation variant)
    {
        if (evt.Payload is null)
        {
            return null;
        }

        JsonObject payload = evt.Payload.DeepClone().AsObject();
        if (variant.HasPayloadPatch
            && frame.Seq == variant.AtSeq
            && string.Equals(evt.Type, variant.EventType, StringComparison.Ordinal))
        {
            payload[variant.Field!] = variant.Value?.DeepClone();
        }

        return payload;
    }

    /// <summary>§1.3 第 4 步的固定类型序在文本里的序号（core.clock.tick 是第 3 步，rank 0 便于对照）。</summary>
    public static int InputRank(string type)
    {
        for (int i = 0; i < InputTypeOrder.Length; i++)
        {
            if (string.Equals(InputTypeOrder[i], type, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>world 的规范渲染（§7.1 的五个字段逐字）。</summary>
    public static string RenderWorld(WorldSnapshot world) =>
        $"cursor={(world.Cursor is PetPoint point ? point.ToString() : "off")}"
        + $" window={world.WindowRect}"
        + $" focused={world.FocusedApp ?? "-"}"
        + $" tod={world.TimeOfDayMinutes}"
        + $" occluded={Lower(world.IsOccluded)}";

    private static string Lower(bool value) => value ? "true" : "false";

    private static string Round(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>
/// 逐条覆盖对账（验收④的机器形态）：13 个事件名 × 五种模式，逐格给「覆盖 / 未覆盖 + 原因」。
/// 这不是文档，是判据：CoverageLedgerTests 断言每一格都有明确状态与**具名**原因，
/// 且不许出现「待定 / 稍后 / 暂略」这类没有内容的词。
///
/// 层 1（今天真跑、真红）= 名字表 + 夹具 + 投递序 + payload 口径 + 仲裁序；
/// 层 2（t2 落地后才可执行）= 五种 dispatch 的 EventBus、目录守门、CapabilityBroker、真采样器。
/// 「可测的部分」指的就是这条分界 —— 层 2 的判据已全部写好，但会 SKIP 并写明具名原因（§2.6）。
/// </summary>
public static class CoverageLedger
{
    /// <summary>一格的裁决。</summary>
    public sealed record Cell(string Subject, string Status, string Reason)
    {
        public override string ToString() => $"{Subject} {Status} {Reason}";
    }

    /// <summary>13 条事件逐条的覆盖状态（每条两格：目录+夹具 / 生产源）。</summary>
    public static IReadOnlyList<Cell> Events()
    {
        var cells = new List<Cell>();
        foreach (CatalogEntry entry in EventCatalogTable.All)
        {
            cells.Add(new Cell(
                $"event:{entry.Name}:catalog+fixture",
                "covered",
                $"§2.1 第 {EventCatalogTable.IndexOf(entry.Name) + 1} 条（§{entry.Section}，{entry.Mode}）；"
                + $"夹具 fixtures/single-{entry.Name}.replay.jsonl + payload 口径校验；源：{entry.SourceNote}"));
            cells.Add(ProducerCell(entry));
        }

        return cells;
    }

    private static Cell ProducerCell(CatalogEntry entry) => entry.Name switch
    {
        "core.clock.tick" => new Cell($"event:{entry.Name}:producer", "covered",
            "帧循环第 3 步（§1.3）：每帧都发；投递序由 core 自己的 HostEventDispatch.Log 判定"),
        "input.pointer.move" => new Cell($"event:{entry.Name}:producer", "covered",
            "夹具投递 + §2.5 的 velocity 语义（首次不写该字段）+ 帧泵采样点由 §7.2 归 t2"),
        "input.pointer.down" or "input.pointer.up" => new Cell($"event:{entry.Name}:producer", "covered",
            "夹具投递（§3 的「被 host 委托」路径）+ PetInstance.PointerDown/Up 的会话面（§11 §5）"),
        "host.suspend" => new Cell($"event:{entry.Name}:producer", "covered",
            "§6.5：Suspend 前**先征询** bail；bail-veto 夹具判「阻止」与「放行」两条子断言"),
        "host.resume" => new Cell($"event:{entry.Name}:producer", "covered",
            "§6.6：先发 host.resume 再改状态；由事件在投递序里早于状态转移判定"),
        "core.window.moved" => new Cell($"event:{entry.Name}:producer", "covered",
            "§6.2：moveTo 映射；同 tick 两条 moveTo 只发一条、position 取后者"),
        "core.window.focus" => new Cell($"event:{entry.Name}:producer", "covered",
            "夹具注入焦点变化 + WorldSnapshot.FocusedApp 同时在场（§7.1 的字段填充）"),
        "core.window.dpiChanged" => new Cell($"event:{entry.Name}:producer", "covered",
            "夹具注入 WM_DPICHANGED 形态 payload；真实消息分支归 t2（LayeredFrameWindow 已处理该消息）"),
        "pet.state.commit" => new Cell($"event:{entry.Name}:producer", "partial",
            "只覆盖分发器（§2.4 的 waterfall 三跳 + 深拷贝）；**生产触发源在 M4 不存在**"
            + "（commit 由插件触发，M4 没有写入者）—— §2.6 已诚实登记，t2 的 --eventtest 输出 SKIP no-producer"),
        "pet.state.migrated" => new Cell($"event:{entry.Name}:producer", "partial",
            "名字与分发口径覆盖；**M4 无生产触发源**（§2.6 逐字登记）"),
        "input.key.down" or "input.key.up" => new Cell($"event:{entry.Name}:producer", "partial",
            "payload 与「无源」的法律事实覆盖；**触发源依赖授权**（§7.3：未授权时不激活钩子，"
            + "M4 不实现任何全局钩子 → setwindowshookex-sites=0）"),
        _ => new Cell($"event:{entry.Name}:producer", "uncovered",
            "未登记原因（本行出现即判据失败：每一格必须有具名原因）"),
    };

    /// <summary>五种 dispatch 逐模式的覆盖状态。</summary>
    public static IReadOnlyList<Cell> Modes() => new[]
    {
        new Cell("dispatch:emit", "partial",
            "§1.4 的 after-fault-ran=true（异常不阻止后续监听者）需 t2 的 EventBus；"
            + "判据已写好（DispatchContractTests.emit-after-fault），SKIP 原因 = EventBus 未出现"),
        new Cell("dispatch:parallel", "partial",
            "§1.4 的并发需 t2 的 EventBus；层 1 的等价判据是投递序 FIFO（core 日志的先后）"),
        new Cell("dispatch:serial", "partial",
            "§1.4 的 saw-previous=true 需 t2 的 DispatchRound；层 1 判据 = core.clock.tick 每帧必达且按序"),
        new Cell("dispatch:bail", "partial",
            "§1.4 的 stopped-at=1 与 fail-closed（异常按 true）需 t2 的 EventBus；bail-veto 夹具已写好两个子断言"),
        new Cell("dispatch:waterfall", "partial",
            "§2.4 的三跳 + 深拷贝需 t2 的 DispatchPetStateCommit 入口；判据已写好（WaterfallContractTests）"),
    };

    /// <summary>13 名 × 5 模式的交叉格 —— 每格都必须有状态与原因（不适用也要写清为什么）。</summary>
    public static IReadOnlyList<Cell> Cross()
    {
        var cells = new List<Cell>();
        foreach (CatalogEntry entry in EventCatalogTable.All)
        {
            foreach (DispatchMode mode in Enum.GetValues<DispatchMode>())
            {
                cells.Add(entry.Mode == mode
                    ? new Cell($"cross:{entry.Name}:{mode}", "covered",
                        "本事件在 §2.1 注册的模式；夹具按它构造")
                    : new Cell($"cross:{entry.Name}:{mode}", "n-a",
                        $"本事件在 §2.1 注册为 {entry.Mode}，不以 {mode} 分发 —— 交叉格不适用（不是未覆盖）"));
            }
        }

        return cells;
    }

    /// <summary>一行可判定的汇总（t5 逐条贴原文用）。</summary>
    public static string Describe()
    {
        IReadOnlyList<Cell> events = Events();
        IReadOnlyList<Cell> modes = Modes();
        IReadOnlyList<Cell> cross = Cross();
        return $"events={EventCatalogTable.All.Length} fixtures={EventCatalogTable.All.Length}"
            + $" cells={events.Count} covered={Count(events, "covered")} partial={Count(events, "partial")}"
            + $" uncovered={Count(events, "uncovered")}"
            + $" modes={modes.Count} modes-covered={Count(modes, "covered")}"
            + $" modes-partial={Count(modes, "partial")}"
            + $" cross={cross.Count} cross-covered={Count(cross, "covered")} cross-n-a={Count(cross, "n-a")}";
    }

    private static int Count(IReadOnlyList<Cell> cells, string status)
    {
        int n = 0;
        foreach (Cell cell in cells)
        {
            if (string.Equals(cell.Status, status, StringComparison.Ordinal))
            {
                n++;
            }
        }

        return n;
    }
}
