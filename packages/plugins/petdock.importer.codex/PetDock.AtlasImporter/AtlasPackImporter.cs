using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
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
    private const int AtlasHeight = 1872;
    private const int CellWidth = 192;
    private const int CellHeight = 208;
    private const int Columns = 8;

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

        BitmapDecoder decoder = BitmapDecoder.Create(
            new Uri(Path.GetFullPath(sheetPath)),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapSource atlas = decoder.Frames[0];
        atlas.Freeze();
        if (atlas.PixelWidth != AtlasWidth || atlas.PixelHeight != AtlasHeight)
        {
            throw new InvalidDataException(
                $"图集必须是 {AtlasWidth}×{AtlasHeight}，实际 {atlas.PixelWidth}×{atlas.PixelHeight}");
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
                    ["height"] = AtlasHeight,
                    ["cellWidth"] = CellWidth,
                    ["cellHeight"] = CellHeight,
                    ["rows"] = Rows.Select(row => row.Id).ToArray(),
                    ["frames"] = total,
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
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string? FindSheetByExtension(string sourceDir)
    {
        foreach (string name in new[] { "spritesheet.webp", "spritesheet.png" })
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
