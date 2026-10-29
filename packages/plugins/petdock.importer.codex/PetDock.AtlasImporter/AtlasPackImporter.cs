using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.AtlasImporter;

/// <summary>
/// Codex Pets / OpenPets 的固定图集 → PetDock 标准包。
/// 契约（2026-10-29 核对）：1536×1872，8 列 × 9 行，单格 192×208，57 个有效帧；
/// 行顺序固定：idle / running-right / running-left / waving / jumping / failed / waiting / running / review。
/// 转换只读用户目录，产物是独立标准包；不引用原图集。
/// </summary>
public sealed class AtlasPackImporter : IPackImporter, IPackImporterProgress
{
    private const int AtlasWidth = 1536;
    private const int CellWidth = 192;
    private const int CellHeight = 208;

    /// <summary>
    /// 认识的两个几何：v1 = 8×9（1872 高），v2 = 8×11（2288 高）。
    /// v2 多出来的两行**不导入**：它们的语义没有公开契约，猜一个行名就等于编造素材含义。
    /// 只认「前 9 行仍是同一顺序」这一条——它由 v1 的契约固定，v2 若改了顺序本工具会读错，
    /// 所以 provenance 里记下高度与版本，出问题能一眼看出是哪种图集。
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> KnownHeights = new Dictionary<int, string>
    {
        [1872] = "v1",
        [2288] = "v2",
    };

    private static readonly (string Id, int Frames, double Fps, bool Loop)[] Rows =
    {
        ("idle", 6, 6.0, true),
        ("running-right", 8, 12.0, true),
        ("running-left", 8, 12.0, true),
        ("waving", 4, 8.0, false),
        ("jumping", 5, 10.0, false),
        ("failed", 8, 8.0, false),
        ("waiting", 6, 4.0, true),
        ("running", 6, 8.0, true),
        ("review", 6, 6.0, false),
    };

    public string Id => "com.petdock.atlas.importer";

    public bool CanImport(string sourceDir)
    {
        try
        {
            string? sheet = ResolveSheet(sourceDir);
            return sheet is not null && File.Exists(sheet);
        }
        catch
        {
            return false;
        }
    }

    public void Import(string sourceDir, string outDir) =>
        Import(sourceDir, outDir, _ => { }, default);

