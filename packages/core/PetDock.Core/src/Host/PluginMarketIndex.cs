// 市场索引的形状与校验（2026-10-30）。
//
// 索引是一份 JSON，由所有者随仓库发布（plugins/index.json），不含任何可执行内容：
//
//   { "schema": 1,
//     "generated": "2026-10-30T00:00:00Z",
//     "plugins": [ { "id", "name", "version", "summary", "url", "sha256", "bytes", "capabilities" } ] }
//
// 这里只做**结构判定**：字段齐不齐、sha256 是不是 64 位十六进制、url 前缀对不对、体积上限。
// 语义上的「这插件能干什么」不在这里猜 —— 装的时候仍然走 PluginInstaller 的清单校验。
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PetDock.Hosting;

/// <summary>解析后的索引。<see cref="Entries"/> 已按 id 排序，便于判据比对。</summary>
internal sealed record PluginMarketIndex(
    int Schema,
    string Generated,
    IReadOnlyList<MarketEntry> Entries)
{
    /// <summary>单个插件包的体积上限（与安装器的 MaxTotalBytes 同量级，防止索引把人引到大文件上）。</summary>
    internal const long MaxEntryBytes = 64L * 1024 * 1024;

    /// <summary>结构判定。失败时 why 是机器可读的短码，界面再翻成人话。</summary>
    public static bool TryParse(JsonElement root, out PluginMarketIndex? index, out string? why)
    {
        index = null;
        why = null;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schema", out JsonElement schemaElement) ||
            schemaElement.ValueKind != JsonValueKind.Number ||
            schemaElement.GetInt32() != 1)
        {
            why = "index-schema";
            return false;
        }

        if (!root.TryGetProperty("plugins", out JsonElement pluginsElement) ||
            pluginsElement.ValueKind != JsonValueKind.Array)
        {
            why = "index-plugins";
            return false;
        }

        string generated = root.TryGetProperty("generated", out JsonElement g) && g.ValueKind == JsonValueKind.String
            ? g.GetString() ?? ""
            : "";

        var entries = new List<MarketEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in pluginsElement.EnumerateArray())
        {
            if (!TryParseEntry(item, out MarketEntry? entry, out why))
            {
                return false;
            }

            if (!seen.Add(entry!.Id))
            {
                why = "index-duplicate-id";
                return false;
            }

            entries.Add(entry);
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        index = new PluginMarketIndex(1, generated, entries);
        return true;
    }

    private static bool TryParseEntry(JsonElement item, out MarketEntry? entry, out string? why)
    {
        entry = null;
        why = null;
        if (item.ValueKind != JsonValueKind.Object)
        {
            why = "entry-not-object";
            return false;
        }

        string? id = Str(item, "id");
        string? name = Str(item, "name");
        string? version = Str(item, "version");
        string? summary = Str(item, "summary");
        string? url = Str(item, "url");
        string? sha = Str(item, "sha256");

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
        {
            why = "entry-missing-fields";
            return false;
        }

        if (sha is null || sha.Length != 64 || !IsHex(sha))
        {
            why = "entry-bad-sha256";
            return false;
        }

        if (url is null || !url.StartsWith(PluginMarket.AllowedDownloadPrefix, StringComparison.Ordinal))
        {
            why = "entry-bad-url";
            return false;
        }

        long bytes = item.TryGetProperty("bytes", out JsonElement b) && b.ValueKind == JsonValueKind.Number
            ? b.GetInt64()
            : 0;
        if (bytes < 0 || bytes > MaxEntryBytes)
        {
            why = "entry-bad-size";
            return false;
        }

        var caps = new List<string>();
        if (item.TryGetProperty("capabilities", out JsonElement capElement) && capElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement c in capElement.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.String)
                {
                    caps.Add(c.GetString() ?? "");
                }
            }
        }

        entry = new MarketEntry(id, name, version, summary ?? "", url, sha.ToLowerInvariant(), bytes, caps);
        return true;
    }

    private static string? Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static bool IsHex(string value)
    {
        foreach (char c in value)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>校验一个文件的 sha256；不匹配时 mismatch 给出「期望/实际」各前 8 位。</summary>
    public static void VerifySha256(string path, string expected, out string? mismatch)
    {
        mismatch = null;
        byte[] actual;
        using (FileStream file = File.OpenRead(path))
        {
            actual = SHA256.HashData(file);
        }

        string hex = Convert.ToHexString(actual).ToLowerInvariant();
        if (string.Equals(hex, expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        mismatch = $"sha256-expected-{expected[..8]}-actual-{hex[..8]}";
    }
}
