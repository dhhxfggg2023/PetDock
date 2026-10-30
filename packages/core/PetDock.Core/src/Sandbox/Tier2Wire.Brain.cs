// RFC-27 §3.3-§3.4：directive 与事件包的编解码（从 Tier2Wire.cs 拆出，D-03c 400 行判据）。
//
// 与 Tier2Wire.cs 是同一个类的两半；Str/Bool/Number 三个读取器留在那边，这里直接用。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;

namespace PetDock.Core.Sandbox;

/// <summary>`abi.tick` 的指令与事件包投影（RFC-27 §3.3/§3.4）。</summary>
public static partial class Tier2Wire
{
    /// <summary>
    /// 解析 `abi.tick` 的 `directives`（RFC-27 §3.3）：六种 kind 全投影，未知 kind 或缺失槽位
    /// 令**整包作废**（不静默跳过）。
    /// </summary>
    public static bool TryParseDirectives(
        JsonNode? result,
        out IReadOnlyList<BrainDirective>? directives,
        out string? error)
    {
        directives = null;
        error = null;
        if (result is not JsonObject obj)
        {
            error = "brain-bad:not-object";
            return false;
        }

        if (obj["directives"] is not JsonArray array)
        {
            error = "brain-bad:directives-not-array";
            return false;
        }

        var list = new List<BrainDirective>(array.Count);
        foreach (JsonNode? item in array)
        {
            if (item is not JsonObject directive)
            {
                error = "brain-bad:directive-not-object";
                return false;
            }

            switch (Str(directive["kind"]) ?? string.Empty)
            {
                case "setAnimation":
                    string? clip = Str(directive["clip"]);
                    if (string.IsNullOrEmpty(clip))
                    {
                        error = "brain-bad:clip-missing";
                        return false;
                    }

                    list.Add(BrainDirective.SetAnimation(
                        clip,
                        Bool(directive["restart"]),
                        directive["opts"] as JsonObject));
                    break;

                case "moveTo":
                    if (directive["at"] is not JsonObject at
                        || Number(at["x"]) is not double mx
                        || Number(at["y"]) is not double my)
                    {
                        error = "brain-bad:move-to-at";
                        return false;
                    }

                    list.Add(BrainDirective.MoveTo(new PetPoint(mx, my), Number(directive["speed"])));
                    break;

                case "emit":
                    if (directive["event"] is not JsonObject ev)
                    {
                        error = "brain-bad:emit-event";
                        return false;
                    }

                    string? type = Str(ev["type"]);
                    if (string.IsNullOrEmpty(type))
                    {
                        error = "brain-bad:emit-type";
                        return false;
                    }

                    list.Add(BrainDirective.Emit(new PetEvent(
                        type,
                        Number(ev["at"]) is double at2 ? (long)at2 : 0,
                        ev["payload"] as JsonObject,
                        Str(ev["trace"]))));
                    break;

                case "requestCapability":
                    string? capability = Str(directive["capability"]);
                    if (string.IsNullOrEmpty(capability))
                    {
                        error = "brain-bad:capability-missing";
                        return false;
                    }

                    list.Add(BrainDirective.RequestCapability(capability, Str(directive["justification"]) ?? string.Empty));
                    break;

                case "suspend":
                    list.Add(BrainDirective.Suspend(Str(directive["reason"]) ?? string.Empty));
                    break;

                case "resume":
                    list.Add(BrainDirective.Resume(Str(directive["reason"]) ?? string.Empty));
                    break;

                default:
                    error = "brain-bad:unknown-kind";
                    return false;
            }
        }

        directives = list;
        return true;
    }

    /// <summary>`PetEvent` 的通知参数（RFC-27 §3.4）。`id` 由调用方决定加不加，这里只管 params。</summary>
    public static JsonObject EventJson(PetEvent evt) => new()
    {
        ["type"] = evt.Type,
        ["at"] = evt.At,
        ["payload"] = evt.Payload?.DeepClone(),
        ["trace"] = evt.Trace,
    };

}
