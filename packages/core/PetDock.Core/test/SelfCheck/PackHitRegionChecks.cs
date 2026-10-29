// 素材包命中区的判据（2026-10-29）：命令形态 `--uitest`，判据行 `ui-pack-hits-*`。
//
// 这一层读的是外部输入，所以判据重点是「离谱的坐标不许变成可点区域」：
// 归一化夹到 0..1、零面积丢弃、最多 8 块；没声明就走老的 alpha 规则。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal static class PackHitRegionChecks
{
    internal delegate void Report(string name, bool ok, string detail);

    public static void Run(Report check)
    {
        try
        {
            Parse(check);
            Clamp(check);
            Caps(check);
            Absent(check);
            Covers(check);
            SamplePack(check);
        }
        catch (Exception error)
        {
            check("ui-pack-hits-crash", false, error.GetType().Name + ": " + error.Message);
        }
    }

    private static void Parse(Report check)
    {
        IReadOnlyList<HitBox> boxes = Read("{\"hitRegions\":["
            + "{\"id\":\"body\",\"x\":0.25,\"y\":0.2,\"w\":0.5,\"h\":0.6},"
            + "{\"x\":0,\"y\":0,\"w\":0.5,\"h\":0.5}]}");
        check(
            "ui-pack-hits-read",
            boxes.Count == 2 && Close(boxes[0].X, 0.25) && Close(boxes[0].Y, 0.2)
                && Close(boxes[0].W, 0.5) && Close(boxes[0].H, 0.6),
            $"count={boxes.Count} first={Describe(boxes)}");
    }

    private static void Clamp(Report check)
    {
        IReadOnlyList<HitBox> boxes = Read("{\"hitRegions\":[{\"x\":-0.5,\"y\":0.8,\"w\":0.7,\"h\":0.9}]}");
        bool ok = boxes.Count == 1
            && Close(boxes[0].X, 0) && Close(boxes[0].Y, 0.8)
            && Close(boxes[0].W, 0.2) && Close(boxes[0].H, 0.2);
        check("ui-pack-hits-clamp", ok, Describe(boxes));
    }

    private static void Caps(Report check)
    {
        var sb = new StringBuilder("{\"hitRegions\":[");
        for (int i = 0; i < 10; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"x\":0.1,\"y\":0.1,\"w\":0.2,\"h\":0.2}");
        }

        sb.Append("]}");
        IReadOnlyList<HitBox> boxes = Read(sb.ToString());
        check("ui-pack-hits-caps", boxes.Count == 8, $"count={boxes.Count} (上限 8)");
    }

    private static void Absent(Report check)
    {
        check("ui-pack-hits-absent", Read("{}").Count == 0, "没有 hitRegions = 空（按 alpha 判）");
        check(
            "ui-pack-hits-bad-shape",
            Read("{\"hitRegions\":\"nope\"}").Count == 0
                && Read("{\"hitRegions\":[{\"x\":0.1,\"y\":0.1,\"w\":0,\"h\":0.2}]}").Count == 0,
            "坏形状与零面积都不产生可点区域");
    }

    private static void Covers(Report check)
    {
        var box = new HitBox(0.25, 0.2, 0.5, 0.6);
        bool inside = box.Contains(100, 100, 200, 200);
        bool outside = box.Contains(10, 10, 200, 200);
        bool degenerate = box.Contains(100, 100, 0, 0);
        check(
            "ui-pack-hits-covers",
            inside && !outside && !degenerate,
            $"inside={inside} outside={outside} degenerate={degenerate}");
    }

    /// <summary>示例包声明了「身体」这一块：它是文档里那个例子的活样本，不能被悄悄删掉。</summary>
    private static void SamplePack(Report check)
    {
        string? pack = FindSamplePack();
        if (pack is null)
        {
            check("ui-pack-hits-sample", false, "找不到 assets/sample-pet");
            return;
        }

        IReadOnlyList<HitBox> boxes = PetHitRegions.ForPack(pack);
        bool sane = boxes.Count >= 1 && boxes[0].W > 0.2 && boxes[0].H > 0.2;
        check("ui-pack-hits-sample", sane, $"count={boxes.Count} first={Describe(boxes)}");
    }

    private static IReadOnlyList<HitBox> Read(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return PetHitRegions.Read(doc.RootElement);
    }

    private static bool Close(double value, double expected) => Math.Abs(value - expected) < 0.0001;

    private static string Describe(IReadOnlyList<HitBox> boxes) =>
        boxes.Count == 0 ? "(none)" : $"{boxes[0].X:0.##},{boxes[0].Y:0.##},{boxes[0].W:0.##},{boxes[0].H:0.##}";

    private static string? FindSamplePack()
    {
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && cursor is not null; depth++)
        {
            string candidate = Path.Combine(cursor.FullName, "assets", "sample-pet");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            cursor = cursor.Parent;
        }

        return null;
    }
}
