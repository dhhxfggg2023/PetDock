// t1 契约 §8.2/§8.3/§8.4 的判据主体：夹具格式与规则、确定性、五条负例。
//
// 每条断言下面写「什么情况下会红」。这里每一条都跑**真的 core 代码**（HostArbiter.Sorted /
// HostEventDispatch / PetInstance 的公开链），不是替身。层 2（EventBus / CapabilityBroker）的判据
// 在 DispatchContractTests 与 BrokerContractTests，它们 SKIP 时写明具名原因（§2.6：SKIP 不是 PASS）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;
using Xunit;

namespace PetDock.EventReplay.Tests;

/// <summary>夹具格式、确定性、负例（§8.2/§8.3/§8.4）。</summary>
public sealed class EventReplayTests
{
    /// <summary>§8.2 规则 1：seq 从 0 连续递增；断号 → fixture-seq-gap at={n}。
    /// 红：夹具行数/seq 对不上 —— 「第几帧」与「第几行」分叉，之后每条判据都不可信。</summary>
    [Fact]
    public void FixtureSequencesAreContiguous()
    {
        int checkedCount = 0;
        foreach (string name in Names())
        {
            ReplayFixture fixture = Fixtures.Read(name);
            var gaps = Violations(fixture, name).Where(v => v.Rule == "fixture-seq-gap").ToArray();
            Assert.True(fixture.Frames.Count > 0, $"{name}: 夹具为空");
            Assert.True(gaps.Length == 0, $"{name}: 构造器产出断号 {Join(gaps)}");

            // 反例自证：把**第 2 行**（若存在）的 seq 改坏，这条规则必须报出来。
            if (fixture.Frames.Count >= 2)
            {
                ReplayFixture broken = fixture with
                {
                    Frames = fixture.Frames.Select((f, i) => i == 1 ? f with { Seq = 99 } : f).ToArray(),
                };
                Assert.True(
                    Violations(broken, name).Any(v => v.Rule == "fixture-seq-gap"),
                    $"{name}: seq 被改坏后 fixture-seq-gap 没报出来（规则是空转的）");
            }
            else
            {
                // 单帧夹具没有「第 2 行」可改 —— 反例自证改在第 0 行（seq 99 ≠ 0）。
                ReplayFixture broken = fixture with
                {
                    Frames = fixture.Frames.Select((f, i) => i == 0 ? f with { Seq = 99 } : f).ToArray(),
                };
                Assert.True(
                    Violations(broken, name).Any(v => v.Rule == "fixture-seq-gap"),
                    $"{name}: seq 被改坏后 fixture-seq-gap 没报出来（规则是空转的）");
            }

            checkedCount++;
        }

        Assert.True(checkedCount >= 19, $"夹具数 {checkedCount} < 19（6 条专名 + 13 条一事件一夹具）");
    }

    /// <summary>§8.2 规则 2 的夹具侧半边：夹具正文不得出现本机时刻（wall / now / ms / ticks）。
    /// 红：夹具把墙钟读进来 —— 重放不再是可复现的，确定性会在不同机器上飘。</summary>
    [Fact]
    public void FixturesCarryNoWallClockFields()
    {
        foreach (string name in Names())
        {
            ReplayFixture fixture = Fixtures.Read(name);
            var clock = Violations(fixture, name).Where(v => v.Rule == "fixture-wall-clock").ToArray();
            Assert.True(clock.Length == 0, $"{name}: {Join(clock)}");

            // 反例自证：塞一个 wallMs 键进正文，必须报出来。
            string tainted = Fixtures.RawText(name).Replace("\"dtMs\"", "\"wallMs\"", StringComparison.Ordinal);
            Assert.True(
                FixtureRules.Validate(fixture, tainted, IsNegative(name))
                    .Any(v => v.Rule == "fixture-wall-clock"),
                $"{name}: 正文里出现 wallMs 键也没报出来 —— 墙钟禁令是空转的");
        }
    }

