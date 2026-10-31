// 程序自身更新的判据（2026-10-31）。命令形态 --uitest，判据行 ui-update-*。
//
// 更新是**第二个**主动出网的地方（第一个是插件市场），所以边界同样要被钉住而不是靠注释承诺：
//   * 地址写死 —— 改一行就要过门禁，防止哪天变成「可配置」被当中转；
//   * 更新源结构严格 —— 字段缺失/形状不对整份拒绝；
//   * 版本比较按数值 —— 按字符串比会把 0.10.0 判成小于 0.9.0，第十个小版本之后所有人收不到更新；
//   * 三种结局分得开 —— 「没查到」不许被显示成「已是最新」。
//
// 这些判据**不联网**：喂的是本地合成的 JSON，所以离线也能跑。
using System;
using System.Text.Json;
using PetDock.Core.Ui;
using PetDock.Hosting;

namespace PetDock.Hosting;

internal static class UpdateChecks
{
    /// <summary>与其它判据类同款的回调形状（D-03b：不写系统那个泛型委托的名字）。</summary>
    internal delegate void Report(string name, bool ok, string detail);

    private const string GoodUrl =
        "https://github.com/dhhxfggg2023/PetDock/releases/download/v9.9.9/PetDock-9.9.9-win-x64.zip";

    public static void Run(Report check)
    {
        FixedEndpoint(check);
        VersionCompare(check);
        FeedParses(check);
        FeedRejects(check);
        ResultsStayDistinct(check);

        // 2026-10-31 第二片：下载完之后的两条路（立即重启装 / 稍后下次启动装），
        // 以及常驻的重启。都在临时目录里跑真文件，不联网、不碰真实安装目录。
        UpdateStageChecks.Run(check);
        UpdateStartupChecks.Run(check);
    }

    /// <summary>地址写死且是 https + 本仓库；下载前缀也是写死的，两处都不许「可配置」。</summary>
    private static void FixedEndpoint(Report check)
    {
        string endpoint = UpdateService.Endpoint;
        bool https = endpoint.StartsWith("https://", StringComparison.Ordinal);
        // 2026-10-31 改口径：不再走 raw.githubusercontent.com（所有者这台机器上它被 hosts 指到
        // 127.0.0.1，永远是 30 秒超时），改走同一台服务的 contents API —— 路径仍写死、仍只走 https。
        bool hosted = endpoint.StartsWith(
                "https://api.github.com/repos/dhhxfggg2023/PetDock/contents/", StringComparison.Ordinal)
            && endpoint.EndsWith("/update.json?ref=main", StringComparison.Ordinal);
        bool singleScheme = endpoint.IndexOf("://", 6, StringComparison.Ordinal) < 0;

        bool prefixOk = UpdateService.AllowedDownloadPrefix
            .StartsWith("https://github.com/dhhxfggg2023/PetDock/releases/download/", StringComparison.Ordinal);

        check("ui-update-fixed-endpoint", https && hosted && singleScheme && prefixOk,
            $"https={https} hosted={hosted} single-scheme={singleScheme} prefix={prefixOk} endpoint={endpoint}");
    }

    /// <summary>
    /// 版本比较必须按数值。这条判据的价值全在 0.9.0 vs 0.10.0 那一对：
    /// 按字符串比 "0.10.0" &lt; "0.9.0"，于是第十个小版本发布之后，所有老用户都会被告知「已是最新」。
    /// </summary>
    private static void VersionCompare(Report check)
    {
        bool tenWins = UpdateFeed.Compare("0.10.0", "0.9.0") > 0;
        bool same = UpdateFeed.Compare("0.2.0", "0.2.0") == 0;
        bool newer = UpdateFeed.Compare("0.2.1", "0.2.0") > 0;
        bool older = UpdateFeed.Compare("0.1.9", "0.2.0") < 0;
        bool major = UpdateFeed.Compare("1.0.0", "0.99.99") > 0;

        // 判不了就不催更：形状不对一律 0（宁可少提示，不误报）。
        bool garbage = UpdateFeed.Compare("0.2", "0.2.0") == 0 && UpdateFeed.Compare("x.y.z", "0.2.0") == 0;

        check("ui-update-version-compare", tenWins && same && newer && older && major && garbage,
            $"0.10.0>0.9.0={tenWins} same={same} newer={newer} older={older} major={major} garbage={garbage}");
    }

