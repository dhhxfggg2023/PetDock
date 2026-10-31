// 程序自身的更新源（2026-10-31 用户要求：「程序内部好像没有更新功能啊，不太方便呢」）。
//
// 这一片只做**纯判定**：把一小份 JSON 读成更新源、比版本号。它不联网、不落盘、不碰文件系统。
//
// 为什么更新源里**没有** sha256 与体积（2026-10-31 的教训）：
//   一开始按插件市场的形态写了 sha256 + bytes，结果 dist 的 repro 判据立刻红了 ——
//   那是个真循环：zip 里写着 source_commit=C，update.json 写着 sha256(zip_at_C)，
//   提交 update.json 让 HEAD 变成 C'，在 C' 重新构建 zip 里就是 C'，指纹又变了。
//   只要更新源里出现任何「从本次构建产物量出来」的字段，它就不可能和产物同时自洽。
//   （同一类错误上一轮在 plugins/index.json 的 generated 上已经付过一次代价。）
//
// 所以：更新源只声明**构建之前就确定**的东西 —— 版本号与下载地址（都来自 package.json）；
// 完整性交给 Release 上同一个 zip 旁边的 .sha256 旁车文件，程序下载后比对它。
// 这样 update.json 可以与版本号一起提交，构建永远从干净的树开始。
using System;
using System.Text.Json;

namespace PetDock.Hosting;

/// <summary>一份更新源：新版本的版本号、下载地址与说明。</summary>
internal sealed record UpdateFeed(
    int Schema,
    string Version,
    string Url,
    string Notes)
{
    /// <summary>安装包体积上限（防止被引到一个填满磁盘的地址上）。</summary>
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

        // TryGetInt32 而不是 GetInt32：这份 JSON 来自网络，`"schema": 1.5` 会让 GetInt32 抛
        // FormatException 从 TryParse 里穿出去 —— 一个「返回原因码」的函数把进程抛穿是更坏的失败。
        if (!root.TryGetProperty("schema", out JsonElement schema)
            || !schema.TryGetInt32(out int schemaVersion)
            || schemaVersion != 1)
        {
            why = "feed-schema";
            return false;
        }

        string? version = Str(root, "version");
        string? url = Str(root, "url");

        if (version is null || !IsVersion(version))
        {
            why = "feed-bad-version";
            return false;
        }

        // 地址前缀写死：更新源里出现别的地址 = 这份 JSON 被改过或被投毒。不猜、不改写。
        if (url is null || !url.StartsWith(UpdateService.AllowedDownloadPrefix, StringComparison.Ordinal))
        {
            why = "feed-bad-url";
            return false;
        }

        feed = new UpdateFeed(1, version, url, Str(root, "notes") ?? string.Empty);
        return true;
    }

    /// <summary>旁车地址：同一个 zip 旁边的 .sha256（发布时一起上传，不进 git）。</summary>
    public string SidecarUrl => Url + ".sha256";

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

    /// <summary>读一行「当前版本 → 新版本」的可读读数（日志与判据共用）。</summary>
    public string Describe(string current) => $"current={current} latest={Version} url={Url}";
}
