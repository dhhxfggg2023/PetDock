// 夹具构造器（docs/41 D-13b 的纪律：夹具是**构造器产物**，不是手工写下的语料；
// D-13a 的纪律：不进仓库 —— 这里只写 %TEMP%，由 FixtureDirectory.Dispose 删除）。
//
// 13 条夹具的分工（对应 t1 §8.4 的五条负例 + §2/§3 的 13 条事件覆盖）：
//   base-8frames   8 帧的干净基线：三种核心事件 + 五条指令 + 五种模式各至少出现一次；
//                  也是 §8.3 确定性与 §8.2 五条规则的正向载体（N5 元判据用的就是它的变体）。
//   outside-catalog N1：表外事件 input.pointer.hover 与合法 core.clock.tick 同批 —— 拒收不许
//                   连带丢掉合法事件（§2.2 判据 3 的三个子断言）。
//   bail-veto      N2：bail 监听者抛异常 → 阻止 Suspend（§6.5）。
//   capability-timeout N3：注入时钟推 201ms → refused/timeout，审计恰好一行（§4.4）。
//   brain-fault    N4：Brain.Tick 抛异常 → 本 tick 指令全部作废（§11:259 的回归保护）。
//   tod-drift      N5：同一夹具的两次重放 **tod 差 1** → 确定性判据必须 FAIL（元判据）。
//
// 另有 8 条「一事件一夹具」（每帧一条事件，带自己的 payload），用来把 §2/§3 的 13 条逐条喂进总线。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace PetDock.EventReplay.Tests;

/// <summary>全部夹具的构造器。键 = 夹具名（去掉 .replay.jsonl 后缀）。</summary>
public static class FixtureConstructor
{
    /// <summary>干净基线的帧数（§8.3 的 Replay.NonEmpty 判 lines ≥ 8 就是按它定的）。</summary>
    public const int BaselineFrames = 8;

    /// <summary>全部夹具：6 条专名 + 13 条「一条事件一个夹具」（§8.4 的 N1..N5 在内）。</summary>
    public static IReadOnlyDictionary<string, ReplayFixture> All()
    {
        var fixtures = new Dictionary<string, ReplayFixture>(StringComparer.Ordinal)
        {
            ["base-8frames"] = Baseline("base-8frames"),
            ["tod-drift"] = Baseline("tod-drift"),
            ["outside-catalog"] = OutsideCatalog(),
            ["bail-veto"] = BailVeto(),
            ["capability-timeout"] = CapabilityTimeout(),
            ["brain-fault"] = BrainFault(),
        };

        foreach (string name in EventCatalogTable.Names)
        {
            fixtures["single-" + name] = SingleEvent(name);
        }

        return fixtures;
    }

    /// <summary>8 帧基线。世界按 §7.1 的五字段给（光标在窗口内 → 非 null；第 6 帧移出窗口 → null），
    /// 每帧推进 dtMs=33（累计），tod 按注入值逐帧 +1（**不读墙钟**）。</summary>
    private static ReplayFixture Baseline(string name)
    {
        var frames = new List<FixtureFrame>();
        for (int i = 0; i < BaselineFrames; i++)
        {
            int cursorX = i < 5 ? 12 + (i * 3) : 400; // 第 6 帧起移出 256 宽的窗口 → Cursor 必须为 null
            var world = new WorldSnapshotView(
                new[] { cursorX, 40 },
                new[] { 0, 0, 256, 256 },
                i % 2 == 0 ? "notepad" : null,
                Occluded: i == 7);

            var events = new List<FixtureEvent>
            {
                new("core.clock.tick", Payload(("dt", 33))),
            };
            if (i is 0 or 2 or 4)
            {
                events.Add(new FixtureEvent("input.pointer.move",
                    i == 0
                        ? Payload(("pos", new[] { cursorX, 40 }))                              // §2.5：首次不写 velocity
                        : Payload(("pos", new[] { cursorX, 40 }), ("velocity", 0.4))));
            }

            if (i == 1)
            {
                events.Add(new FixtureEvent("input.pointer.down", Payload(("button", 0), ("windowPoint", new[] { 12, 40 }))));
            }

            if (i == 3)
            {
                events.Add(new FixtureEvent("input.pointer.up", Payload(("button", 0), ("windowPoint", new[] { 12, 40 }))));
            }

            if (i == 5)
            {
                events.Add(new FixtureEvent("core.window.focus", Payload(("focused", true))));
            }

            frames.Add(new FixtureFrame(
                Seq: i,
                DtMs: 33,
                Tod: 600 + i,
                World: world,
                Events: events,
                Brain: BrainForFrame(i),
                Expected: ExpectedForFrame(i, cursorX),
                Session: i == 1 ? "s1" : null));
        }

        return new ReplayFixture(name, frames);
    }