    /// <summary>§8.2 规则 5：夹具里的 type 必须 ∈ §2.1 的 13 个（负例夹具除外）。
    /// 红：夹具悄悄用了表外名字 —— 会把「表外不得出现」这条硬约束的判据自己污染掉。</summary>
    [Fact]
    public void FixturesStayInsideTheCatalog()
    {
        var offenders = new List<string>();
        foreach (string name in Names())
        {
            if (IsNegative(name))
            {
                continue;
            }

            foreach (FixtureViolation violation in Violations(Fixtures.Read(name), name)
                .Where(v => v.Rule == "fixture-unknown-type"))
            {
                offenders.Add($"{name}: {violation}");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("; ", offenders));

        // 反例自证：把一条合法事件换成表外名字（不声明为负例夹具），必须报出来。
        ReplayFixture baseline = Fixtures.Read("base-8frames");
        ReplayFixture mutated = baseline with
        {
            Frames = baseline.Frames.Select((f, i) => i == 0
                ? f with { Events = new[] { new FixtureEvent("input.keyboard.secret", null) } }
                : f).ToArray(),
        };
        Assert.True(
            FixtureRules.Validate(mutated, ReplayFixtureIo.Join(mutated), allowOutsideCatalog: false)
                .Any(v => v.Rule == "fixture-unknown-type"),
            "表外事件名没被 R5 报出来");
    }

    /// <summary>§8.2 规则 3：expected.directives 按 kind 降序（同 kind 保序）。
    /// 红：期望里的指令序不是仲裁序 —— R3 与 R4 的「两种口径」就只剩一种，判据退化成同义反复。</summary>
    [Fact]
    public void ExpectedDirectivesAreInArbitrationOrder()
    {
        foreach (string name in Names())
        {
            ReplayFixture fixture = Fixtures.Read(name);
            Assert.True(
                !Violations(fixture, name).Any(v => v.Rule == "fixture-directive-order"),
                $"{name}: {Join(Violations(fixture, name).Where(v => v.Rule == "fixture-directive-order").ToArray())}");

            // 反例自证：把期望顺序倒过来，必须报出来。
            if (fixture.Frames.Any(f => f.Expected.Directives.Count >= 2))
            {
                ReplayFixture broken = fixture with
                {
                    Frames = fixture.Frames.Select(f => f.Expected.Directives.Count >= 2
                        ? f with { Expected = f.Expected with { Directives = f.Expected.Directives.Reverse().ToArray() } }
                        : f).ToArray(),
                };
                Assert.True(
                    Violations(broken, name).Any(v => v.Rule == "fixture-directive-order"),
                    $"{name}: 期望顺序被倒过来后 R3 没报出来");
            }
        }
    }

    /// <summary>§8.2 规则 4 的机器可判定半边：期望里不许出现「输入里没有」的事件（投递序的幻影）。
    /// 红：期望凭空多一条事件 —— 投递序判据失去前提（它拿什么当参照）。</summary>
    [Fact]
    public void ExpectedEventsHaveNoPhantoms()
    {
        foreach (string name in Names())
        {
            ReplayFixture fixture = Fixtures.Read(name);
            Assert.True(
                !Violations(fixture, name).Any(v => v.Rule == "fixture-event-phantom"),
                $"{name}: {Join(Violations(fixture, name).Where(v => v.Rule == "fixture-event-phantom").ToArray())}");
        }

        // 反例自证：给某帧期望里塞一条输入里不存在的事件。
        ReplayFixture baseline = Fixtures.Read("base-8frames");
        ReplayFixture mutated = baseline with
        {
            Frames = baseline.Frames.Select((f, i) => i == 3
                ? f with
                {
                    Expected = f.Expected with
                    {
                        Events = f.Expected.Events.Concat(new[] { new FixtureEvent("core.window.dpiChanged", null) }).ToArray(),
                    },
                }
                : f).ToArray(),
        };
        Assert.True(
            FixtureRules.Validate(mutated, ReplayFixtureIo.Join(mutated), allowOutsideCatalog: false)
                .Any(v => v.Rule == "fixture-event-phantom"),
            "期望里的幻影事件没被报出来");
    }

    /// <summary>§8.2 规则 4 的语义半边：夹具**声明**的期望投递序与 core 实际日志的投递序一致。
    /// 红：core 的投递序与期望分叉 —— 事件进 Brain 的先后不可复现（docs/11:137 的 FIFO 保证）。
    /// 这条读的是 core 自己的 `HostEventDispatch.Log`，不是我这边的记账。</summary>
    [Fact]
    public void DeliveryOrderMatchesCoreLogFifo()
    {
        ReplayFixture fixture = Fixtures.Read("base-8frames");
        using CoreDrive drive = CoreDrive.Create();

        int compared = 0;
        foreach (FixtureFrame frame in fixture.Frames)
        {
            foreach (FixtureEvent evt in frame.Events)
            {
                drive.Publish(evt.Type, evt.Payload, frame.Seq);
            }

            var inputTypes = frame.Events.Select(e => e.Type).ToArray();
            var derived = ReplayEngine.EventsDerivedFromDirectives(frame);
            var expectedInputs = frame.Expected.Events.Select(e => e.Type)
                .Where(t => !derived.Contains(t)).ToArray();
            Assert.Equal(inputTypes, expectedInputs);
            compared += expectedInputs.Length;
        }

        Assert.True(compared > 0, "没有比较任何投递序 —— 判据是空转的");

        // core 的日志与注入序一致：FIFO 的前提是**同一次分发内**的先后。指令派生的事件
        // （moveTo → core.window.moved 等）在指令落地时才追加到日志尾部，属于 §5.3 的裁决序，
        // 与 §11:137 的投递序**刻意不同** —— 所以这里只比较「输入事件」那一段的相对序。
        var injectedInputs = fixture.Frames.SelectMany(f => f.Events.Select(e => e.Type)).ToArray();
        var loggedInputs = drive.Dispatch.Log.Select(e => e.Type)
            .Where(ReplayEngine.IsInputEvent)
            .ToArray();
        Console.WriteLine($"EVENT-CHECK Note.DeliveryOrder INFO injected=[{string.Join(",", injectedInputs)}]"
            + $" logged=[{string.Join(",", loggedInputs)}]");
        Assert.Equal(injectedInputs, loggedInputs);

        // 反例自证：把注入序倒过来，日志必须跟着倒（证明它不是常量）。
        using CoreDrive reversed = CoreDrive.Create();
        foreach (string type in injectedInputs.Reverse())
        {
            reversed.Publish(type, null, 0);
        }

        Assert.Equal(injectedInputs.Reverse().ToArray(), reversed.Dispatch.Log.Select(e => e.Type));
    }

    /// <summary>§8.3：同一夹具连跑两次，规范行 sha256 相等（确定性）。
    /// 红：core 的行为依赖本机状态（墙钟 / 句柄 / 计时），重放不可复现。</summary>
    [Fact]
    public void ReplayIsDeterministic()
    {
        var failures = new List<string>();
        foreach (string name in Names())
        {
            DeterminismVerdict verdict = ReplayEngine.Judge(Fixtures.Read(name));
            if (!verdict.Pass)
            {
                failures.Add(verdict.Detail);
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    /// <summary>§8.3 的反向自证：原始行**必须真的不同**（否则「两次规范行相等」是同义反复）。
    /// 红：抹除器把两次都抹成空串 —— 那条 PASS 什么都没证明。</summary>
    [Fact]
    public void VolatileFieldsAreReallyStripped()
    {
        ReplayFixture fixture = Fixtures.Read("base-8frames");
        ReplayRun first = ReplayEngine.Run(fixture);
        ReplayRun second = ReplayEngine.Run(fixture);

        Assert.True(
            !string.Equals(first.RawText, second.RawText, StringComparison.Ordinal),
            "两次重放的**原始**行完全相同 —— 抹除器没有东西可抹，确定性判据退化");
        Assert.Equal(first.CanonicalSha, second.CanonicalSha);

        // 抹除物逐个列出，且规范行里真的不再出现它们。
        Assert.Contains("elapsed=", Canonicalizer.StrippedFieldNames);
        Assert.Contains("run=", Canonicalizer.StrippedFieldNames);
        Assert.False(first.CanonicalText.Contains("elapsed=", StringComparison.Ordinal), "规范行里仍有 elapsed=");
        Assert.False(first.CanonicalText.Contains(" run=", StringComparison.Ordinal), "规范行里仍有 run=");

        // 保留物必须还在（§8.3 逐字：clip / position / verdict / count）—— 防「抹掉太多」的同义反复。
        Assert.Contains("clip=", first.CanonicalText);
        Assert.Contains("type=", first.CanonicalText);
        Assert.Contains("payload=", first.CanonicalText);
    }

    /// <summary>§8.3 的反向自证（防「两次都空」的假绿）：规范行数必须够（基线 ≥8）。
    /// 红：夹具被删空 —— 空的「相等」是最容易被当成绿的假绿。
    /// 短夹具（单帧的 single-*/N1..N4）按帧数缩放判据（见 <see cref="ReplayEngine.Judge"/> 的 floor）。</summary>
    [Fact]
    public void ReplayOutputIsNotEmpty()
    {
        foreach (string name in Names())
        {
            ReplayFixture fixture = Fixtures.Read(name);
            ReplayRun run = ReplayEngine.Run(fixture);
            int floor = Math.Min(8, 1 + (fixture.Frames.Count * 5));
            Assert.True(
                run.CanonicalLineCount >= floor,
                $"{name}: 规范行只有 {run.CanonicalLineCount} 行（{fixture.Frames.Count} 帧，下界 {floor}）");
        }

        // 基线必须达到契约 §8.3 的硬数 8（它是 8 帧夹具，8 行是它的结构下界）。
        Assert.True(ReplayEngine.Run(Fixtures.Read("base-8frames")).CanonicalLineCount >= 8);
    }

    /// <summary>§8.3 的显式清单：抹掉的字段名必须逐字列出，且**不许**把可观测判据也抹掉。
    /// 红：抹除清单被扩到 clip/position/verdict/count —— 判据变成同义反复。</summary>
    [Fact]
    public void StrippedFieldListIsExplicitAndBounded()
    {
        IReadOnlyList<string> stripped = Canonicalizer.StrippedFieldNames;
        Assert.True(stripped.Count is > 0 and <= 8, $"抹除清单长度 {stripped.Count} 不合理");

        // 判据不可被抹：这些 token 一旦进抹除清单，「两次相等」就退化成同义反复。
        foreach (string forbidden in new[] { "clip=", "position=", "verdict=", "count=", "type=", "payload=" })
        {
            Assert.False(stripped.Contains(forbidden, StringComparer.Ordinal), $"抹除清单里不该有 {forbidden}");
        }

        Assert.Contains("elapsed=", stripped);
        Assert.Contains("run=", stripped);
        foreach (string retained in Canonicalizer.RetainedFieldNames)
        {
            Assert.False(stripped.Contains(retained, StringComparer.Ordinal), $"抹除清单与保留清单重叠：{retained}");
        }

        // 抹除只抹那个 token 的**值**，不是整行删掉。
        string cleaned = Canonicalizer.Strip("FRAME seq=3 run=7 dispatched=2 elapsed=1.234 clip=idle");
        Assert.Contains("seq=3", cleaned, StringComparison.Ordinal);
        Assert.Contains("dispatched=2", cleaned, StringComparison.Ordinal);
        Assert.Contains("clip=idle", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("elapsed", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("run=", cleaned, StringComparison.Ordinal);
    }

    /// <summary>§8.4 N5（**元判据**）：同一夹具两次结果**故意**不同（tod 差 1）→ Deterministic 必须 FAIL。
    /// 这条最要命 —— 没有它，Deterministic 可能是「什么都没比较」的假绿。
    /// 同时断言「判据本身能红」与「为什么红」（两个 sha 不同）。</summary>
    [Fact]
    public void N5_MetaCriterionDeterminismJudgeCanFail()
    {
        ReplayFixture fixture = Fixtures.Read("tod-drift");

        DeterminismVerdict clean = ReplayEngine.Judge(fixture);
        Assert.True(clean.Pass, $"不变体应当 PASS，实际 {clean.Detail}");

        DeterminismVerdict drifted = ReplayEngine.Judge(fixture, new RunMutation(TodOffset: 1));
        Assert.False(drifted.BytesEqual, $"tod 差 1 之后规范行仍然相等（{drifted.Detail}）—— 确定性判据是假绿");
        Assert.False(drifted.Pass, "tod 差 1 之后 Deterministic 仍然 PASS");
        Assert.NotEqual(drifted.Run1Sha, drifted.Run2Sha);
        Assert.True(drifted.RawDiffers, "两次原始行相同 —— 观测面本身是空的");

        // 验收③「贴红的那一次原文」：把判据自己渲染的那一行**照原样**打出来（就是 FAIL 那一次）。
        Console.WriteLine($"EVENT-CHECK Negative.RedLine PROOF {drifted.RenderedLine}");
        Assert.StartsWith("EVENT-CHECK Replay.Deterministic FAIL", drifted.RenderedLine, StringComparison.Ordinal);
    }

    /// <summary>§8.4「改一条 payload 字段 / 改一个事件」→ 判据红（验收③逐字）。
    /// 红：payload 的某个字段被改而判据不红 —— 那 payload 就没人看着。</summary>
    [Fact]
    public void NegativeCase_MutatedEventOrPayloadTurnsTheJudgeRed()
    {
        ReplayFixture fixture = Fixtures.Read("base-8frames");
        DeterminismVerdict clean = ReplayEngine.Judge(fixture);
        Assert.True(clean.Pass, $"基线本身应当 PASS：{clean.Detail}");

        // 只改一个 payload 字段的值（dt: 33 → 34），其余一字不动。
        DeterminismVerdict mutated = ReplayEngine.Judge(
            fixture,
            new RunMutation(AtSeq: 2, EventType: "core.clock.tick", Field: "dt", Value: JsonValue.Create(34)));
        Assert.False(mutated.BytesEqual, "改了 core.clock.tick.dt 之后规范行仍然相同");
        Assert.False(mutated.Pass, "改了 payload 字段之后判据仍然 PASS");

        // 改一条事件的 payload 点（pos）同样必须红。
        DeterminismVerdict moved = ReplayEngine.Judge(
            fixture,
            new RunMutation(AtSeq: 0, EventType: "input.pointer.move", Field: "pos", Value: new JsonArray(999, 999)));
        Assert.False(moved.Pass, "改了 input.pointer.move.pos 之后判据仍然 PASS");

        Console.WriteLine(
            $"EVENT-CHECK Replay.NegativePayload PASS run1=sha256:{mutated.Run1Sha} run2=sha256:{mutated.Run2Sha}"
            + " bytes-equal=false");
    }

    /// <summary>N1（§8.4 / §2.2 判据 3）：表外事件拒收不许连带丢掉合法事件，进程仍 exit 0。
    /// 红：拒收实现成「整批丢弃」—— 一条坏事件能把同批的合法事件一起吃掉。</summary>
    [Fact]
    public void N1_OutsideCatalogIsRejectedButLegalEventSurvives()
    {
        Assert.Contains(Fixtures.Read("outside-catalog").Frames[0].Events, e => e.Type == "input.pointer.hover");

        using CoreDrive drive = CoreDrive.Create();
        drive.Publish("input.pointer.hover", new JsonObject { ["pos"] = new JsonArray(1, 2) }, 0);
        drive.Publish("core.clock.tick", new JsonObject { ["dt"] = 33 }, 0);

        // (c) 合法事件必须仍在（这条层 1 就真跑真过）。
        Assert.Equal(1, drive.CountOf("core.clock.tick"));

        CoreSurfaceReport surface = CoreSurface.Current;
        if (!surface.HasCatalogGate)
        {
            Skip.With(CoreSurface.Reason("Publish 的目录守门（表外不入日志 + OutsideCatalog 计数）", "§2.2 判据 1", "t2"));
            return;
        }

        Assert.Equal(0, drive.CountOf("input.pointer.hover"));
        PropertyInfo counter = typeof(HostEventDispatch).GetProperty(surface.CatalogGateMember.Split('.')[1])!;
        Assert.Equal(1, counter.GetValue(drive.Dispatch));
        Console.WriteLine("EVENT-CHECK Negative.OutsideCatalog PASS rejected=1 outside=0 logged-outside=0 legal-kept=true");
    }

    /// <summary>N4（§8.4）：Brain.Tick 抛异常 → 本 tick 的 directives **全部**作废 + BrainFaults++ + degraded。
    /// 红：坏 brain 的部分指令落地 —— docs/11:259「指令全部作废」被破坏。</summary>
    [Fact]
    public void N4_BrainFaultVoidsTheWholeTick()
    {
        ReplayFixture fixture = Fixtures.Read("brain-fault");
        Assert.Contains("brain-tick-failed", fixture.Frames[0].Expected.Refusals[0]);
        Assert.True(
            !Violations(fixture, "brain-fault").Any(),
            $"brain-fault 夹具自身不合规：{Join(Violations(fixture, "brain-fault").ToArray())}");

        if (!CoreSurface.Current.HasScriptedBrainInjection)
        {
            // PetInstance 的 Brain 是构造器只读注入的，测试侧没有公开写入者 —— 可注入槽位归 t2（§7.4）。
            // 负例夹具与期望已就位；执行面等 t2 的注入点。
            Skip.With(CoreSurface.Reason("PetInstance 的可注入 brain 槽位", "§8.4 N4 / docs/11:259", "t2"));
            return;
        }

        Assert.Fail("brain 注入点已出现：本条应改为真跑（t3 的补测义务）");
    }

    /// <summary>§8.2 规则 2 的驱动侧半边 + t1 §8.3 的 grep 双保险：驱动侧**不许**读墙钟。
    /// 红：驱动里出现 DateTime.Now / Environment.TickCount —— 确定性只能靠运气。</summary>
    [Fact]
    public void DriverDoesNotReadWallClock()
    {
        string sourceDir = SourceDirectory();
        string[] drivers =
        {
            "Replay.cs", "ReplayFixture.cs", "CoreAdapter.cs", "FixtureConstructor.cs", "CoreSurface.cs",
        };

        var offenders = new List<string>();
        foreach (string file in drivers)
        {
            string path = Path.Combine(sourceDir, file);
            Assert.True(File.Exists(path), $"缺源文件 {path}");
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                foreach (string forbidden in new[] { "DateTime.Now", "DateTimeOffset.Now", "Environment.TickCount" })
                {
                    if (line.Contains(forbidden, StringComparison.Ordinal))
                    {
                        offenders.Add($"{file}:{i + 1} {forbidden}");
                    }
                }

                if (line.Contains("Stopwatch.GetTimestamp", StringComparison.Ordinal))
                {
                    offenders.Add($"{file}:{i + 1} Stopwatch.GetTimestamp（只许 Utility.cs 的 Volatile）");
                }
            }
        }

        Assert.True(offenders.Count == 0, "驱动侧出现墙钟调用：\n" + string.Join("\n", offenders));

        // 自证：扫描关键词真的能命中一个样板。
        Assert.True("double t = DateTime.Now.TimeOfDay.TotalMilliseconds;".Contains("DateTime.Now", StringComparison.Ordinal));

        // 唯一被允许的一处：Utility.cs 的 Volatile（它只喂给抹除器，不进判据比较对象）。
        string utility = File.ReadAllText(Path.Combine(sourceDir, "Utility.cs"));
        Assert.Contains("class Volatile", utility);
        Assert.Contains("strippedFields", utility.Replace("StrippedFields", "strippedFields"));
    }

    /// <summary>验收④：逐条列出覆盖 / 未覆盖及原因。这条判据断言 CoverageLedger 的每一格都有
    /// 状态与**具名**原因，且不许出现空话词 —— 覆盖对账本身是可判定的，不是文档。</summary>
    [Fact]
    public void CoverageLedgerIsFullyAttributed()
    {
        IReadOnlyList<CoverageLedger.Cell> events = CoverageLedger.Events();
        IReadOnlyList<CoverageLedger.Cell> modes = CoverageLedger.Modes();
        IReadOnlyList<CoverageLedger.Cell> cross = CoverageLedger.Cross();

        Assert.Equal(EventCatalogTable.All.Length * 2, events.Count);
        Assert.Equal(5, modes.Count);
        Assert.Equal(EventCatalogTable.All.Length * 5, cross.Count);

        string[] vague = { "待定", "稍后", "暂略", "TBD", "unknown", "不清楚", "以后" };
        foreach (CoverageLedger.Cell cell in events.Concat(modes).Concat(cross))
        {
            Assert.True(cell.Subject.Length > 0, "有格子没有主语");
            Assert.True(
                cell.Status is "covered" or "partial" or "uncovered" or "n-a",
                $"{cell.Subject}: 状态 {cell.Status} 不在四值里");
            Assert.True(cell.Reason.Length >= 12, $"{cell.Subject}: 原因太短（「{cell.Reason}」）");
            foreach (string word in vague)
            {
                Assert.False(
                    cell.Reason.Contains(word, StringComparison.Ordinal),
                    $"{cell.Subject}: 原因里出现没有内容的词「{word}」");
            }
        }

        Assert.Equal(0, events.Count(c => c.Status == "uncovered"));
        Assert.Contains(events, c => c.Status == "partial");

        foreach (string name in Names())
        {
            Assert.True(File.Exists(Path.Combine(Fixtures.Root, name + ".replay.jsonl")), $"夹具 {name} 没落盘");
        }

        Console.WriteLine($"EVENT-CHECK Coverage.Ledger PASS {CoverageLedger.Describe()}");
    }
    /// <summary>§2.1 的 13 个名字 + §2.3 的口径 + 五种模式：表内 13、分节 8+5、全部 camelCase。
    /// 红：名字表被改（少一条 / 多一条 / 改拼写）—— 目录是硬约束，它一变判据全部失效。</summary>
    [Fact]
    public void CatalogMirrorHas13NamesWithCamelCaseFields()
    {
        Assert.Equal(13, EventCatalogTable.All.Length);
        Assert.Equal(8, EventCatalogTable.All.Count(e => e.Section == 2));
        Assert.Equal(5, EventCatalogTable.All.Count(e => e.Section == 3));
        Assert.Equal(13, EventCatalogTable.Names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(14, EventCatalogTable.NamesWithExemption.Count);
        Assert.Contains(EventCatalogTable.PendingTableExemption, EventCatalogTable.NamesWithExemption);
        Assert.False(EventCatalogTable.IsKnown(EventCatalogTable.PendingTableExemption));

        Assert.True(EventCatalogTable.FieldNameViolations().Count == 0);
        Assert.Equal(DispatchMode.Bail, EventCatalogTable.ModeOf("host.suspend"));
        Assert.Equal(DispatchMode.Waterfall, EventCatalogTable.ModeOf("pet.state.commit"));
        Assert.Equal(DispatchMode.Serial, EventCatalogTable.ModeOf("core.clock.tick"));
        Assert.Equal(DispatchMode.Emit, EventCatalogTable.ModeOf("input.pointer.down"));
        Assert.Equal(DispatchMode.Parallel, EventCatalogTable.ModeOf("core.window.moved"));
        Assert.Equal(1, EventCatalogTable.All.Count(e => e.Mode == DispatchMode.Bail));
        Assert.Equal(1, EventCatalogTable.All.Count(e => e.Mode == DispatchMode.Waterfall));

        foreach (DispatchMode mode in Enum.GetValues<DispatchMode>())
        {
            Assert.True(EventCatalogTable.Representative.ContainsKey(mode), $"模式 {mode} 没有代表事件");
            Assert.Equal(mode, EventCatalogTable.ModeOf(EventCatalogTable.Representative[mode]));
        }
    }

    /// <summary>§2.1 与 core 的 `EventCatalog`（t2 落地后）逐项比对：core 那份是**运行期唯一**名字表，
    /// 测试侧的镜像只能是镜像。未落地时 SKIP 并写明原因（§2.6）。</summary>
    [Fact]
    public void CatalogMirrorMatchesCoreEventCatalog()
    {
        CoreSurfaceReport surface = CoreSurface.Current;
        if (!surface.HasEventCatalog)
        {
            Skip.With(CoreSurface.Reason("core 的 EventCatalog（运行期唯一名字表）", "§2.1", "t2"));
            return;
        }

        Type catalog = typeof(PetInstance).Assembly.GetType(surface.EventCatalogType)!;
        IReadOnlyList<string>? names = ReadStringCollection(catalog);
        Assert.True(names is not null, $"{surface.EventCatalogType} 上没有可读的字符串集合");
        Assert.Equal(
            EventCatalogTable.NamesWithExemption.OrderBy(n => n, StringComparer.Ordinal),
            names!.OrderBy(n => n, StringComparer.Ordinal));
    }

    private static IReadOnlyList<string>? ReadStringCollection(Type catalog)
    {
        foreach (PropertyInfo property in catalog.GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (property.GetValue(null) is IEnumerable<string> values)
            {
                return values.ToArray();
            }
        }

        foreach (FieldInfo field in catalog.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is IEnumerable<string> values)
            {
                return values.ToArray();
            }
        }

        return null;
    }

    /// <summary>验收①的机器判据（D-13a 纪律的延续）：夹具是**构造器产物**，只落 %TEMP%、退出即删，
    /// 仓库里**不得**存在任何 `.replay.jsonl`。
    /// 红：有人把生成物提交进仓库 —— 那就成了手工语料，构造器与夹具的真实性对账全部失效。
    /// 这条判据读的是**仓库树**（不是 %TEMP%），所以它是真的在守那条纪律。</summary>
    [Fact]
    public void GeneratedFixturesNeverLandInTheRepository()
    {
        string root = RepositoryRoot();
        var committed = new List<string>();
        foreach (string ext in new[] { ".replay.jsonl", ".replay.json" })
        {
            foreach (string path in Directory.EnumerateFiles(root, "*" + ext, SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal))
                {
                    continue;
                }

                committed.Add(rel);
            }
        }

        Assert.True(
            committed.Count == 0,
            $"仓库里出现夹具生成物（D-13a：夹具不入库）：{string.Join(", ", committed)}");

        // 正面：夹具**确实**被构造出来并落盘到了 %TEMP%（否则上面那条就是空转的）。
        string tempPath = Path.Combine(Fixtures.Root, "base-8frames.replay.jsonl");
        Assert.True(File.Exists(tempPath), $"夹具没有落盘：{tempPath}");
        Assert.StartsWith(Path.GetTempPath(), Fixtures.Root, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("petdock-eventreplay-", Fixtures.Root);
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

    private static IReadOnlyList<FixtureViolation> Violations(ReplayFixture fixture, string name) =>
        FixtureRules.Validate(fixture, ReplayFixtureIo.Join(fixture), IsNegative(name));

    private static string Join(IReadOnlyList<FixtureViolation> violations) =>
        string.Join("; ", violations.Select(v => v.ToString()));

    private static IReadOnlyList<string> Names() => Fixtures.Names();

    /// <summary>负例夹具（§8.2 规则 5 的例外）：只有 outside-catalog 一个。</summary>
    private static bool IsNegative(string name) => string.Equals(name, "outside-catalog", StringComparison.Ordinal);

    private static string SourceDirectory()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PetDock.EventReplay.Tests.csproj")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("找不到本工程源树（PetDock.EventReplay.Tests.csproj）");
    }
}
