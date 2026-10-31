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

        // 同 UpdateFeed：索引也是网络来的，`"schema": 1.5` 不能把 TryParse 抛穿。
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schema", out JsonElement schemaElement) ||
            !schemaElement.TryGetInt32(out int schemaVersion) ||
            schemaVersion != 1)
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

        // id 与 version 会被拼进下载的临时文件名（PluginMarket.DownloadAsync），所以它们的形状
        // 必须是**安全的文件名材料**：不许出现路径分隔符，也不许以点开头。2026-10-31 之前只查了
        // 「非空」，索引里一个 ../ 就能让下载写到暂存区之外 —— 而索引本身没有签名，
        // 它的 sha256 只能证明「下载到的字节与索引说的一致」，不能证明「索引没被改」。
        if (!IsSafeSegment(id, maxLength: 64))
        {
            why = "entry-bad-id";
            return false;
        }

        if (!IsSafeVersion(version))
        {
            why = "entry-bad-version";
            return false;
        }

        if (name.Length > 120 || (summary?.Length ?? 0) > 600)
        {
            why = "entry-too-long";
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

    /// <summary>安全的一段：小写字母/数字/点/横线/下划线，首尾必须是字母或数字，长度受限。</summary>
    private static bool IsSafeSegment(string value, int maxLength)
    {
        if (value.Length == 0 || value.Length > maxLength || !char.IsLetterOrDigit(value[0]) || !char.IsLetterOrDigit(value[^1]))
        {
            return false;
        }

        foreach (char c in value)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '.' or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>版本号只允许数字与点（与更新源同一条纪律：它只进本地文件名，不是路径）。</summary>
    private static bool IsSafeVersion(string value)
    {
        if (value.Length == 0 || value.Length > 32 || !char.IsDigit(value[0]))
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsDigit(c) && c != '.')
            {
                return false;
            }
        }

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