    /// <summary>基线每帧的脚本化 brain 指令（**乱序**给出；期望按 kind 降序 —— 见 ReplayFixture.cs 文件头）。
    /// 第 4 帧刻意把三条不同 kind 的指令乱序给出，使「仲裁序 ≠ 到达序」（§5.3 的第 3 条断言）在层 1 就能判。</summary>
    private static IReadOnlyList<FixtureDirective> BrainForFrame(int i) => i switch
    {
        0 => new[]
        {
            new FixtureDirective("requestCapability", Capability: "input.mousePosition", Reason: "need cursor"),
            new FixtureDirective("setAnimation", Clip: "idle"),
        },
        2 => new[]
        {
            new FixtureDirective("emit", EventType: "core.window.moved"),
            new FixtureDirective("moveTo", At: new[] { 10, 20 }),
        },
        4 => new[]
        {
            new FixtureDirective("emit", EventType: "core.window.focus"),
            new FixtureDirective("setAnimation", Clip: "hover", Restart: true),
            new FixtureDirective("suspend", Reason: "budget"),
        },
        _ => Array.Empty<FixtureDirective>(),
    };

    /// <summary>基线每帧的期望（仲裁后序 + 投递序 + 拒绝集）。
    /// ⚠️ 期望的事件集必须与**实际会投递的**事件逐一对应：重放引擎由夹具驱动，每帧都会投
    /// `core.clock.tick`（8 帧都有），所以期望里也必须每帧都有它 —— 本会话实测踩过「suspended
    /// 之后就不期望了」的写法，那会让投递序判据自己错。</summary>
    private static Expected ExpectedForFrame(int i, int cursorX)
    {
        var directives = new List<FixtureDirective>();
        var events = new List<FixtureEvent>
        {
            new("core.clock.tick", Payload(("dt", 33))),
        };

        switch (i)
        {
            case 0:
                directives.Add(new FixtureDirective("setAnimation", Clip: "idle"));
                directives.Add(new FixtureDirective("requestCapability", Capability: "input.mousePosition", Reason: "need cursor"));
                events.Add(new FixtureEvent("input.pointer.move", Payload(("pos", new[] { cursorX, 40 }))));
                break;
            case 1:
                events.Add(new FixtureEvent("input.pointer.down", Payload(("button", 0), ("windowPoint", new[] { 12, 40 }))));
                break;
            case 2:
                directives.Add(new FixtureDirective("moveTo", At: new[] { 10, 20 }));
                directives.Add(new FixtureDirective("emit", EventType: "core.window.moved"));
                events.Add(new FixtureEvent("input.pointer.move",
                    Payload(("pos", new[] { cursorX, 40 }), ("velocity", 0.4))));
                break;
            case 3:
                events.Add(new FixtureEvent("input.pointer.up", Payload(("button", 0), ("windowPoint", new[] { 12, 40 }))));
                break;
            case 4:
                directives.Add(new FixtureDirective("suspend", Reason: "budget"));
                directives.Add(new FixtureDirective("setAnimation", Clip: "hover", Restart: true));
                directives.Add(new FixtureDirective("emit", EventType: "core.window.focus"));
                events.Add(new FixtureEvent("input.pointer.move",
                    Payload(("pos", new[] { cursorX, 40 }), ("velocity", 0.4))));
                // 本帧的 brain **没有** moveTo，所以这里不能期望 core.window.moved（§6.2 只由 moveTo 发）。
                events.Add(new FixtureEvent("core.window.focus", Payload(("focused", true))));
                events.Add(new FixtureEvent("host.suspend", Payload(("reason", "budget"))));
                break;
            case 5:
                events.Add(new FixtureEvent("core.window.focus", Payload(("focused", true))));
                break;
            default:
                break;
        }

        return new Expected(directives, events, Array.Empty<string>());
    }

    /// <summary>N1：表外事件与合法事件同批（§2.2 判据 3 的 (a)(b)(c) 三个子断言）。
    /// 它是**负例夹具** —— 因此 FixtureRules 校验时带 allowOutsideCatalog=true。</summary>
    private static ReplayFixture OutsideCatalog() => new(
        "outside-catalog",
        new[]
        {
            new FixtureFrame(0, 33, 600,
                new WorldSnapshotView(new[] { 12, 40 }, new[] { 0, 0, 256, 256 }, null, false),
                new[]
                {
                    new FixtureEvent("input.pointer.hover", Payload(("pos", new[] { 12, 40 }))), // 表外
                    new FixtureEvent("core.clock.tick", Payload(("dt", 33))),                   // 合法：必须仍进日志
                },
                Array.Empty<FixtureDirective>(),
                new Expected(
                    Array.Empty<FixtureDirective>(),
                    new[] { new FixtureEvent("core.clock.tick", Payload(("dt", 33))) },
                    new[] { "input.pointer.hover" }),
                null),
        });

