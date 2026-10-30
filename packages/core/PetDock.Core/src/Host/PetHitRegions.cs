// 素材包声明的命中区（2026-10-29）：从标准包清单读 hitRegions，转成内核认的归一化矩形。
//
// 格式（全部可选；没写就按老规则「命中图 / alpha」判）：
//   "hitRegions": [
//     { "id": "body", "x": 0.25, "y": 0.15, "w": 0.5, "h": 0.75 }
//   ]
// 坐标为 0..1 的比例（相对画面），所以同一份声明在 64 与 256 像素下都成立。
//
// 外部输入照样要克制：最多 8 块；越界坐标夹回 0..1；宽或高 <= 0 的块丢弃（否则会变成一条
// 「无限薄」的命中线，用户点十次中一次）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal static class PetHitRegions
{
    private const string ManifestName = "petdock-sample.json";
    private const int MaxRegions = 8;

    /// <summary>读一个素材包的命中区；没有清单/没有字段/坏 JSON 一律空（退回默认命中）。</summary>
    public static IReadOnlyList<HitBox> ForPack(string? packDir)
    {
        if (string.IsNullOrWhiteSpace(packDir))
        {
            return Array.Empty<HitBox>();
        }

        string manifest = Path.Combine(packDir, ManifestName);
        if (!File.Exists(manifest))
        {
            return Array.Empty<HitBox>();
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest, Encoding.UTF8));
            return Read(doc.RootElement);
        }
        catch (Exception error)
        {
            KernelLog.Write($"pack-hit-regions-unreadable path={packDir} {error.GetType().Name}");
            return Array.Empty<HitBox>();
        }
    }

    /// <summary>从清单根节点读 hitRegions（判据直接喂一个 JSON 根节点给这条）。</summary>
    public static IReadOnlyList<HitBox> Read(JsonElement root)
    {
        if (!root.TryGetProperty("hitRegions", out JsonElement regions) || regions.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<HitBox>();
        }

        var boxes = new List<HitBox>(Math.Min(regions.GetArrayLength(), MaxRegions));
        foreach (JsonElement item in regions.EnumerateArray())
        {
            if (boxes.Count >= MaxRegions)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            double? x = Number(item, "x");
            double? y = Number(item, "y");
            double? w = Number(item, "w");
            double? h = Number(item, "h");
            if (x is null || y is null || w is null || h is null || w <= 0 || h <= 0)
            {
                continue;
            }

            double left = Clamp(x.Value);
            double top = Clamp(y.Value);
            double right = Clamp(x.Value + w.Value);
            double bottom = Clamp(y.Value + h.Value);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            boxes.Add(new HitBox(left, top, right - left, bottom - top));
        }

        return boxes;
    }

    private static double Clamp(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    private static double? Number(JsonElement item, string key) =>
        item.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double number) ? number : null;
}
