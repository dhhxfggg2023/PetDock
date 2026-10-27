// RFC-27 §3.2-§3.6：tier-2 wire 的方法面与会话 quota 记账。
// 与 Tier2Sandbox.cs 是同一个类的两半：那边是进程边界（spawn/往返/关闭），这边是边界里跑的包。
// 分开的理由是文件行数判据（D-03c 400 行），与本仓其它 partial 同一个理由。
// 两类失败在这里被刻意分开（RFC-27 §3.2）：
//   格式错 → 返回 false + 错误串，进程留存（客体写错包不该等于崩溃）；
//   越限   → BudgetExceeded + 杀进程（原规格 14 §5 的「越限 = 卸载」）。
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;

namespace PetDock.Core.Sandbox;

public sealed partial class Tier2Sandbox
{
    /// <summary>最近一次成功解码的帧（诊断用；不参与合成）。</summary>
    public Tier2Frame? LastFrame { get; private set; }

    /// <summary>`abi.hello` 往返（RFC-27 §3.1）；失败即装载失败。</summary>
    private bool NegotiateHello()
    {
        var parameters = new JsonObject
        {
            ["abi"] = new JsonObject { ["major"] = 1, ["minor"] = 0 },
        };

        Tier2Reply reply = Call("abi.hello", parameters);
        if (!reply.Ok)
        {
            Emit(SandboxEventKind.GuestFault, ("stage", "hello"), ("err", reply.Error ?? "<none>"));
            return false;
        }

        if (!Tier2Wire.TryParseHello(reply.Result, out Tier2Hello? hello, out string? error))
        {
            Emit(SandboxEventKind.GuestFault, ("stage", "hello"), ("err", error ?? "<none>"));
            return false;
        }

        Hello = hello;
        return true;
    }

    /// <summary>
    /// 取一帧（RFC-27 §3.2）。格式错返回 false 且进程留存；单帧字节越限则卸载。
    /// </summary>
    public bool TryQueryFrame(int nowMs, out Tier2Frame? frame, out string? error)
    {
        frame = null;
        error = null;
        if (!IsLoaded)
        {
            error = "process-not-running";
            return false;
        }

        Tier2Reply reply = Call("abi.frame", new JsonObject { ["nowMs"] = nowMs });
        if (!reply.Ok)
        {
            error = reply.Error;
            return false;
        }

        if (!Tier2Wire.TryParseFrame(reply.Result, LastFrameId, out frame, out error, out int bytes))
        {
            return false;
        }

        if (bytes > _maxFrameBytes)
        {
            QuotaExceeded++;
            Emit(
                SandboxEventKind.BudgetExceeded,
                ("stage", "quota-frame-bytes"),
                ("bytes", bytes),
                ("budget", _maxFrameBytes));
            StopProcess();
            frame = null;
            error = "quota-frame-bytes";
            return false;
        }

        LastFrameId = frame!.FrameId;
        LastFrame = frame;
        return true;
    }

    /// <summary>驱动客体头脑一 tick（RFC-27 §3.3），返回的指令是宿主仲裁器的输入类型。</summary>
    public bool TryTickBrain(
        int dtMs,
        WorldSnapshot world,
        out IReadOnlyList<BrainDirective>? directives,
        out string? error)
    {
        directives = null;
        error = null;
        if (!IsLoaded)
        {
            error = "process-not-running";
            return false;
        }

        var parameters = new JsonObject
        {
            ["dtMs"] = dtMs,
            ["world"] = Tier2Wire.WorldJson(world),
        };

        Tier2Reply reply = Call("abi.tick", parameters);
        if (!reply.Ok)
        {
            error = reply.Error;
            return false;
        }

        return Tier2Wire.TryParseDirectives(reply.Result, out directives, out error);
    }

    /// <summary>把一条总线事件投给客体（RFC-27 §3.4：单向通知，不等应答）。</summary>
    public bool SendEvent(PetEvent evt) =>
        evt is not null && Notify("abi.event", Tier2Wire.EventJson(evt));

    /// <summary>`abi.state.get`：读客体自持的状态（RFC-27 §3.6）。</summary>
    public bool TryStateGet(IReadOnlyList<string>? keys, out JsonObject? values, out string? error)
    {
        values = null;
        error = null;
        if (!IsLoaded)
        {
            error = "process-not-running";
            return false;
        }

        var parameters = new JsonObject();
        if (keys is { Count: > 0 })
        {
            var array = new JsonArray();
            foreach (string key in keys)
            {
                array.Add(key);
            }

            parameters["keys"] = array;
        }

        Tier2Reply reply = Call("abi.state.get", parameters);
        if (!reply.Ok)
        {
            error = reply.Error;
            return false;
        }

        if (reply.Result is not JsonObject obj)
        {
            error = "state-bad:not-object";
            return false;
        }

        values = obj["values"] as JsonObject ?? new JsonObject();
        return true;
    }

    /// <summary>
    /// `abi.state.set`：写客体自持的状态。8 KiB 上限在**宿主侧**按收到的 patch 算（RFC-27 §3.6），
    /// 超限直接拒绝且不发送——客体自报的大小不能作为裁决依据。
    /// </summary>
    public bool TryStateSet(JsonObject patch, out int bytes, out string? error)
    {
        bytes = 0;
        error = null;
        if (patch is null)
        {
            error = "state-bad:patch-missing";
            return false;
        }

        if (!IsLoaded)
        {
            error = "process-not-running";
            return false;
        }

        bytes = Encoding.UTF8.GetByteCount(patch.ToJsonString());
        if (bytes > Tier2Wire.MaxStateBytes)
        {
            error = "state-bad:state-too-large";
            return false;
        }

        Tier2Reply reply = Call("abi.state.set", new JsonObject { ["patch"] = patch.DeepClone() });
        if (!reply.Ok)
        {
            error = reply.Error;
            return false;
        }

        return true;
    }

    /// <summary>发一条 JSON-RPC 通知：写一行、不读应答、不占请求预算（RFC-27 §3.4）。</summary>
    public bool Notify(string method, JsonObject parameters)
    {
        if (_stdin is null || _process is null || _process.HasExited)
        {
            return false;
        }

        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters.DeepClone(),
        };

        try
        {
            string line = notification.ToJsonString();
            _stdin.WriteLine(line);
            _stdin.Flush();
            Notifications++;
            return ChargeWire(Encoding.UTF8.GetByteCount(line) + 1, "in");
        }
        catch (Exception ex)
        {
            Crashes++;
            Emit(SandboxEventKind.GuestFault, ("stage", "notify"), ("method", method), ("err", ex.GetType().Name));
            return false;
        }
    }

    /// <summary>
    /// 记一次 wire 字节并检查会话预算（RFC-27 §3.5）：用尽即杀进程并计一次越限。
    /// 返回 false = 这次往返已经把预算越过去了，调用方必须当作失败处理。
    /// </summary>
    private bool ChargeWire(long bytes, string direction)
    {
        if (direction == "in")
        {
            WireInBytes += bytes;
        }
        else
        {
            WireOutBytes += bytes;
        }

        if (WireInBytes + WireOutBytes <= _sessionByteBudget)
        {
            return true;
        }

        QuotaExceeded++;
        Emit(
            SandboxEventKind.BudgetExceeded,
            ("stage", "quota-session-bytes"),
            ("wireIn", WireInBytes),
            ("wireOut", WireOutBytes),
            ("budget", _sessionByteBudget));
        StopProcess();
        return false;
    }
}
