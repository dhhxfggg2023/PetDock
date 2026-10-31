// 出网通道的判据（2026-10-31）。判据行 ui-net-*。
//
// 为什么这几条必须存在 —— 一次真实的致命缺陷：
//   更新源与插件市场各自 new 了一个 HttpClient，并把「只连一个域名」实现成
//   AllowAutoRedirect = false；而 GitHub 的 Release 资产**必然**先回一次 302
//   （本机实测：zip 与它的 .sha256 都是 302 → release-assets.githubusercontent.com）。
//   于是「下载新版本」与「从市场装插件」在真实网络上永远只会拿到 download-status-302，
//   而全部门禁绿：判据只解析 JSON、打包自检自造 zip、联网那一步用的是它自己的下载器 ——
//   产品代码从没被真实流量碰过。
//
// 修法是「自己跟重定向、每一跳先校验主机」，所以这里判的正是那条策略本身：
// 把样例喂给纯函数，断言放行与拒绝的边界。不联网也能判，判的就是产品走的那条路。
using System;
using System.Net;
using PetDock.Hosting;

namespace PetDock.Hosting;

internal static class NetChecks
{
    internal static void Run(UpdateChecks.Report check)
    {
        AllowedHosts(check);
        RedirectPolicy(check);
        ContentsAddress(check);
        FailureNamesHost(check);
    }

    /// <summary>能说话的主机只有 GitHub 那几台；旧地址（raw）与伪装后缀一律拒绝。</summary>
    private static void AllowedHosts(UpdateChecks.Report check)
    {
        bool allowed = GitHubHttp.IsAllowedHost("api.github.com")
            && GitHubHttp.IsAllowedHost("github.com")
            && GitHubHttp.IsAllowedHost("release-assets.githubusercontent.com")
            && GitHubHttp.IsAllowedHost("objects.githubusercontent.com");
        bool refused = !GitHubHttp.IsAllowedHost("raw.githubusercontent.com")
            && !GitHubHttp.IsAllowedHost("evil.example.com")
            && !GitHubHttp.IsAllowedHost("github.com.evil.example.com")
            && !GitHubHttp.IsAllowedHost(string.Empty);
        check("ui-net-allowed-hosts", allowed && refused, $"allowed={allowed} refused={refused}");

        bool redirects = GitHubHttp.IsRedirect(HttpStatusCode.Found)
            && GitHubHttp.IsRedirect(HttpStatusCode.MovedPermanently)
            && GitHubHttp.IsRedirect(HttpStatusCode.SeeOther)
            && GitHubHttp.IsRedirect(HttpStatusCode.TemporaryRedirect)
            && GitHubHttp.IsRedirect(HttpStatusCode.PermanentRedirect);
        bool quiet = !GitHubHttp.IsRedirect(HttpStatusCode.OK)
            && !GitHubHttp.IsRedirect(HttpStatusCode.NotModified)
            && !GitHubHttp.IsRedirect(HttpStatusCode.Forbidden);
        check("ui-net-redirect-status", redirects && quiet, $"redirects={redirects} else={quiet}");
    }

    /// <summary>
    /// 重定向策略：允许的那一跳真的跟过去；
    /// 相对 Location 按当前地址解析；换主机、降级成 http、没有 Location 三种都必须拒。
    /// </summary>
    private static void RedirectPolicy(UpdateChecks.Report check)
    {
        var from = new Uri("https://github.com/dhhxfggg2023/PetDock/releases/download/v1/x.zip");
        var asset = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/x.zip");

        bool followed = GitHubHttp.TryRedirect(from, asset, out Uri? next, out string? whyFollow)
            && next is not null
            && next.Host == "release-assets.githubusercontent.com"
            && whyFollow is null;

        bool relative = GitHubHttp.TryRedirect(from, new Uri("/other/x.zip", UriKind.Relative), out Uri? rel, out _)
            && rel is not null
            && rel.AbsoluteUri == "https://github.com/other/x.zip";

        bool foreign = !GitHubHttp.TryRedirect(from, new Uri("https://evil.example.com/x.zip"), out _, out string? whyForeign)
            && whyForeign == "redirect-host-not-allowed-evil.example.com";

        bool insecure = !GitHubHttp.TryRedirect(from, new Uri("http://release-assets.githubusercontent.com/x.zip"), out _, out string? whyInsecure)
            && whyInsecure == "redirect-not-https-release-assets.githubusercontent.com";

        bool noLocation = !GitHubHttp.TryRedirect(from, null, out _, out string? whyNone)
            && whyNone == "redirect-no-location-github.com";

        check("ui-net-redirect-policy", followed && relative && foreign && insecure && noLocation,
            $"followed={followed} relative={relative} foreign={foreign} insecure={insecure} no-location={noLocation}");
    }

    /// <summary>
    /// 连不上的原因串必须点名主机：这是「可诊断性」那一族判据最小的那一条 ——
    /// 用户手里的失败信息是「连不上 evil.example.com」还是「网络不通」，决定他能不能自己修。
    /// </summary>
    private static void FailureNamesHost(UpdateChecks.Report check)
    {
        string unreachable = GitHubHttp.FailureReason("evil.example.com", new System.Net.Http.HttpRequestException("boom"));
        string timeout = GitHubHttp.FailureReason("evil.example.com", new System.Threading.Tasks.TaskCanceledException());
        bool named = unreachable == "evil.example.com-unreachable-HttpRequestException"
            && timeout == "evil.example.com-timeout";
        check("ui-net-failure-names-host", named, $"unreachable={unreachable} timeout={timeout}");
    }

    /// <summary>仓库文件的地址形状与两个出口的接线：写死、只走 https、没有第二段协议前缀。</summary>
    private static void ContentsAddress(UpdateChecks.Report check)
    {
        string feed = GitHubHttp.ContentsUrl("update.json");
        bool shape = feed == "https://api.github.com/repos/dhhxfggg2023/PetDock/contents/update.json?ref=main"
            && feed.IndexOf("://", 6, StringComparison.Ordinal) < 0;
        bool wired = UpdateService.Endpoint == GitHubHttp.ContentsUrl(UpdateService.FeedPath)
            && PluginMarket.Endpoint == GitHubHttp.ContentsUrl(PluginMarket.IndexPath);
        check("ui-net-contents-address", shape && wired, $"shape={shape} wired={wired} feed={feed}");
    }
}
