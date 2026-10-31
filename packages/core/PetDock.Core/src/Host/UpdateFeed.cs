// 程序自身的更新源（2026-10-31 用户要求：「程序内部好像没有更新功能啊，不太方便呢」）。
//
// 这一片只做**纯判定**：把一小份 JSON 读成更新源、比版本号。它不联网、不落盘、不碰文件系统，
// 所以整份逻辑都能在判据里直接喂数据 —— 版本比较写错一次就会让「已是最新」和「有新版本」
// 说反，而那种错在界面上看起来完全正常。
//
// 为什么更新源是独立的一份 JSON 而不是查 Releases API：
//   · Releases API 要 JSON 解析分页、要处理预发布标记，而这里只需要「最新一版的版本号 + 地址 + 指纹」；
//   · 它跟着仓库走（update.json），由 tools/gen-update-feed.mjs 在打包时按产物真实指纹生成 ——
//     手写这份文件一定会漂移成「声明的 sha256 和包不一样」。
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PetDock.Hosting;

/// <summary>一份更新源：新版本的版本号、下载地址、指纹与体积。</summary>
internal sealed record UpdateFeed(
    int Schema,
    string Version,
    string Url,
    string Sha256,
    long Bytes,
    string Notes)
{
    /// <summary>安装包体积上限（与发布物同量级；防止被引到一个填满磁盘的地址上）。</summary>
    internal const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>结构判定。失败时 why 是机器可读短码，界面再翻成人话。</summary>
    public static bool TryParse(JsonElement root, out UpdateFeed? feed, out string? why)
    {
        feed = null;
        why = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            why = "feed-not-object";
            return false;
        }

        if (!root.TryGetProperty("schema", out JsonElement schema)
            || schema.ValueKind != JsonValueKind.Number
            || schema.GetInt32() != 1)
        {
            why = "feed-schema";
            return false;
        }

        string? version = Str(root, "version");
        string? url = Str(root, "url");
        string? sha = Str(root, "sha256");

        if (version is null || !IsVersion(version))
        {
            why = "feed-bad-version";
            return false;
        }

        if (sha is null || sha.Length != 64 || !IsHex(sha))
        {
            why = "feed-bad-sha256";
            return false;
        }

        // 地址前缀写死：更新源里出现别的地址 = 这份 JSON 被改过或被投毒。不猜、不改写。
        if (url is null || !url.StartsWith(UpdateService.AllowedDownloadPrefix, StringComparison.Ordinal))
        {
            why = "feed-bad-url";
            return false;
        }

        long bytes = root.TryGetProperty("bytes", out JsonElement b) && b.ValueKind == JsonValueKind.Number
            ? b.GetInt64()
            : 0;
        if (bytes <= 0 || bytes > MaxBytes)
        {
            why = "feed-bad-size";
            return false;
        }

        feed = new UpdateFeed(1, version, url, sha.ToLowerInvariant(), bytes, Str(root, "notes") ?? "");
        return true;
    }

    /// <summary>是不是 x.y.z 形状（发布版本号只到这个粒度，见 version-parity）。</summary>
    public static bool IsVersion(string text)
    {
        string[] parts = text.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (part.Length == 0 || part.Length > 9)
            {
                return false;
            }

            foreach (char c in part)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// 比版本号：a &lt; b 返回 -1，相等 0，a &gt; b 返回 1。
    ///
    /// 逐段按**数值**比，不是按字符串：按字符串比会把 "0.10.0" 判成比 "0.9.0" 小，
    /// 于是第十个小版本之后所有用户都收不到更新 —— 而界面上只会说「已是最新」。
    /// 任何一边不是 x.y.z 形状时返回 0（判不了就不催更，宁可少提示也不误报）。
    /// </summary>
    public static int Compare(string a, string b)
    {
        if (!IsVersion(a) || !IsVersion(b))
        {
            return 0;
        }

        string[] left = a.Split('.');
        string[] right = b.Split('.');
        for (int i = 0; i < 3; i++)
        {
            int x = int.Parse(left[i], System.Globalization.CultureInfo.InvariantCulture);
            int y = int.Parse(right[i], System.Globalization.CultureInfo.InvariantCulture);
            if (x != y)
            {
                return x < y ? -1 : 1;
            }
        }

        return 0;
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

    /// <summary>读一行「当前版本 → 新版本」的可读读数（日志与判据共用）。</summary>
    public string Describe(string current) =>
        $"current={current} latest={Version} bytes={Bytes} sha256={Sha256[..8]}";
}
