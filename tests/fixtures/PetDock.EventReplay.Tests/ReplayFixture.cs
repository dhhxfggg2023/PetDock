// t1 契约 §8.2 的夹具格式：`.replay.jsonl`，一行一帧，UTF-8 无 BOM。
//
// 格式逐字按契约 §8.2 的样例；**一处测试侧扩展**（必须在文件头写明，因为契约样例里没有它）：
// 契约样例只给了 `events`（输入）与 `expected`（输出），没说脚本化 brain 的产物从哪来。
// M4 的 Brain 是插件（t4/reactor 的产物），重放测试不能依赖某个具体插件的行为，所以每帧多一个
// `brain.directives` 块 = **本次 tick 由脚本化 brain 交出的指令原序**。它是输入，不是期望；
// 期望仍按 §8.2 的三块（directives / events / refusals）。由此 `expected.directives` 是**仲裁后**
// 的序（§5.3），与 `brain.directives` 的乱序**刻意不同** —— 否则那条判据就是同义反复。
//
// 五条规则（§8.2，每条都是判据）：
//   R1 seq 从 0 连续递增；断号 → fixture-seq-gap at={n}
//   R2 dtMs / tod / world 全部来自夹具，重放期间禁止读墙钟（驱动侧见 Replay.Judge 的 raw-differs）
//   R3 expected.directives 按 kind 降序（DirectiveKind 数值），同 kind 按产生序
//   R4 expected.events 按投递序 FIFO —— R3 与 R4 的顺序口径**不同，是故意的**（§8.2 规则 3/4）
//   R5 夹具里出现的任何事件 type 必须 ∈ §2.1 的 13 个（负例夹具除外，见 §8.4）
// 另加一条 R6（夹具侧半边，本文件自我约束）：夹具 JSON **不得出现** wall / now / ms 这类
// 本机时刻字段名 —— 这条必须对**原始文本**判，因为解析后字段名已经没有了。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PetDock.EventReplay.Tests;

/// <summary>夹具里的一条事件（type + payload）。</summary>
public sealed record FixtureEvent(string Type, JsonObject? Payload);

/// <summary>夹具里的一条指令（输入或期望）。`Kind` 用 core <c>DirectiveKind</c> 的名字
/// （setAnimation / moveTo / emit / requestCapability / suspend / resume），与 ABI 名字逐字一致。</summary>
public sealed record FixtureDirective(
    string Kind,
    string? Clip = null,
    bool Restart = false,
    int[]? At = null,
    string? Capability = null,
    string? Reason = null,
    string? EventType = null);

/// <summary>一帧的期望（§8.2 的 `expected` 三块）。</summary>
public sealed record Expected(
    IReadOnlyList<FixtureDirective> Directives,
    IReadOnlyList<FixtureEvent> Events,
    IReadOnlyList<string> Refusals);

/// <summary>夹具里的世界快照（§7.1 的五个字段，零增零删）。</summary>
public sealed record WorldSnapshotView(
    int[]? Cursor,
    int[] WindowRect,
    string? FocusedApp,
    bool Occluded);

/// <summary>一行 = 一帧。</summary>
public sealed record FixtureFrame(
    int Seq,
    int DtMs,
    int Tod,
    WorldSnapshotView World,
    IReadOnlyList<FixtureEvent> Events,
    IReadOnlyList<FixtureDirective> Brain,
    Expected Expected,
    string? Session);

/// <summary>一个完整夹具。</summary>
public sealed record ReplayFixture(string Name, IReadOnlyList<FixtureFrame> Frames);

/// <summary>夹具违规（规则号 + 位置 + 说明）。</summary>
public sealed record FixtureViolation(string Rule, string Where, string Message)
{
    public override string ToString() => $"{Rule} {Where}: {Message}";
}