    /// <summary>合法更新源能解析，字段原样带过去。</summary>
    private static void FeedParses(Report check)
    {
        // 与线上同一份形状（tools/gen-update-feed.mjs）：只有版本、地址、说明。
        // 让自检吃和用户同一份形状，别测一个自己也造不出来的样例。
        string json = """
        {"schema":1,"version":"9.9.9","url":"https://github.com/dhhxfggg2023/PetDock/releases/download/v9.9.9/PetDock-9.9.9-win-x64.zip","notes":"hello"}
        """;
        using JsonDocument doc = JsonDocument.Parse(json);
        bool parsed = UpdateFeed.TryParse(doc.RootElement, out UpdateFeed? feed, out string? why);

        // 旁车地址 = 包地址 + .sha256：校验值来自发布资产，不来自更新源本身（见 UpdateFeed 文件头）。
        bool sidecar = parsed && feed!.SidecarUrl == feed.Url + ".sha256";
        bool fields = parsed && feed!.Version == "9.9.9" && feed.Notes == "hello";
        check("ui-update-feed-parses", parsed && sidecar && fields,
            $"parsed={parsed} sidecar={sidecar} fields={fields} why={why ?? "none"}");
    }

    /// <summary>四种坏更新源各自被拒，原因码可区分（形状不对就整份拒绝，不做「尽力解析」）。</summary>
    private static void FeedRejects(Report check)
    {
        var cases = new (string Name, string Json, string Expect)[]
        {
            ("schema", """{"schema":2,"version":"1.0.0","url":"https://github.com/dhhxfggg2023/PetDock/releases/download/v1/x.zip"}""", "feed-schema"),
            ("version", """{"schema":1,"version":"1.0","url":"https://github.com/dhhxfggg2023/PetDock/releases/download/v1/x.zip"}""", "feed-bad-version"),
            ("url", """{"schema":1,"version":"1.0.0","url":"https://evil.example.com/x.zip"}""", "feed-bad-url"),
            ("notobject", """[]""", "feed-not-object"),
        };

        var got = new System.Collections.Generic.List<string>();
        bool all = true;
        foreach ((string name, string json, string expect) in cases)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            bool ok = UpdateFeed.TryParse(doc.RootElement, out _, out string? why);
            if (ok || why != expect)
            {
                all = false;
            }

            got.Add(name + "=" + (ok ? "accepted" : why));
        }

        check("ui-update-feed-rejects-bad", all, string.Join(" ", got));
    }

    /// <summary>
    /// 三种结局必须分得开 —— 这是这一片的**核心**判据。
    /// 把「没查到」显示成「已是最新」，用户会在一个其实有更新的版本上永远不再检查。
    /// </summary>
    private static void ResultsStayDistinct(Report check)
    {
        var found = new UpdateCheckLine("0.2.0", "0.3.0", 100, "notes", null);
        var current = new UpdateCheckLine("0.2.0", null, 0, string.Empty, null);
        var failed = new UpdateCheckLine("0.2.0", null, 0, string.Empty, "offline");

        bool ok = found.HasUpdate && !found.UpToDate
            && current.UpToDate && !current.HasUpdate
            && !failed.HasUpdate && !failed.UpToDate
            && failed.Error is not null;

        check("ui-update-three-outcomes", ok,
            $"found={found.HasUpdate}/{found.UpToDate} current={current.HasUpdate}/{current.UpToDate}"
            + $" failed={failed.HasUpdate}/{failed.UpToDate} error={failed.Error}");
    }
}
