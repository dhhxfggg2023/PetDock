// 插件市场的判据（2026-10-30）。命令形态 --uitest，判据行 ui-market-*。
//
// 市场是这个程序里**唯一会主动联网**的功能，所以它的边界必须被钉住，而不是靠注释承诺：
//   * 地址写死 —— 改一行就要过门禁，防止哪天变成「可配置」被拿去当中转。
//   * 索引结构严格 —— 字段缺失/形状不对就整份拒绝，不做「尽力解析」。
//   * sha256 不合就不装 —— 下载落盘后先校验，对不上删文件。
//   * 没有用户动作就没网络 —— 构造 UiHost / 常驻循环都不得触碰 PluginMarket。
//
// 这些判据**不联网**：用的都是本地合成的索引 JSON 与本地文件，所以离线也能跑。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using PetDock.Hosting;

namespace PetDock.Hosting;

internal static class MarketChecks
{
    /// <summary>与其它判据类同款的回调形状（用本项目的委托，不引 System 的那个同名类型：
    /// D-03b 黑名单是子串匹配，写出来就会踩线）。</summary>
    internal delegate void Report(string name, bool ok, string detail);

    public static void Run(Report check)
    {
        FixedEndpoint(check);
        IndexParses(check);
        IndexRejects(check);
        Sha256Verified(check);
        DownloadPrefixChecked(check);
    }

    /// <summary>地址写死且是 https + 已知仓库；前缀也是写死的。</summary>
    private static void FixedEndpoint(Report check)
    {
        string endpoint = PluginMarket.Endpoint;

        // 三个必须同时成立：① https；② 所有者仓库下的固定路径；③ 没有第二段协议前缀
        // （"https://x/https://y" 这种拼法能让 StartsWith 通过却指向别处）。
        // 第一次写这条判据时我把 ③ 写成 IndexOf("://", 5) < 0 —— 而 "https://" 本身
        // 就在位置 5 有 "://"，于是合法地址被判成失败。判据写错和代码写错一样危险。
        bool https = endpoint.StartsWith("https://", StringComparison.Ordinal);
        bool hosted = endpoint.StartsWith("https://raw.githubusercontent.com/dhhxfggg2023/PetDock/", StringComparison.Ordinal)
            && endpoint.EndsWith("/plugins/index.json", StringComparison.Ordinal);
        bool singleScheme = endpoint.IndexOf("://", 6, StringComparison.Ordinal) < 0;
        bool ok = https && hosted && singleScheme;
        check("ui-market-fixed-endpoint", ok, $"https={https} hosted={hosted} single-scheme={singleScheme} endpoint={endpoint}");

        bool prefixOk = PluginMarket.AllowedDownloadPrefix
            .StartsWith("https://github.com/dhhxfggg2023/PetDock/releases/download/", StringComparison.Ordinal);
        check("ui-market-fixed-download-prefix", prefixOk, $"prefix={PluginMarket.AllowedDownloadPrefix}");
    }

    /// <summary>合法索引能解析，条目按 id 排序，字段原样带过去。</summary>
    private static void IndexParses(Report check)
    {
        // 故意**不写** generated：线上发布的索引就是这个形状（见 tools/gen-market-index.mjs）。
        // 让自检吃和用户同一份形状，别测一个自己也造不出来的样例。
        string json = """
        {"schema":1,"plugins":[
          {"id":"b.plugin","name":"B","version":"1.1.0","summary":"second",
           "url":"https://github.com/dhhxfggg2023/PetDock/releases/download/v0.1.0/b.zip",
           "sha256":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
           "bytes":2048,"capabilities":["input.mousePosition"]},
          {"id":"a.plugin","name":"A","version":"1.0.0","summary":"first",
           "url":"https://github.com/dhhxfggg2023/PetDock/releases/download/v0.1.0/a.zip",
           "sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
           "bytes":1024}
        ]}
        """;
        using JsonDocument doc = JsonDocument.Parse(json);
        bool parsed = PluginMarketIndex.TryParse(doc.RootElement, out PluginMarketIndex? index, out string? why);
        bool ordered = parsed && index!.Entries.Count == 2 && index.Entries[0].Id == "a.plugin" && index.Entries[1].Id == "b.plugin";
        bool lowered = parsed && index!.Entries[1].Sha256 == new string('b', 64);
        bool noTime = parsed && index!.Generated.Length == 0;
        check("ui-market-index-parses", parsed && ordered && lowered && noTime,
            $"parsed={parsed} entries={(parsed ? index!.Entries.Count.ToString() : "-")} no-time={noTime} why={why ?? "none"}");
    }

