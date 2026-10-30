// RFC-27 §3.1-§3.4：tier-2 wire 契约的包编解码。
//
// 这一层的职责只有两件：把宿主侧的 ABI 类型写成一行 JSON，以及把客体回的那一行读回成同一批类型。
// 一切「格式错」都在这里被拒绝，并且**不杀进程**（RFC-27 §3.2：格式错是可恢复的，越限才卸载）。
//
// P-05（原规格 01 §5）：公共入口一律不外抛，失败只落成错误串。所以每个取值都走 Str/Bool/Number 这三个
// 吃掉类型错的读取器，而不是直接 GetValue<T>()（后者对错类型抛 InvalidOperationException）。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;

namespace PetDock.Core.Sandbox;

/// <summary>原规格 11 §2.1 的 Frame 在 wire 上的形态（RFC-27 §3.2）。</summary>
public sealed record Tier2Frame(
    long FrameId,
    string? Clip,
    int Width,
    int Height,
    double Scale,
    PetRect? ClearRect,
    byte[] PremultipliedBgra,
    byte[]? Hitmap)
{
    /// <summary>一像素是否命中（原规格 11 §2.2 的仲裁：命中图优先，否则看 alpha）。</summary>
    public bool Hits(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return false;
        }

        int at = y * Width + x;
        if (Hitmap is not null)
        {
            return Hitmap[at] != 0;
        }

        int alphaAt = at * 4 + 3;
        return alphaAt < PremultipliedBgra.Length && PremultipliedBgra[alphaAt] != 0;
    }
}

/// <summary>`abi.hello` 的结果（RFC-27 §3.1）。</summary>
public sealed record Tier2Hello(
    string Id,
    string Version,
    bool RequiresHitmap,
    double PreferredFps,
    IReadOnlyList<string> Capabilities);

/// <summary>帧与会话元信息的编解码。全部静态，无状态；directive/事件包过线的那一半在 Tier2Wire.Brain.cs。</summary>
public static partial class Tier2Wire
{
    /// <summary>单帧边长上限（RFC-27 §3.2）。</summary>
    public const int MaxSide = 512;

    /// <summary>单帧字节上限：位图 + 命中图（RFC-27 §3.5）。512×512 位图正好 1 MiB，留一倍余量。</summary>
    public const int MaxFrameBytes = 2 * 1024 * 1024;

    /// <summary>原规格 11 §6 的状态键值上限。</summary>
    public const int MaxStateBytes = 8 * 1024;

    /// <summary>解析 `abi.hello` 的结果。</summary>
    public static bool TryParseHello(JsonNode? result, out Tier2Hello? hello, out string? error)
    {
        hello = null;
        error = null;
        if (result is not JsonObject obj)
        {
            error = "hello-bad:not-object";
            return false;
        }

        string? id = Str(obj["id"]);
        if (string.IsNullOrEmpty(id))
        {
            error = "hello-bad:id-missing";
            return false;
        }

        var caps = new List<string>();
        if (obj["capabilities"] is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                string? cap = Str(item);
                if (!string.IsNullOrEmpty(cap))
                {
                    caps.Add(cap);
                }
            }
        }

