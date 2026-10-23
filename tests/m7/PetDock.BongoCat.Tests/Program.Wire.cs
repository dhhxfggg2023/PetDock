// RFC-27 §4：tier-2 wire 契约的判据（11 条）与承担这些判据的客体角色（mode=wire）。
//
// 与 Program.cs 是同一个类的两半。这里的客体只做「按脚本回包」，不模拟真实渲染——
// 判据要验的是**包形状与记账**，不是某一帧画得好不好看（RFC-27 §9 第 1 条）。
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;
using PetDock.Core.Sandbox;
using PetDock.Hosting;

namespace PetDock.BongoCat.Tests;

internal static partial class Program
{
    // ---------- 判据 ----------

    private static void Tier2WireContract()
    {
        string command = Tier2GuestCommand();
        var sink = new CollectingSink();
        var gate = new CapabilityGate(new[] { "ipc.tier2:petdock-test-shim" }, null, sink);

        // ① hello：装载的第二个往返。
        var wire = new Tier2Sandbox(gate, sink, command, Tier2GuestArgs("wire"), requestTimeoutMs: 2000, frameBudgetMs: 2000);
        bool up = wire.Start();
        Tier2Hello? hello = wire.Hello;
        Check("tier2-wire-hello", up && hello is not null
            && hello.Id == "petdock.wire.guest" && hello.RequiresHitmap && Math.Abs(hello.PreferredFps - 30) < 0.001,
            $"up={up} id={hello?.Id} hitmap={hello?.RequiresHitmap} fps={hello?.PreferredFps}");

        // ② 帧往返：4×4、像素值、帧号。
        bool frameOk = wire.TryQueryFrame(0, out Tier2Frame? frame, out string? frameErr);
        Check("tier2-wire-frame-round-trip", frameOk && frame is not null
            && frame.FrameId == 1 && frame.Width == 4 && frame.Height == 4
            && Math.Abs(frame.Scale - 1.0) < 0.001 && frame.Clip == "idle"
            && frame.PremultipliedBgra.Length == 64 && frame.PremultipliedBgra[3] == 0xFF,
            $"ok={frameOk} err={frameErr} id={frame?.FrameId} size={frame?.Width}x{frame?.Height} bytes={frame?.PremultipliedBgra.Length}");

        // ③ 命中图优先于 alpha（docs/11 §2.2）：位图 alpha 全 255，但只有 (0,0) 命中。
        bool hitOk = frame is not null && frame.Hits(0, 0) && !frame.Hits(1, 1) && frame.PremultipliedBgra[(1 * 4 + 1) * 4 + 3] == 0xFF;
        Check("tier2-wire-hitmap-wins", hitOk,
            $"hit(0,0)={frame?.Hits(0, 0)} hit(1,1)={frame?.Hits(1, 1)} alpha(1,1)=0xFF");

        // ④ 格式错：位图长度不对 → 拒绝但进程留存，下一帧恢复正常。
        wire.Call("probe.frame-script", new JsonObject { ["script"] = "bad-size" });
        bool badRejected = !wire.TryQueryFrame(0, out _, out string? badErr);
        wire.Call("probe.frame-script", new JsonObject { ["script"] = "good" });
        bool recovered = wire.TryQueryFrame(0, out Tier2Frame? after, out _);
        Check("tier2-wire-frame-size-reject", badRejected && badErr == "frame-bad:bitmap-size" && recovered && wire.IsLoaded,
            $"rejected={badRejected} err={badErr} recovered={recovered} id={after?.FrameId} live={wire.IsLoaded}");

        // ⑤ 帧号回退 → 拒绝（同 id 回显校验的理由：错位的帧比报错难查）。
        wire.Call("probe.frame-script", new JsonObject { ["script"] = "regress" });
        bool regressed = !wire.TryQueryFrame(0, out _, out string? regressErr);
        Check("tier2-wire-frame-id-regress", regressed && (regressErr ?? string.Empty).Contains("frame-id-regress", StringComparison.Ordinal) && wire.IsLoaded,
            $"rejected={regressed} err={regressErr} live={wire.IsLoaded}");

        // ⑥ 事件包：单向通知，顺序 = 发送顺序。
        wire.SendEvent(new PetEvent("core.one", 1));
        wire.SendEvent(new PetEvent("core.two", 2));
        wire.SendEvent(new PetEvent("core.three", 3));
        Tier2Reply eventsReply = wire.Call("probe.events", new JsonObject());
        string order = string.Join(",", Types(eventsReply.Result));
        Check("tier2-wire-event-order", order == "core.one,core.two,core.three" && wire.Notifications == 3,
            $"order={order} notifications={wire.Notifications}");

        // ⑦ 头脑 tick：世界快照五项过线 + 两种指令回读成同一个 BrainDirective。
        var world = new WorldSnapshot(new PetPoint(42, 43), new PetRect(1000, 500, 128, 128), "code", 615, false);
        bool tickOk = wire.TryTickBrain(16, world, out IReadOnlyList<BrainDirective>? directives, out string? tickErr);
        bool animOk = tickOk && directives is { Count: 2 }
            && directives[0].Kind == DirectiveKind.SetAnimation && directives[0].Clip == "look_left" && directives[0].Restart
            && directives[1].Kind == DirectiveKind.MoveTo
            && directives[1].At is PetPoint at && Math.Abs(at.X - 1) < 0.001 && Math.Abs(at.Y - 2) < 0.001
            && directives[1].Speed is double speed && Math.Abs(speed - 3.5) < 0.001;

        // 世界快照的逐字段回读：客体把收到的 world 原样带回来，宿主侧逐项比对。
        var echoParams = new JsonObject
        {
            ["dtMs"] = 16,
            ["world"] = Tier2Wire.WorldJson(world),
            ["probe"] = "echo",
        };
        Tier2Reply echo = wire.Call("abi.tick", echoParams);
        JsonObject? echoNode = echo.Result?["echo"] as JsonObject;
        bool echoOk = echoNode is not null
            && echoNode["cursorX"]?.GetValue<double>() == 42
            && echoNode["windowX"]?.GetValue<double>() == 1000
            && echoNode["focusedApp"]?.GetValue<string>() == "code"
            && echoNode["tod"]?.GetValue<double>() == 615
            && echoNode["occluded"]?.GetValue<bool>() == false
            && echoNode["dtMs"]?.GetValue<double>() == 16;
        Check("tier2-wire-brain-tick", animOk && echoOk,
            $"directives={directives?.Count} first={directives?[0].Clip} echo={(echoNode is null ? "<none>" : echoNode.ToJsonString())} err={tickErr}");

        // ⑧ 未知 kind → 整包作废（不静默跳过）。
        wire.Call("probe.tick-script", new JsonObject { ["script"] = "unknown" });
        bool unknown = !wire.TryTickBrain(16, world, out _, out string? unknownErr);
        Check("tier2-wire-directive-unknown-kind", unknown && unknownErr == "brain-bad:unknown-kind", $"rejected={unknown} err={unknownErr}");

        // ⑨ 状态：往返一致；超 8 KiB 的 patch 在宿主侧被拒且不落盘。
        bool setOk = wire.TryStateSet(new JsonObject { ["mood"] = "happy", ["n"] = 3 }, out int stateBytes, out string? setErr);
        bool getOk = wire.TryStateGet(null, out JsonObject? values, out string? getErr)
            && values?["mood"]?.GetValue<string>() == "happy" && values?["n"]?.GetValue<double>() == 3;
        var big = new JsonObject { ["blob"] = new string('x', 9000) };
        bool bigRejected = !wire.TryStateSet(big, out _, out string? bigErr) && (bigErr ?? string.Empty).Contains("state-too-large", StringComparison.Ordinal);
        bool stillOld = wire.TryStateGet(null, out JsonObject? after2, out _) && after2?["mood"]?.GetValue<string>() == "happy";
        Check("tier2-wire-state-round-trip", setOk && getOk && bigRejected && stillOld,
            $"set={setOk}/{stateBytes}B get={getOk} big-rejected={bigRejected} unchanged={stillOld} err={setErr ?? getErr}");

        wire.Dispose();

        // ⑩ 单帧字节越限（注入小上限）→ 卸载 + 计一次越限。
        var frameQuota = new Tier2Sandbox(gate, sink, command, Tier2GuestArgs("wire"), requestTimeoutMs: 2000, frameBudgetMs: 2000, maxFrameBytes: 1024);
        bool quotaUp = frameQuota.Start();
        frameQuota.Call("probe.frame-script", new JsonObject { ["script"] = "huge" });
        bool quotaFrame = !frameQuota.TryQueryFrame(0, out _, out string? quotaFrameErr);
        Check("tier2-quota-frame-bytes", quotaUp && quotaFrame && quotaFrameErr == "quota-frame-bytes"
            && !frameQuota.IsLoaded && frameQuota.QuotaExceeded >= 1,
            $"up={quotaUp} rejected={quotaFrame} err={quotaFrameErr} live={frameQuota.IsLoaded} quota={frameQuota.QuotaExceeded}");
        frameQuota.Dispose();

        // ⑪ 会话 wire 字节越限（注入小预算）→ 卸载 + 计一次越限。
        var byteQuota = new Tier2Sandbox(gate, sink, command, Tier2GuestArgs("wire"), requestTimeoutMs: 2000, frameBudgetMs: 2000, sessionByteBudget: 400);
        bool byteUp = byteQuota.Start();
        for (int i = 0; i < 3 && byteQuota.IsLoaded; i++)
        {
            byteQuota.TryQueryFrame(0, out _, out _);
        }

        Check("tier2-quota-wire-bytes", byteUp && !byteQuota.IsLoaded && byteQuota.QuotaExceeded >= 1 && byteQuota.WireInBytes > 0,
            $"up={byteUp} live={byteQuota.IsLoaded} quota={byteQuota.QuotaExceeded} in={byteQuota.WireInBytes} out={byteQuota.WireOutBytes}");
        byteQuota.Dispose();
    }