/// <summary>`.replay.jsonl` 的读写 + §8.2 五条规则的校验。答案唯一：违规就是违规，不做容错解析。</summary>
public static class ReplayFixtureIo
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>一行一帧地写（UTF-8 无 BOM，LF 结尾，末尾一个换行）。</summary>
    public static void Write(ReplayFixture fixture, string path) =>
        File.WriteAllText(path, Join(fixture), Utf8NoBom);

    /// <summary>夹具的完整文本（写盘前先取文本，便于判 R6 的原始字段名）。</summary>
    public static string Join(ReplayFixture fixture)
    {
        var text = new StringBuilder();
        foreach (FixtureFrame frame in fixture.Frames)
        {
            text.Append(SerializeFrame(frame)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>读回；任何一行不是合法 JSON 都抛（夹具坏了必须看得见，不许静默跳过）。</summary>
    public static ReplayFixture Read(string name, string path)
    {
        var frames = new List<FixtureFrame>();
        foreach (string rawLine in File.ReadAllLines(path, Utf8NoBom))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            JsonNode node = JsonNode.Parse(line)
                ?? throw new InvalidDataException($"夹具行无法解析为 JSON: {line}");
            frames.Add(ParseFrame(node));
        }

        return new ReplayFixture(name, frames);
    }

    private static string SerializeFrame(FixtureFrame frame)
    {
        var root = new JsonObject
        {
            ["seq"] = frame.Seq,
            ["dtMs"] = frame.DtMs,
            ["tod"] = frame.Tod,
            ["world"] = new JsonObject
            {
                ["cursor"] = frame.World.Cursor is null ? null : ToArray(frame.World.Cursor),
                ["windowRect"] = ToArray(frame.World.WindowRect),
                ["focusedApp"] = frame.World.FocusedApp,
                ["occluded"] = frame.World.Occluded,
            },
            ["events"] = EventArray(frame.Events),
            ["brain"] = new JsonObject { ["directives"] = DirectiveArray(frame.Brain) },
            ["session"] = frame.Session,
            ["expected"] = new JsonObject
            {
                ["directives"] = DirectiveArray(frame.Expected.Directives),
                ["events"] = EventArray(frame.Expected.Events),
                ["refusals"] = StringArray(frame.Expected.Refusals),
            },
        };
        // 不传 JsonSerializerOptions：JsonNode.ToJsonString(options) 会把 options 标成只读，
        // 而同一个 JsonObject 在本进程里会被序列化多次（写盘 + 判 R6）—— 传 options 会在**第二次**
        // 调用时抛「must specify a TypeInfoResolver setting before being marked as read-only」。
        // 默认输出本来就是非缩进的紧凑形态，正是夹具要的单行。
        return root.ToJsonString();
    }

    private static JsonArray EventArray(IReadOnlyList<FixtureEvent> events)
    {
        var array = new JsonArray();
        foreach (FixtureEvent evt in events)
        {
            var node = new JsonObject { ["type"] = evt.Type };
            if (evt.Payload is not null)
            {
                node["payload"] = evt.Payload.DeepClone();
            }

            array.Add(node);
        }

        return array;
    }

    private static JsonArray DirectiveArray(IReadOnlyList<FixtureDirective> directives)
    {
        var array = new JsonArray();
        foreach (FixtureDirective directive in directives)
        {
            var node = new JsonObject { ["kind"] = directive.Kind };
            if (directive.Clip is not null)
            {
                node["clip"] = directive.Clip;
                node["restart"] = directive.Restart;
            }

            if (directive.At is not null)
            {
                node["at"] = ToArray(directive.At);
            }

            if (directive.Capability is not null)
            {
                node["capability"] = directive.Capability;
            }

            if (directive.Reason is not null)
            {
                node["reason"] = directive.Reason;
            }

            if (directive.EventType is not null)
            {
                node["eventType"] = directive.EventType;
            }

            array.Add(node);
        }

        return array;
    }

    private static JsonArray StringArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonArray ToArray(int[] values)
    {
        var array = new JsonArray();
        foreach (int value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static FixtureFrame ParseFrame(JsonNode node)
    {
        JsonObject root = node.AsObject();
        JsonObject worldNode = root["world"]?.AsObject() ?? new JsonObject();
        JsonObject expectedNode = root["expected"]?.AsObject() ?? new JsonObject();
        JsonObject brainNode = root["brain"]?.AsObject() ?? new JsonObject();

        return new FixtureFrame(
            Int(root, "seq"),
            Int(root, "dtMs"),
            Int(root, "tod"),
            new WorldSnapshotView(
                IntArray(worldNode["cursor"]),
                IntArray(worldNode["windowRect"]) ?? Array.Empty<int>(),
                Str(worldNode, "focusedApp"),
                Bool(worldNode, "occluded")),
            Events(root["events"]),
            Directives(brainNode["directives"]),
            new Expected(
                Directives(expectedNode["directives"]),
                Events(expectedNode["events"]),
                Strings(expectedNode["refusals"])),
            Str(root, "session"));
    }

    private static IReadOnlyList<FixtureEvent> Events(JsonNode? node)
    {
        var list = new List<FixtureEvent>();
        if (node is not JsonArray array)
        {
            return list;
        }

        foreach (JsonNode? item in array)
        {
            if (item is null)
            {
                continue;
            }

            JsonObject obj = item.AsObject();
            list.Add(new FixtureEvent(Str(obj, "type") ?? string.Empty, obj["payload"]?.AsObject()));
        }

        return list;
    }

    private static IReadOnlyList<FixtureDirective> Directives(JsonNode? node)
    {
        var list = new List<FixtureDirective>();
        if (node is not JsonArray array)
        {
            return list;
        }

        foreach (JsonNode? item in array)
        {
            if (item is null)
            {
                continue;
            }

            JsonObject obj = item.AsObject();
            list.Add(new FixtureDirective(
                Str(obj, "kind") ?? string.Empty,
                Str(obj, "clip"),
                Bool(obj, "restart"),
                IntArray(obj["at"]),
                Str(obj, "capability"),
                Str(obj, "reason"),
                Str(obj, "eventType")));
        }

        return list;
    }

    private static IReadOnlyList<string> Strings(JsonNode? node)
    {
        var list = new List<string>();
        if (node is not JsonArray array)
        {
            return list;
        }

        foreach (JsonNode? item in array)
        {
            list.Add(item?.ToString() ?? string.Empty);
        }

        return list;
    }

    private static int Int(JsonObject obj, string name)
    {
        if (obj[name] is JsonNode node && node.AsValue().TryGetValue(out int value))
        {
            return value;
        }

        return 0;
    }

    private static bool Bool(JsonObject obj, string name)
    {
        if (obj[name] is JsonNode node && node.AsValue().TryGetValue(out bool value))
        {
            return value;
        }

        return false;
    }

    private static string? Str(JsonObject obj, string name)
    {
        if (obj[name] is JsonNode node && node.GetValueKind() == JsonValueKind.String)
        {
            return node.GetValue<string>();
        }

        return null;
    }

    private static int[]? IntArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var values = new int[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            values[i] = array[i]!.GetValue<int>();
        }

        return values;
    }
}

/// <summary>§8.2 的五条规则 + R6（夹具侧墙钟禁令）。每条都能红，各自配一个负例。</summary>
public static class FixtureRules
{
    /// <summary>夹具正文里不许出现这些**键名**（R6：时刻必须来自夹具）。`events[].payload` 里
    /// 的 `dt` 等合法字段名不受影响 —— 判定按 JSON 键名做，不按子串。</summary>
    public static readonly string[] ForbiddenVolatileKeys =
    {
        "wallMs", "wallClock", "stopwatch", "tickCount", "nowMs", "epochMs",
    };

    /// <summary>R6 的**例外**：`wall` 与 `ms` 这两个名字在 core 的自检输出里合法（`elapsed=`/`wall-`），
    /// 作为夹具键一律禁用；但 `wall: null` 这个占位槽位是允许的（它证明「没有本机时刻」，
    /// 而不是「藏了一个」）。所以判定写成：这些键必须**不存在**，或者存在但值恒为 null。</summary>
    public static readonly string[] ForbiddenUnlessNull = { "wall", "ms", "now", "ticks" };

    /// <summary>夹具违规的全集：<paramref name="allowOutsideCatalog"/> = 负例夹具（§8.2 规则 5 的例外）。</summary>
    public static IReadOnlyList<FixtureViolation> Validate(
        ReplayFixture fixture, string rawText, bool allowOutsideCatalog = false)
    {
        var violations = new List<FixtureViolation>();
        CollectR6(rawText, violations);
        for (int i = 0; i < fixture.Frames.Count; i++)
        {
            FixtureFrame frame = fixture.Frames[i];
            Collect(frame, i, allowOutsideCatalog, violations);
        }

        return violations;
    }

    /// <summary>R6：按 JSON 键名判（原始文本 + 解析各一份证据：两处都对才叫过）。</summary>
    private static void CollectR6(string rawText, List<FixtureViolation> violations)
    {
        string[] lines = rawText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException error)
            {
                violations.Add(new FixtureViolation("fixture-unparsable", $"line={i + 1}", error.Message));
                continue;
            }

            if (node is null)
            {
                continue;
            }

            foreach (string key in ForbiddenVolatileKeys)
            {
                if (HasKey(node, key))
                {
                    violations.Add(new FixtureViolation(
                        "fixture-wall-clock", $"line={i + 1} key={key}",
                        $"夹具里出现本机时刻键 {key}（R2：时刻必须全部来自夹具）"));
                }
            }

            foreach (string key in ForbiddenUnlessNull)
            {
                if (HasNonNullKey(node, key))
                {
                    violations.Add(new FixtureViolation(
                        "fixture-wall-clock", $"line={i + 1} key={key}",
                        $"夹具键 {key} 带非 null 值（R2：本机时刻不许进夹具；null 占位槽位是允许的）"));
                }
            }
        }
    }

    private static void Collect(
        FixtureFrame frame, int index, bool allowOutsideCatalog, List<FixtureViolation> violations)
    {
        // R1：seq 必须从 0 连续递增。
        if (frame.Seq != index)
        {
            violations.Add(new FixtureViolation(
                "fixture-seq-gap", $"at={frame.Seq}", $"第 {index} 行的 seq 应为 {index}，实际 {frame.Seq}"));
        }

        // R5：事件 type 必须 ∈ §2.1 的 13 个（负例夹具除外）。
        if (!allowOutsideCatalog)
        {
            foreach (string problem in EventCatalogTable.UnknownTypes(TypesOf(frame)))
            {
                violations.Add(new FixtureViolation("fixture-unknown-type", $"seq={frame.Seq}", problem));
            }
        }

        // R3：expected.directives 按 kind 降序（数值），同 kind 保序。
        for (int j = 1; j < frame.Expected.Directives.Count; j++)
        {
            if (KindRank(frame.Expected.Directives[j - 1].Kind) < KindRank(frame.Expected.Directives[j].Kind))
            {
                violations.Add(new FixtureViolation(
                    "fixture-directive-order", $"seq={frame.Seq} at={j}",
                    $"expected.directives 不是按 kind 降序：{frame.Expected.Directives[j - 1].Kind}"
                    + $" 在 {frame.Expected.Directives[j].Kind} 之前（§8.2 规则 3）"));
            }
        }

        // R4 的机器可判定半边：expected.events 里每个 type 的次数不得超过「输入事件 + 指令派生」
        // 的次数（投递序 FIFO 本身由 core 日志的先后判定，见 EventReplayTests 的那条）。
        //
        // ⚠️ 本会话实测踩过：指令派生的事件**不算幻影** —— §6.2 的 moveTo → core.window.moved、
        // §6.5/§6.6 的 suspend/resume → host.suspend/host.resume、emit → 它自己的 type；
        // 它们由 Brain 指令落地时发布，本来就不在「输入事件」列表里。判定必须把它们并进来，
        // 否则会把合法的契约行为判成红。派生集由 ReplayEngine.EventsDerivedFromDirectives 给出
        // （**同一个函数**被引擎与规则共用，避免两份口径漂移）。
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (FixtureEvent evt in frame.Events)
        {
            remaining.TryGetValue(evt.Type, out int n);
            remaining[evt.Type] = n + 1;
        }

        foreach (string derived in ReplayEngine.EventsDerivedFromDirectives(frame))
        {
            remaining.TryGetValue(derived, out int n);
            if (n <= 0)
            {
                remaining[derived] = 1;
            }
        }

        foreach (FixtureEvent evt in frame.Expected.Events)
        {
            remaining.TryGetValue(evt.Type, out int n);
            if (n <= 0)
            {
                violations.Add(new FixtureViolation(
                    "fixture-event-phantom", $"seq={frame.Seq}",
                    $"expected.events 里有输入里不存在的事件 {evt.Type}（§8.2 规则 4 的投递序前提）"));
                continue;
            }

            remaining[evt.Type] = n - 1;
        }
    }

    private static IEnumerable<string> TypesOf(FixtureFrame frame)
    {
        foreach (FixtureEvent evt in frame.Events)
        {
            yield return evt.Type;
        }

        foreach (FixtureEvent evt in frame.Expected.Events)
        {
            yield return evt.Type;
        }

        foreach (FixtureDirective directive in frame.Brain)
        {
            if (directive.EventType is not null)
            {
                yield return directive.EventType;
            }
        }
    }

    /// <summary>DirectiveKind 的数值序（越大越优先，src/PetRuntime/Brain.cs:20-39 冻结）。</summary>
    public static int KindRank(string kind) => kind switch
    {
        "suspend" => 5,
        "resume" => 4,
        "setAnimation" => 3,
        "moveTo" => 2,
        "emit" => 1,
        "requestCapability" => 0,
        _ => -1,
    };

    private static bool HasKey(JsonNode node, string key) => Scan(node, key, requireNonNull: false);

    private static bool HasNonNullKey(JsonNode node, string key) => Scan(node, key, requireNonNull: true);

    private static bool Scan(JsonNode node, string key, bool requireNonNull)
    {
        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in obj)
            {
                if (string.Equals(pair.Key, key, StringComparison.Ordinal)
                    && (!requireNonNull || pair.Value is not null))
                {
                    return true;
                }

                if (pair.Value is not null && Scan(pair.Value, key, requireNonNull))
                {
                    return true;
                }
            }

            return false;
        }

        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is not null && Scan(item, key, requireNonNull))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>夹具目录（%TEMP%，用完即删；**绝不写仓库目录** —— D-13a 的纪律）。</summary>
public sealed class FixtureDirectory : IDisposable
{
    private FixtureDirectory(string root) => Root = root;

    public string Root { get; }

    /// <summary>建一个临时夹具目录并把全部夹具写进去。</summary>
    public static FixtureDirectory Create()
    {
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "petdock-eventreplay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var directory = new FixtureDirectory(root);
        foreach (KeyValuePair<string, ReplayFixture> pair in FixtureConstructor.All())
        {
            ReplayFixtureIo.Write(pair.Value, directory.PathOf(pair.Key));
        }

        return directory;
    }

    public string PathOf(string name) => System.IO.Path.Combine(Root, name + ".replay.jsonl");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception error)
        {
            Console.WriteLine($"EVENT-CHECK FixtureDirectory.Cleanup FAIL {error.GetType().Name}: {error.Message}");
        }
    }
}
