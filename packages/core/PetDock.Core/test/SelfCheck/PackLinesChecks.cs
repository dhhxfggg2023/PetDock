// 素材包自带台词的判据（2026-10-29）。命令形态 `--uitest`，判据行 `ui-pack-lines-*`。
//
// 读的是外部输入，所以判据的重点是「越界与坏形状不许把它带进来」：
// 最多 8 句、每句 60 字、空/重复丢弃、形状不对当没声明 —— 而不是「能不能读出一句话」。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PetDock.Hosting;

internal static class PackLinesChecks
{
    internal delegate void Report(string name, bool ok, string detail);

    public static void Run(Report check)
    {
        string root = Path.Combine(Path.GetTempPath(), "petdock-linescheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            ReadsAllThree(check, root);
            Caps(check, root);
            Absent(check, root);
            BadShape(check, root);
            SamplePackHasLines(check);
        }
        catch (Exception error)
        {
            check("ui-pack-lines-crash", false, error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void ReadsAllThree(Report check, string root)
    {
        string dir = WritePack(root, "reads", "{\"poke\":[\"a\",\"b\"],\"throw\":[\"c\"],\"drop\":[\"d\"]}");
        PetPackMetadata? meta = PetPackMetadataReader.Read(dir);
        check(
            "ui-pack-lines-read",
            meta is not null
                && meta.Lines.Poke.Count == 2
                && meta.Lines.Throw.Count == 1
                && meta.Lines.Drop.Count == 1
                && !meta.Lines.IsEmpty,
            $"poke={meta?.Lines.Poke.Count ?? -1} throw={meta?.Lines.Throw.Count ?? -1}"
            + $" drop={meta?.Lines.Drop.Count ?? -1}");
        check(
            "ui-pack-lines-metadata",
            meta?.Author == "check" && meta?.License == "CC0-1.0",
            $"author={meta?.Author} license={meta?.License}");
    }

    private static void Caps(Report check, string root)
    {
        // 13 句（含重复与 100 字超长）：上限 8、重复只留一次、每句截到 60 字。
        var values = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            values.Add("s" + i);
        }

        values.Add("s0");
        values.Add(new string('x', 100));
        for (int i = 5; i < 11; i++)
        {
            values.Add("s" + i);
        }

        string payload = "{\"poke\":[" + string.Join(",", values.ConvertAll(v => "\"" + v + "\"")) + "]}";
        string dir = WritePack(root, "caps", payload);
        PetPackLines lines = PetPackMetadataReader.Read(dir)?.Lines ?? PetPackLines.Empty;
        check(
            "ui-pack-lines-caps",
            lines.Poke.Count == 8 && CountOf(lines.Poke, "s0") == 1 && Longest(lines.Poke) <= 60,
            $"count={lines.Poke.Count} s0={CountOf(lines.Poke, "s0")} maxLen={Longest(lines.Poke)}");
    }

    private static void Absent(Report check, string root)
    {
        string dir = WritePack(root, "absent", null);
        PetPackLines lines = PetPackMetadataReader.Read(dir)?.Lines ?? PetPackLines.Empty;
        check("ui-pack-lines-absent", lines.IsEmpty, "没有 lines 字段 = 空（回落到宿主通用句）");
    }

    private static void BadShape(Report check, string root)
    {
        string dir = WritePack(root, "bad", "{\"poke\":\"not-an-array\",\"throw\":[1,2,3]}");
        bool ok;
        string detail;
        try
        {
            PetPackLines lines = PetPackMetadataReader.Read(dir)?.Lines ?? PetPackLines.Empty;
            ok = lines.IsEmpty;
            detail = "poke=" + lines.Poke.Count + " throw=" + lines.Throw.Count;
        }
        catch (Exception error)
        {
            ok = false;
            detail = error.GetType().Name + ": " + error.Message;
        }

        check("ui-pack-lines-bad-shape", ok, detail);
    }

    /// <summary>仓库里的示例包必须真的带台词：否则「素材包可以自己说话」只是文档里的一句愿望。</summary>
    private static void SamplePackHasLines(Report check)
    {
        string? pack = FindSamplePack();
        if (pack is null)
        {
            check("ui-pack-lines-sample", false, "找不到 assets/sample-pet");
            return;
        }

        PetPackLines lines = PetPackMetadataReader.Read(pack)?.Lines ?? PetPackLines.Empty;
        check(
            "ui-pack-lines-sample",
            lines.Poke.Count >= 2 && lines.Throw.Count >= 1 && lines.Drop.Count >= 1,
            $"poke={lines.Poke.Count} throw={lines.Throw.Count} drop={lines.Drop.Count} dir={pack}");
    }

    private static string WritePack(string root, string name, string? linesJson)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        string json = "{\"schemaVersion\":1,\"name\":\"" + name + "\",\"author\":\"check\","
            + "\"license\":\"CC0-1.0\""
            + (linesJson is null ? string.Empty : ",\"lines\":" + linesJson)
            + "}";
        File.WriteAllText(Path.Combine(dir, "petdock-sample.json"), json, new UTF8Encoding(false));
        return dir;
    }

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

    private static int CountOf(IReadOnlyList<string> lines, string value)
    {
        int count = 0;
        foreach (string line in lines)
        {
            if (string.Equals(line, value, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static int Longest(IReadOnlyList<string> lines)
    {
        int longest = 0;
        foreach (string line in lines)
        {
            longest = Math.Max(longest, line.Length);
        }

        return longest;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响判据结论。
        }
    }
}
