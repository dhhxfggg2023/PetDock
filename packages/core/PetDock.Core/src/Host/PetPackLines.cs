// 素材包自己的台词（2026-10-29）：标准包的 petdock-sample.json 可以带 "lines"，
// 让点按 / 拖拽 / 抛掷 / 落地说的是这个包自己的话，而不是宿主的通用句。
//
// 为什么放在素材包里、不放进 i18n 表：台词属于这只宠物（角色口吻），不属于界面语言包；
// 素材作者最清楚自己的角色会说什么。缺这个字段就回落到宿主通用句，不编造。
//
// 两层语言：
//   "lines": { "poke": [...], ... }                    ← 默认（不分语言）
//   "lines": { ..., "languages": { "en-US": {...} } }  ← 按界面语言覆盖，缺的键回落默认
// 匹配顺序：精确语言码 → 主语言（zh-CN ↔ zh）→ 默认 → 宿主通用句。
//
// 读取要克制：每组最多 8 句、每句 60 字，空串/重复丢弃 —— 素材是外部输入，
// 不设上限会被一份几十万字的 JSON 拖住界面线程。
using System;
using System.Collections.Generic;

namespace PetDock.Hosting;

/// <summary>一个素材包声明的一组台词（任一为空 = 该动作回落）。</summary>
internal sealed record PackLineSet(
    IReadOnlyList<string> Poke,
    IReadOnlyList<string> Drag,
    IReadOnlyList<string> Throw,
    IReadOnlyList<string> Drop)
{
    public static PackLineSet Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>());

    public bool IsEmpty => Poke.Count == 0 && Drag.Count == 0 && Throw.Count == 0 && Drop.Count == 0;

    /// <summary>按组取一列；组名不认识就返回空（不抛）。</summary>
    public IReadOnlyList<string> Group(string key) => key switch
    {
        "poke" => Poke,
        "drag" => Drag,
        "throw" => Throw,
        "drop" => Drop,
        _ => Array.Empty<string>(),
    };
}

/// <summary>一个素材包的台词表：默认一组 + 按语言覆盖的若干组。</summary>
internal sealed record PetPackLines(
    PackLineSet Default,
    IReadOnlyDictionary<string, PackLineSet> ByLanguage)
{
    public static PetPackLines Empty { get; } = new(
        PackLineSet.Empty,
        new Dictionary<string, PackLineSet>(StringComparer.OrdinalIgnoreCase));

    /// <summary>三组（含各语言）都空 = 完全没声明（宿主据此一律回落）。</summary>
    public bool IsEmpty
    {
        get
        {
            if (!Default.IsEmpty)
            {
                return false;
            }

            foreach (PackLineSet set in ByLanguage.Values)
            {
                if (!set.IsEmpty)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>按界面语言挑一组：精确码 → 主语言 → 默认。</summary>
    public PackLineSet For(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return Default;
        }

        if (ByLanguage.TryGetValue(language, out PackLineSet? exact))
        {
            return exact;
        }

        string primary = PrimaryTag(language);
        foreach (KeyValuePair<string, PackLineSet> pair in ByLanguage)
        {
            if (string.Equals(PrimaryTag(pair.Key), primary, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return Default;
    }

    private static string PrimaryTag(string tag)
    {
        int dash = tag.IndexOf('-');
        return dash > 0 ? tag.Substring(0, dash) : tag;
    }
}
