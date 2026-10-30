// 素材包自带台词的判据（2026-10-29）。命令形态 `--uitest`，判据行 `ui-pack-lines-*`。
//
// 读的是外部输入，所以判据的重点是「越界与坏形状不许把它带进来」：
// 最多 8 句、每句 60 字、空/重复丢弃、形状不对当没声明 —— 而不是「能不能读出一句话」。
// 台词还可以按界面语言覆盖（languages 表），判据盯着「精确 → 主语言 → 默认」这条回落链。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PetDock.Hosting;

internal static class PackLinesChecks
{
    internal delegate void Report(string name, bool ok, string detail);

    public static void Run(Report check)
    {
        string root = Path.Combine(SelfTestScratch.Base, "petdock-linescheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            ReadsAllGroups(check, root);
            Caps(check, root);
            Absent(check, root);
            BadShape(check, root);
            Languages(check, root);
            LanguagePrefix(check, root);
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

    private static void ReadsAllGroups(Report check, string root)
    {
        string dir = WritePack(
            root,
            "reads",
            "{\"poke\":[\"a\",\"b\"],\"drag\":[\"d\"],\"throw\":[\"c\"],\"drop\":[\"e\"]}");
        PetPackMetadata? meta = PetPackMetadataReader.Read(dir);
        PackLineSet lines = meta?.Lines.Default ?? PackLineSet.Empty;
        check(
            "ui-pack-lines-read",
            lines.Poke.Count == 2 && lines.Drag.Count == 1 && lines.Throw.Count == 1
                && lines.Drop.Count == 1 && !(meta?.Lines.IsEmpty ?? true),
            $"poke={lines.Poke.Count} drag={lines.Drag.Count} throw={lines.Throw.Count}"
            + $" drop={lines.Drop.Count}");
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
        PackLineSet lines = PetPackMetadataReader.Read(dir)?.Lines.Default ?? PackLineSet.Empty;
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
        string dir = WritePack(
            root,
            "bad",
            "{\"poke\":\"not-an-array\",\"throw\":[1,2,3],\"languages\":{\"en-US\":\"nope\"}}");
        bool ok;
        string detail;
        try
        {
            PetPackLines lines = PetPackMetadataReader.Read(dir)?.Lines ?? PetPackLines.Empty;
            ok = lines.IsEmpty;
            detail = "default=" + lines.Default.Poke.Count + " languages=" + lines.ByLanguage.Count;
        }
        catch (Exception error)
        {
            ok = false;
            detail = error.GetType().Name + ": " + error.Message;
        }

        check("ui-pack-lines-bad-shape", ok, detail);
    }

    /// <summary>精确语言码 → 该语言的台词；不认识的语言 → 默认那组。</summary>
    private static void Languages(Report check, string root)
    {
        string dir = WritePack(
            root,
            "lang",
            "{\"poke\":[\"默认\"],\"languages\":{\"en-US\":{\"poke\":[\"hello\"],\"drag\":[\"put me down\"]}}}");
        PetPackLines lines = PetPackMetadataReader.Read(dir)?.Lines ?? PetPackLines.Empty;
        PackLineSet english = lines.For("en-US");
        PackLineSet chinese = lines.For("zh-CN");
        PackLineSet french = lines.For("fr-FR");
        check(
            "ui-pack-lines-language",
            english.Poke.Count == 1 && english.Poke[0] == "hello" && english.Drag.Count == 1
                && chinese.Poke.Count == 1 && chinese.Poke[0] == "默认"
                && french.Poke.Count == 1 && french.Poke[0] == "默认",
            $"en={english.Poke.Count} zh={chinese.Poke.Count} fr={french.Poke.Count}");
    }

    /// <summary>清单只写 "zh" 时，界面语言 "zh-CN" 也要命中（主语言匹配）。</summary>
    private static void LanguagePrefix(Report check, string root)
    {
        string dir = WritePack(
            root,
            "prefix",
            "{\"poke\":[\"default\"],\"languages\":{\"zh\":{\"poke\":[\"主语言\"]}}}");
        PetPackLines lines = PetPackMetadataReader.Read(dir)?.Lines ?? PetPackLines.Empty;
        PackLineSet zh = lines.For("zh-CN");
        PackLineSet en = lines.For("en-US");
        check(
            "ui-pack-lines-language-prefix",
            zh.Poke.Count == 1 && zh.Poke[0] == "主语言" && en.Poke.Count == 1 && en.Poke[0] == "default",
            $"zh-CN={zh.Poke.Count} en-US={en.Poke.Count}");
    }

    /// <summary>仓库里的示例包必须真的带台词（含按语言覆盖与拖拽那句），否则只是一句愿望。</summary>
    private static void SamplePackHasLines(Report check)
    {
        string? pack = FindSamplePack();
        if (pack is null)
        {
            check("ui-pack-lines-sample", false, "找不到 pets/sample-pet");
            return;
        }

        PetPackLines lines = PetPackMetadataReader.Read(pack)?.Lines ?? PetPackLines.Empty;
        PackLineSet english = lines.For("en-US");
        check(
            "ui-pack-lines-sample",
            lines.Default.Poke.Count >= 2 && lines.Default.Drag.Count >= 1
                && lines.Default.Throw.Count >= 1 && lines.Default.Drop.Count >= 1
                && !english.IsEmpty,
            $"poke={lines.Default.Poke.Count} drag={lines.Default.Drag.Count}"
            + $" throw={lines.Default.Throw.Count} drop={lines.Default.Drop.Count}"
            + $" languages={lines.ByLanguage.Count} dir={pack}");
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
            string candidate = Path.Combine(cursor.FullName, "pets", "sample-pet");
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
