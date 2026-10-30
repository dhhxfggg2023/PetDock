// agent 状态文件的一条快照（2026-10-29，P4 AI/agent 状态）。
//
// 文件格式故意很小（写状态的一方不需要引入我们的库）：
//   { "state": "running", "task": "跑测试", "detail": "3/12", "progress": 0.25,
//     "updatedAt": "2026-10-29T10:00:00Z" }
// 除了 state 之外全是可选的；不认识的东西原样显示，不替它猜含义。
//
// 「有没有更新」用两处时间里较新的那个：updatedAt（写状态的人给的）与文件 mtime。
// 只有一处时用那一处；两处都没有就按"刚读到"算，绝不假装它是新的。
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PetDock.Agent;

internal sealed record AgentSnapshot(
    string State,
    string? Task,
    string? Detail,
    double? Progress,
    string? RawState,
    DateTime? At,
    bool Missing,
    bool Unreadable)
{
    public static AgentSnapshot MissingOne { get; } = new(string.Empty, null, null, null, null, null, true, false);

    public static AgentSnapshot UnreadableOne { get; } = new(string.Empty, null, null, null, null, null, false, true);

    /// <summary>state 归一化到五个词；不认识就保留原文（RawState 记着），不硬翻译。</summary>
    public string Normalized => State;

    public bool Running => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);

    public bool Terminal =>
        string.Equals(State, "done", StringComparison.OrdinalIgnoreCase)
        || string.Equals(State, "failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>读一个状态文件。文件不存在 = Missing；内容读不懂 = Unreadable —— 两者都不是「空闲」。</summary>
    public static AgentSnapshot Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return MissingOne;
        }

        DateTime write;
        try
        {
            write = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            write = DateTime.MinValue;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return UnreadableOne;
            }

            string? state = Text(root, "state");
            if (string.IsNullOrWhiteSpace(state))
            {
                // 没有 state 的 JSON 不是「空闲」，是这一份文件不完整。
                return UnreadableOne;
            }

            DateTime? updated = Time(root);
            return new AgentSnapshot(
                Normalize(state),
                Text(root, "task"),
                Text(root, "detail"),
                Number(root, "progress"),
                state,
                updated ?? (write == DateTime.MinValue ? null : DateTime.SpecifyKind(write, DateTimeKind.Utc)),
                false,
                false);
        }
        catch (Exception)
        {
            return UnreadableOne;
        }
    }

    private static string Normalize(string state) => state.Trim().ToLowerInvariant() switch
    {
        "idle" => "idle",
        "running" => "running",
        "done" or "finished" or "success" => "done",
        "failed" or "error" => "failed",
        _ => state.Trim(),
    };

    private static string? Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
        {
            return number;
        }

        // 有的是字符串写的百分比（"25"）。
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            return parsed;
        }

        return null;
    }

    private static DateTime? Time(JsonElement root)
    {
        if (!root.TryGetProperty("updatedAt", out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long ms))
        {
            // 毫秒时间戳（写状态的一方最省事的形式）。
            return ms > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;
        }

        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            return parsed.UtcDateTime;
        }

        return null;
    }
}
