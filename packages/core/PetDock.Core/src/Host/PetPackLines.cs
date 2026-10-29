// 素材包自己的台词（2026-10-29）：标准包的 petdock-sample.json 可以带 "lines" 对象，
// 让点按 / 抛掷 / 落地说的是这个包自己的话，而不是宿主的通用句。
//
// 为什么放在素材包里、不放进 i18n 表：台词属于这只宠物（角色口吻），不属于界面语言包；
// 素材作者最清楚自己的角色会说什么。缺这个字段就回落到宿主通用句，不编造。
//
// 读取要克制：最多 8 句、每句 60 字，空串/重复丢弃 —— 素材是外部输入，不设上限会被一份
// 几十万字的 JSON 拖住界面线程。
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PetDock.Hosting;

/// <summary>一个素材包声明的三组台词（任一为空 = 用宿主兜底句）。</summary>
internal sealed record PetPackLines(
    IReadOnlyList<string> Poke,
    IReadOnlyList<string> Throw,
    IReadOnlyList<string> Drop)
{
    public static PetPackLines Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>());

    /// <summary>三组都空（宿主据此决定是否回落）。</summary>
    public bool IsEmpty => Poke.Count == 0 && Throw.Count == 0 && Drop.Count == 0;

    /// <summary>从标准包清单根节点读 "lines"；没有/形状不对一律当空，不抛。</summary>
    public static PetPackLines Read(JsonElement root)
    {
        if (!root.TryGetProperty("lines", out JsonElement lines) || lines.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        return new PetPackLines(
            ReadList(lines, "poke"),
            ReadList(lines, "throw"),
            ReadList(lines, "drop"));
    }

    private const int MaxLines = 8;

    private const int MaxChars = 60;

    private static IReadOnlyList<string> ReadList(JsonElement lines, string key)
    {
        if (!lines.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
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