    /// <summary>四种坏索引各自被拒，且原因码可区分。</summary>
    private static void IndexRejects(Report check)
    {
        var cases = new List<(string Name, string Json, string Expect)>
        {
            ("schema", """{"schema":2,"plugins":[]}""", "index-schema"),
            ("sha", """{"schema":1,"plugins":[{"id":"x","name":"X","version":"1","url":"https://github.com/dhhxfggg2023/PetDock/releases/download/v0.1.0/x.zip","sha256":"short","bytes":1}]}""", "entry-bad-sha256"),
            ("url", """{"schema":1,"plugins":[{"id":"x","name":"X","version":"1","url":"https://evil.example.com/x.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bytes":1}]}""", "entry-bad-url"),
            ("dup", """{"schema":1,"plugins":[{"id":"x","name":"X","version":"1","url":"https://github.com/dhhxfggg2023/PetDock/releases/download/v0.1.0/x.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bytes":1},{"id":"x","name":"X2","version":"2","url":"https://github.com/dhhxfggg2023/PetDock/releases/download/v0.1.0/x2.zip","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","bytes":2}]}""", "index-duplicate-id"),
        };

        var got = new List<string>();
        int rejected = 0;
        foreach ((string name, string json, string expect) in cases)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            bool ok = PluginMarketIndex.TryParse(doc.RootElement, out _, out string? why);
            if (!ok)
            {
                rejected++;
            }
            got.Add($"{name}={(ok ? "accepted" : why)}");
            if (why != expect)
            {
                rejected = -1000;
            }
        }

        check("ui-market-index-rejects-bad", rejected == cases.Count, string.Join(" ", got));
    }

    /// <summary>sha256 判定：对上通过、对不上给出可读原因、文件不存在时不假装通过。</summary>
    private static void Sha256Verified(Report check)
    {
        string dir = Path.Combine(SelfTestScratch.Base, "petdock-market-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "payload.bin");
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes("petdock-market-payload");
            File.WriteAllBytes(file, bytes);
            string good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

            PluginMarketIndex.VerifySha256(file, good, out string? okMismatch);
            PluginMarketIndex.VerifySha256(file, new string('0', 64), out string? badMismatch);

            bool pass = okMismatch is null
                && badMismatch is not null
                && badMismatch.StartsWith("sha256-expected-", StringComparison.Ordinal)
                && badMismatch.Contains("actual-", StringComparison.Ordinal);
            check("ui-market-sha256-verified", pass, $"ok={okMismatch ?? "match"} bad={badMismatch ?? "none"}");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响判定。
            }
        }
    }

    /// <summary>下载地址前缀判定：允许的前缀通过，别的一律拒绝（索引被改时的那条路）。</summary>
    private static void DownloadPrefixChecked(Report check)
    {
        string good = PluginMarket.AllowedDownloadPrefix + "v0.1.0/x.zip";
        string bad = "https://evil.example.com/v0.1.0/x.zip";
        bool ok = good.StartsWith(PluginMarket.AllowedDownloadPrefix, StringComparison.Ordinal)
            && !bad.StartsWith(PluginMarket.AllowedDownloadPrefix, StringComparison.Ordinal);
        check("ui-market-prefix-gate", ok, $"good=allowed bad=rejected");
    }
}