    public void Import(string sourceDir, string outDir, PackImportProgressNotify report, CancellationToken token)
    {
        string sheetPath = ResolveSheet(sourceDir)
            ?? throw new InvalidDataException("pet.json 没有 spritesheetPath，且目录里没有 spritesheet.webp/png");
        if (!File.Exists(sheetPath))
        {
            throw new FileNotFoundException("图集文件不存在", sheetPath);
        }

        using JsonDocument pet = JsonDocument.Parse(File.ReadAllText(Path.Combine(sourceDir, "pet.json")));
        JsonElement root = pet.RootElement;
        string id = Text(root, "id") ?? throw new InvalidDataException("pet.json 缺少 id");
        string displayName = Text(root, "displayName") ?? id;
        string? description = Text(root, "description");
        string? sourceUrl = Text(root, "sourceUrl");

        BitmapSource atlas = DecodeSheet(sheetPath);
        if (atlas.PixelWidth != AtlasWidth || !KnownHeights.ContainsKey(atlas.PixelHeight))
        {
            throw new InvalidDataException(
                $"图集必须是 {AtlasWidth}×{string.Join(" 或 ", KnownHeights.Keys)}"
                + $"（v1 / v2），实际 {atlas.PixelWidth}×{atlas.PixelHeight}");
        }

        int rows = atlas.PixelHeight / CellHeight;
        int extraRows = rows - Rows.Length;
        if (extraRows > 0)
        {
            PluginLog.Info(
                $"atlas version={KnownHeights[atlas.PixelHeight]} rows={rows} ignored-extra-rows={extraRows}"
                + "（多出来的行没有公开语义，不导入）");
        }

        int total = Rows.Sum(row => row.Frames);
        int done = 0;
        report?.Invoke(new PackImportProgress(0, total));
        try
        {
            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(Path.Combine(outDir, "anim"));
            var animations = new List<Dictionary<string, object?>>();
            foreach ((string rowId, int frames, double fps, bool loop) in Rows)
            {
                token.ThrowIfCancellationRequested();
                string imageDir = Path.Combine(outDir, "img", rowId);
                Directory.CreateDirectory(imageDir);
                var relativeFrames = new List<string>();
                for (int column = 0; column < frames; column++)
                {
                    token.ThrowIfCancellationRequested();
                    var rect = new Int32Rect(column * CellWidth, RowOf(rowId) * CellHeight, CellWidth, CellHeight);
                    var cropped = new CroppedBitmap(atlas, rect);
                    cropped.Freeze();
                    string file = Path.Combine(imageDir, column.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ".png");
                    SavePng(cropped, file);
                    relativeFrames.Add($"img/{rowId}/{column:00}.png");
                    if (rowId == "idle" && column == 0)
                    {
                        File.Copy(file, Path.Combine(outDir, "icon.png"), overwrite: true);
                    }

                    done++;
                    report?.Invoke(new PackImportProgress(done, total));
                }

                WriteJson(Path.Combine(outDir, "anim", rowId + ".json"), new Dictionary<string, object?>
                {
                    ["frames"] = relativeFrames,
                    ["fps"] = fps,
                    ["loop"] = loop ? "loop" : "once",
                });
                animations.Add(new Dictionary<string, object?>
                {
                    ["id"] = rowId,
                    ["clip"] = $"anim/{rowId}.json",
                });
            }

            WriteJson(Path.Combine(outDir, "petdock-sample.json"), new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["name"] = displayName,
                ["author"] = "imported from Codex Pets atlas",
                ["license"] = "UNLICENSED-third-party (see codex-pets-provenance.json)",
                ["naturalSize"] = new Dictionary<string, object?> { ["w"] = CellWidth, ["h"] = CellHeight },
                ["preferredFps"] = 6.0,
                ["animations"] = animations,
                ["defaultAnimation"] = "idle",
            });
            WriteJson(Path.Combine(outDir, "codex-pets-provenance.json"), new Dictionary<string, object?>
            {
                ["source"] = "codex-pets-atlas",
                ["id"] = id,
                ["displayName"] = displayName,
                ["description"] = description,
                ["sourceUrl"] = sourceUrl,
                ["atlas"] = new Dictionary<string, object?>
                {
                    ["width"] = AtlasWidth,
                    ["height"] = atlas.PixelHeight,
                    ["version"] = KnownHeights[atlas.PixelHeight],
                    ["cellWidth"] = CellWidth,
                    ["cellHeight"] = CellHeight,
                    ["rows"] = Rows.Select(row => row.Id).ToArray(),
                    ["frames"] = total,
                    ["ignoredRows"] = Math.Max(0, rows - Rows.Length),
                },
            });
            report?.Invoke(new PackImportProgress(total, total));
        }
        catch
        {
            // 输出目录由宿主/CLI 的调用方负责清理；这里不递归删用户指定的目录。
            throw;
        }
    }

    /// <summary>
    /// 解码图集。失败时给一条能照着做的原因：WebP 能不能读取决于本机 WIC codec，
    /// 而「读不出来」这句话对用户没有下一个动作 —— 明确告诉他换成 PNG 再试。
    /// </summary>
    private static BitmapSource DecodeSheet(string sheetPath)
    {
        try
        {
            BitmapDecoder decoder = BitmapDecoder.Create(
                new Uri(Path.GetFullPath(sheetPath)),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapSource atlas = decoder.Frames[0];
            atlas.Freeze();
            return atlas;
        }
        catch (Exception error)
        {
            bool webp = string.Equals(Path.GetExtension(sheetPath), ".webp", StringComparison.OrdinalIgnoreCase);
            string hint = webp
                ? "本机没有可用的 WebP 解码器（WIC codec）：把同一张图集转成同名 .png 放在旁边再导入"
                : "图集文件读不出来（可能已损坏，或不是 PNG / WebP）";
            throw new InvalidDataException($"{Path.GetFileName(sheetPath)} 解码失败：{hint}", error);
        }
    }

    /// <summary>同一目录里有没有同名 .png（用来绕开本机缺失的 WebP codec）。</summary>
    private static string? PngSibling(string sheetPath)
    {
        if (!string.Equals(Path.GetExtension(sheetPath), ".webp", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string png = Path.ChangeExtension(sheetPath, ".png");
        return File.Exists(png) ? png : null;
    }

    private static int RowOf(string id)
    {
        for (int i = 0; i < Rows.Length; i++)
        {
            if (string.Equals(Rows[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException("unknown atlas row: " + id);
    }

    private static string? ResolveSheet(string sourceDir)
    {
        string manifest = Path.Combine(sourceDir, "pet.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
        string? relative = Text(doc.RootElement, "spritesheetPath");
        if (string.IsNullOrWhiteSpace(relative))
        {
            relative = FindSheetByExtension(sourceDir);
        }

        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        string root = Path.GetFullPath(sourceDir) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(sourceDir, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 同名 PNG 优先：WebP 能不能解码取决于本机 codec，而导入结果与编码方式无关，
        // 所以有能读的那一份就不去赌读不出来的那一份。
        return PngSibling(full) ?? full;
    }

    private static string? FindSheetByExtension(string sourceDir)
    {
        // PNG 在前：理由同 ResolveSheet 里的同名优先（WIC 的 WebP 支持不是必然的）。
        foreach (string name in new[] { "spritesheet.png", "spritesheet.webp" })
        {
            if (File.Exists(Path.Combine(sourceDir, name)))
            {
                return name;
            }
        }

        return null;
    }

    private static string? Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void SavePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void WriteJson(string path, object payload) =>
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

}