        hello = new Tier2Hello(id, Str(obj["version"]) ?? "0.0.0", Bool(obj["requiresHitmap"]), Number(obj["preferredFps"]) ?? 0, caps);
        return true;
    }

    /// <summary>
    /// 解析 `abi.frame` 的结果。`lastFrameId` 是同一会话上一帧的帧号，用于拒绝回退（RFC-27 §3.2）。
    /// `bytes` 是解码后的位图 + 命中图字节数，供 quota 记账。
    /// </summary>
    public static bool TryParseFrame(
        JsonNode? result,
        long lastFrameId,
        out Tier2Frame? frame,
        out string? error,
        out int bytes)
    {
        frame = null;
        error = null;
        bytes = 0;
        if (result is not JsonObject obj)
        {
            error = "frame-bad:not-object";
            return false;
        }

        if (Number(obj["frameId"]) is not double rawId)
        {
            error = "frame-bad:frame-id-missing";
            return false;
        }

        long frameId = (long)rawId;
        if (frameId <= lastFrameId)
        {
            error = "frame-bad:frame-id-regress";
            return false;
        }

        if (obj["size"] is not JsonObject size)
        {
            error = "frame-bad:size-missing";
            return false;
        }

        if (Number(size["w"]) is not double rawW || Number(size["h"]) is not double rawH)
        {
            error = "frame-bad:size-missing";
            return false;
        }

        int w = (int)rawW;
        int h = (int)rawH;
        if (w < 1 || h < 1 || w > MaxSide || h > MaxSide)
        {
            error = "frame-bad:size-range";
            return false;
        }

        double scale = Number(obj["scale"]) ?? 0;
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            error = "frame-bad:scale-invalid";
            return false;
        }

        string? bitmap = Str(obj["bitmap"]);
        if (string.IsNullOrEmpty(bitmap))
        {
            error = "frame-bad:bitmap-missing";
            return false;
        }

        byte[] pixels;
        try
        {
            pixels = Convert.FromBase64String(bitmap);
        }
        catch (FormatException)
        {
            error = "frame-bad:bitmap-base64";
            return false;
        }

        if (pixels.Length != w * h * 4)
        {
            error = "frame-bad:bitmap-size";
            return false;
        }

        byte[]? hitmap = null;
        if (obj["hitmap"] is JsonNode hitNode)
        {
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(Str(hitNode) ?? string.Empty);
            }
            catch (FormatException)
            {
                error = "frame-bad:hitmap-base64";
                return false;
            }

            if (decoded.Length != w * h)
            {
                error = "frame-bad:hitmap-size";
                return false;
            }

            foreach (byte value in decoded)
            {
                if (value is not (0 or 1))
                {
                    error = "frame-bad:hitmap-value";
                    return false;
                }
            }

            hitmap = decoded;
        }

        PetRect? clear = null;
        if (obj["clearRect"] is JsonObject rect)
        {
            if (Number(rect["x"]) is not double rx || Number(rect["y"]) is not double ry
                || Number(rect["w"]) is not double rw || Number(rect["h"]) is not double rh)
            {
                error = "frame-bad:clear-rect";
                return false;
            }

            clear = new PetRect(rx, ry, rw, rh);
        }

        frame = new Tier2Frame(
            frameId,
            Str(obj["clip"]),
            w,
            h,
            scale,
            clear,
            pixels,
            hitmap);
        bytes = pixels.Length + (hitmap?.Length ?? 0);
        return true;
    }

    /// <summary>`原规格 11:143-149` 的 WorldSnapshot 投影（字段名照抄）。</summary>
    public static JsonObject WorldJson(WorldSnapshot world) => new()
    {
        ["cursor"] = world.Cursor is PetPoint cursor
            ? new JsonObject { ["x"] = cursor.X, ["y"] = cursor.Y }
            : null,
        ["windowRect"] = new JsonObject
        {
            ["x"] = world.WindowRect.X,
            ["y"] = world.WindowRect.Y,
            ["w"] = world.WindowRect.W,
            ["h"] = world.WindowRect.H,
        },
        ["focusedApp"] = world.FocusedApp,
        ["timeOfDayMinutes"] = world.TimeOfDayMinutes,
        ["isOccluded"] = world.IsOccluded,
    };

    /// <summary>读一个字符串；不是字符串（或缺失）返回 null，不抛。</summary>
    private static string? Str(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>读一个布尔；不是布尔（或缺失）返回 false，不抛。</summary>
    private static bool Bool(JsonNode? node)
    {
        if (node is null)
        {
            return false;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>读一个数字；不是数字（或缺失）返回 null，不抛。</summary>
    private static double? Number(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<double>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
