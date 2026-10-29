// 素材包元数据（2026-10-29）：作者 / 许可 / 图标 / 画布尺寸。
//
// 内核仍然不解析素材格式：这里只读标准包自己的 petdock-sample.json 的公开字段，
// 用来在宠物页展示「这是谁做的、什么许可、多大画布」。读不出来就当作未声明，不猜。
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

/// <summary>标准包能公开给界面的元数据；字段缺失一律 null/0，不编造。</summary>
internal sealed record PetPackMetadata(
    string? Author,
    string? License,
    string? IconPath,
    int NaturalWidth,
    int NaturalHeight,
    PetPackLines Lines);

internal static class PetPackMetadataReader
{
    private const string ManifestName = "petdock-sample.json";

    /// <summary>读取一个素材包目录；没有标准包清单/坏 JSON 时返回 null（不阻断列表）。</summary>
    public static PetPackMetadata? Read(string packDir)
    {
        if (string.IsNullOrWhiteSpace(packDir))
        {
            return null;
        }

        string manifest = Path.Combine(packDir, ManifestName);
        if (!File.Exists(manifest))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest, Encoding.UTF8));
            JsonElement root = doc.RootElement;
            int width = 0;
            int height = 0;
            if (root.TryGetProperty("naturalSize", out JsonElement size) && size.ValueKind == JsonValueKind.Object)
            {
                width = ReadInt(size, "w");
                height = ReadInt(size, "h");
            }

            string? icon = null;
            foreach (string name in new[] { "icon.png", "thumbnail.png" })
            {
                string candidate = Path.Combine(packDir, name);
                if (File.Exists(candidate))
                {
                    icon = candidate;
                    break;
                }
            }

            return new PetPackMetadata(
                Text(root, "author"),
                LicenseText(root),
                icon,
                width,
                height,
                PetPackLines.Read(root));
        }
        catch (Exception error)
        {
            KernelLog.Write($"pack-metadata-unreadable path={packDir} {error.GetType().Name}");
            return null;
        }
    }

    private static string? Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? Clean(value.GetString())
            : null;

    private static string? LicenseText(JsonElement root)
    {
        if (!root.TryGetProperty("license", out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return Clean(value.GetString());
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in new[] { "spdx", "name", "id" })
            {
                if (value.TryGetProperty(key, out JsonElement part) && part.ValueKind == JsonValueKind.String)
                {
                    string? text = Clean(part.GetString());
                    if (text is not null)
                    {
                        return text;
                    }
                }
            }
        }

        return null;
    }

    private static int ReadInt(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number) ? number : 0;

    private static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
