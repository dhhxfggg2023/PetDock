// 台词表的解析（2026-10-29）：只认 JSON 形状，不做语义猜测。
//
// 上限是硬约束而不是风格：素材来自外部，一份几万句的 JSON 会把界面线程拖住，
// 而「这个包说了几万句」对用户没有任何价值。超出的部分直接不读。
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PetDock.Hosting;

internal static class PetPackLinesReader
{
    private const int MaxLines = 8;

    private const int MaxChars = 60;

    /// <summary>从标准包清单根节点读 "lines"；没有/形状不对一律当空，不抛。</summary>
    public static PetPackLines Read(JsonElement root)
    {
        if (!root.TryGetProperty("lines", out JsonElement lines) || lines.ValueKind != JsonValueKind.Object)
        {
            return PetPackLines.Empty;
        }

        return new PetPackLines(ReadSet(lines), ReadLanguages(lines));
    }

    private static IReadOnlyDictionary<string, PackLineSet> ReadLanguages(JsonElement lines)
    {
        var map = new Dictionary<string, PackLineSet>(StringComparer.OrdinalIgnoreCase);
        if (!lines.TryGetProperty("languages", out JsonElement languages)
            || languages.ValueKind != JsonValueKind.Object)
        {
            return map;
        }

        foreach (JsonProperty entry in languages.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            map[entry.Name.Trim()] = ReadSet(entry.Value);
        }

        return map;
    }

    private static PackLineSet ReadSet(JsonElement container) => new(
        ReadList(container, "poke"),
        ReadList(container, "drag"),
        ReadList(container, "throw"),
        ReadList(container, "drop"));

    private static IReadOnlyList<string> ReadList(JsonElement container, string key)
    {
        if (!container.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var picked = new List<string>(Math.Min(value.GetArrayLength(), MaxLines));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (picked.Count >= MaxLines)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? text = item.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            text = text.Trim();
            if (text.Length > MaxChars)
            {
                text = text.Substring(0, MaxChars);
            }

            if (seen.Add(text))
            {
                picked.Add(text);
            }
        }

        return picked;
    }
}