    /// <summary>N2：bail 监听者抛异常 → 阻止 suspend（§6.5 + §1.2 子规则 1 的 fail-closed）。</summary>
    private static ReplayFixture BailVeto() => new(
        "bail-veto",
        new[]
        {
            new FixtureFrame(0, 33, 600,
                new WorldSnapshotView(new[] { 12, 40 }, new[] { 0, 0, 256, 256 }, null, false),
                new[] { new FixtureEvent("core.clock.tick", Payload(("dt", 33))) },
                new[] { new FixtureDirective("suspend", Reason: "budget") },
                new Expected(
                    Array.Empty<FixtureDirective>(),
                    new[] { new FixtureEvent("host.suspend", Payload(("reason", "budget"))) },
                    new[] { "suspend-blocked" }),
                null),
        });

    /// <summary>N3：requestCapability 注入时钟推 201ms → refused/timeout（§4.4）。</summary>
    private static ReplayFixture CapabilityTimeout() => new(
        "capability-timeout",
        new[]
        {
            new FixtureFrame(0, 33, 1200,
                new WorldSnapshotView(new[] { 12, 40 }, new[] { 0, 0, 256, 256 }, null, false),
                new[] { new FixtureEvent("core.clock.tick", Payload(("dt", 33))) },
                new[] { new FixtureDirective("requestCapability", Capability: "input.keyboardHook", Reason: "listen keys") },
                new Expected(
                    Array.Empty<FixtureDirective>(),
                    Array.Empty<FixtureEvent>(),
                    new[] { "requestCapability:input.keyboardHook:refused:timeout:latency=201" }),
                null),
        });

    /// <summary>N4：Brain.Tick 抛异常 → 本 tick 的 directives 全部作废（docs/11:259 的回归保护）。</summary>
    private static ReplayFixture BrainFault() => new(
        "brain-fault",
        new[]
        {
            new FixtureFrame(0, 33, 600,
                new WorldSnapshotView(new[] { 12, 40 }, new[] { 0, 0, 256, 256 }, null, false),
                new[] { new FixtureEvent("core.clock.tick", Payload(("dt", 33))) },
                new[] { new FixtureDirective("setAnimation", Clip: "idle") },
                new Expected(
                    Array.Empty<FixtureDirective>(),
                    Array.Empty<FixtureEvent>(),
                    new[] { "brain-tick-failed" }),
                null),
        });

    /// <summary>一条事件一个夹具：把 §2/§3 的 13 条逐条喂进总线（payload 按 §2.3 口径造）。</summary>
    private static ReplayFixture SingleEvent(string type)
    {
        JsonObject payload = type switch
        {
            "core.clock.tick" => Payload(("dt", 33)),
            "core.window.dpiChanged" => Payload(("scale", 1.5), ("rect", new[] { 0, 0, 256, 256 })),
            "core.window.moved" => Payload(("windowId", "pet-1"), ("position", new[] { 10, 20 })),
            "core.window.focus" => Payload(("focused", true)),
            "host.suspend" => Payload(("reason", "budget")),
            "host.resume" => Payload(("reason", "recovered")),
            "pet.state.commit" => Payload(("petId", "pet-1")),
            "pet.state.migrated" => Payload(("from", "1"), ("to", "2")),
            "input.pointer.move" => Payload(("pos", new[] { 12, 40 })),
            "input.pointer.down" => Payload(("button", 0), ("windowPoint", new[] { 12, 40 })),
            "input.pointer.up" => Payload(("button", 0), ("windowPoint", new[] { 12, 40 })),
            "input.key.down" => Payload(("vkey", 65), ("modifiers", 0)),
            "input.key.up" => Payload(("vkey", 65), ("modifiers", 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "表外名字不得构造夹具（§2.1）"),
        };

        bool isKey = type.StartsWith("input.key.", StringComparison.Ordinal);
        return new ReplayFixture(
            "single-" + type,
            new[]
            {
                new FixtureFrame(0, 33, 600,
                    new WorldSnapshotView(new[] { 12, 40 }, new[] { 0, 0, 256, 256 }, null, false),
                    new[] { new FixtureEvent(type, payload) },
                    Array.Empty<FixtureDirective>(),
                    new Expected(
                        Array.Empty<FixtureDirective>(),
                        new[] { new FixtureEvent(type, payload) },
                        isKey ? new[] { "capability-not-granted" } : Array.Empty<string>()),
                    null),
            });
    }

    /// <summary>payload 构造小工具（camelCase 键，§2.3）。</summary>
    private static JsonObject Payload(params (string Key, object? Value)[] pairs)
    {
        var node = new JsonObject();
        foreach ((string key, object? value) in pairs)
        {
            switch (value)
            {
                case null:
                    break; // §2.3：缺省（可空）字段**不写**，不写 null
                case int i:
                    node[key] = i;
                    break;
                case double d:
                    node[key] = d;
                    break;
                case bool b:
                    node[key] = b;
                    break;
                case string s:
                    node[key] = s;
                    break;
                case int[] array:
                    var json = new JsonArray();
                    foreach (int item in array)
                    {
                        json.Add(item);
                    }

                    node[key] = json;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(pairs), value, "payload 值类型不在 §2.3 口径内");
            }
        }

        return node;
    }
}