    private static IEnumerable<string> Types(JsonNode? result)
    {
        if (result?["events"] is not JsonArray array)
        {
            yield break;
        }

        foreach (JsonNode? item in array)
        {
            yield return item?.GetValue<string>() ?? string.Empty;
        }
    }

    // ---------- 客体角色（mode=wire） ----------

    /// <summary>mode=wire 的客体状态：帧号、脚本开关、状态层、收到的通知。</summary>
    private sealed class WireState
    {
        public long FrameNo { get; set; }

        public string FrameScript { get; set; } = "good";

        public string TickScript { get; set; } = "good";

        public JsonObject State { get; set; } = new();

        public List<string> Events { get; } = new();
    }

    private static JsonNode WireHello() => new JsonObject
    {
        ["id"] = "petdock.wire.guest",
        ["version"] = "1.0.0",
        ["requiresHitmap"] = true,
        ["preferredFps"] = 30.0,
        ["capabilities"] = new JsonArray("frames"),
    };

    private static JsonNode? WireMethod(string method, JsonObject? p, WireState ws)
    {
        switch (method)
        {
            case "abi.frame":
                return WireFrame(ws);

            case "abi.tick":
                return WireTick(ws, p);

            case "abi.state.get":
                return new JsonObject { ["schemaVersion"] = 1, ["values"] = ws.State.DeepClone() };

            case "abi.state.set":
                if (p?["patch"] is JsonObject patch)
                {
                    ws.State = patch.DeepClone().AsObject();
                }

                return new JsonObject { ["ok"] = true, ["bytes"] = Encoding.UTF8.GetByteCount((p?["patch"]?.ToJsonString()) ?? "{}") };

            case "probe.frame-script":
                ws.FrameScript = p?["script"]?.GetValue<string>() ?? "good";
                return "ok";

            case "probe.tick-script":
                ws.TickScript = p?["script"]?.GetValue<string>() ?? "good";
                return "ok";

            case "probe.events":
                var list = new JsonArray();
                foreach (string type in ws.Events)
                {
                    list.Add(type);
                }

                return new JsonObject { ["events"] = list };

            default:
                return "ok";
        }
    }

    private static JsonNode WireFrame(WireState ws)
    {
        string script = ws.FrameScript;
        ws.FrameScript = "good";

        int side = script == "huge" ? 64 : 4;
        int pixels = side * side;
        var bitmap = new byte[pixels * 4];
        for (int i = 0; i < pixels; i++)
        {
            bitmap[(i * 4) + 0] = 0xFF;
            bitmap[(i * 4) + 1] = 0xFF;
            bitmap[(i * 4) + 2] = 0xFF;
            bitmap[(i * 4) + 3] = 0xFF;
        }

        var hitmap = new byte[pixels];
        if (side == 4)
        {
            hitmap[0] = 1; // 只有 (0,0) 命中：够验「命中图压过全 255 的 alpha」
        }

        long frameId = script == "regress" ? ws.FrameNo : ++ws.FrameNo;
        var frame = new JsonObject
        {
            ["frameId"] = frameId,
            ["clip"] = "idle",
            ["size"] = new JsonObject { ["w"] = side, ["h"] = side },
            ["scale"] = 1.0,
            ["bitmap"] = Convert.ToBase64String(bitmap),
            ["hitmap"] = Convert.ToBase64String(hitmap),
        };

        if (script == "bad-size")
        {
            frame["bitmap"] = Convert.ToBase64String(new byte[(pixels * 4) - 1]);
        }

        return frame;
    }

    private static JsonNode WireTick(WireState ws, JsonObject? p)
    {
        if (ws.TickScript == "unknown")
        {
            ws.TickScript = "good";
            return new JsonObject { ["directives"] = new JsonArray(new JsonObject { ["kind"] = "dance" }) };
        }

        var result = new JsonObject
        {
            ["directives"] = new JsonArray(
                new JsonObject { ["kind"] = "setAnimation", ["clip"] = "look_left", ["restart"] = true },
                new JsonObject
                {
                    ["kind"] = "moveTo",
                    ["at"] = new JsonObject { ["x"] = 1.0, ["y"] = 2.0 },
                    ["speed"] = 3.5,
                }),
        };

        if (p?["probe"]?.GetValue<string>() == "echo")
        {
            JsonObject? w = p["world"] as JsonObject;
            result["echo"] = new JsonObject
            {
                ["cursorX"] = w?["cursor"]?["x"]?.GetValue<double>(),
                ["windowX"] = w?["windowRect"]?["x"]?.GetValue<double>(),
                ["focusedApp"] = w?["focusedApp"]?.GetValue<string>(),
                ["tod"] = w?["timeOfDayMinutes"]?.GetValue<double>(),
                ["occluded"] = w?["isOccluded"]?.GetValue<bool>(),
                ["dtMs"] = p["dtMs"]?.GetValue<double>(),
            };
        }

        return result;
    }

    private static void Tier2ChildResult(string id, JsonNode? result) =>
        Console.Out.WriteLine(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        }.ToJsonString());
}
