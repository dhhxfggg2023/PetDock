// 程序自身更新的**联网**那一半（2026-10-31）。
//
// 边界与 PluginMarket 同一条纪律（那是本程序另一处主动联网，两者是两个仅有的出口），
// 但**通道只有一个**：所有出网都走 GitHubHttp —— 地址写死、只走 https、重定向自己跟且每跳校验主机。
//
// 为什么更新源走 contents API 而不是 raw.githubusercontent.com（2026-10-31 的第二处修正）：
//   所有者这台机器上 raw.githubusercontent.com 被 hosts 指到 127.0.0.1（本地加速器接了 TCP 不回话），
//   于是「检查更新」永远 30 秒超时，而 api.github.com / github.com 是通的。地址照样写死、只走 https，
//   换的只是同一台服务上的另一条路径。
//
// 为什么这里**不**直接替换正在运行的 exe：
//   在 Windows 上覆盖自己的映像会失败（文件被占用），要替换得先起一个外部脚本、退出进程、
//   等它搬文件再重启 —— 一旦中途出错，用户面对的是一台「桌宠没了、原来的 exe 也不见了」的机器。
//   所以联网这一半只做到「下载 + 校验 + 解到暂存目录 + 登记待安装」，真正的搬运在
//   PetDock.Update.exe 里，且只有用户点了「立即重启更新」或下次启动时才发生。
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PetDock.Hosting;

/// <summary>更新源的唯一出处，以及下载/校验两个动作。</summary>
internal sealed class UpdateService
{
    /// <summary>更新源在仓库里的路径（写死）。判据 ui-update-fixed-endpoint 会读拼出来的那个地址。</summary>
    internal const string FeedPath = "update.json";

    /// <summary>唯一允许的更新源。写死：改地址必须改代码并过门禁。</summary>
    internal static readonly string Endpoint = GitHubHttp.ContentsUrl(FeedPath);

    /// <summary>安装包地址必须在这个前缀下（本仓库的 Releases）。</summary>
    internal const string AllowedDownloadPrefix =
        "https://github.com/dhhxfggg2023/PetDock/releases/download/";

    private const int MaxFeedBytes = 16 * 1024;

    /// <summary>拉更新源。失败返回原因串（点名主机与状态码，界面再包一层人话）。</summary>
    public async Task<(UpdateFeed? Feed, string? Error)> FetchAsync(CancellationToken token)
    {
        (string? text, string? error) = await GitHubHttp
            .FetchRepoFileAsync(FeedPath, MaxFeedBytes, token)
            .ConfigureAwait(false);
        if (text is null)
        {
            return (null, error);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            return UpdateFeed.TryParse(doc.RootElement, out UpdateFeed? feed, out string? why)
                ? (feed, null)
                : (null, why);
        }
        catch (JsonException)
        {
            return (null, "feed-not-json");
        }
    }

    /// <summary>
    /// 下载安装包到 <paramref name="directory"/>，并与同目录的 <c>.sha256</c> 旁车比对。
    ///
    /// 为什么校验值来自旁边的**.sha256 文件**而不是更新源里的字段：
    /// 更新源是 git 跟踪文件，若它写着 sha256(zip)，提交它就会让 HEAD 前进、
    /// 下一次构建的 zip 里 source_commit 变了、指纹又变了 —— 那是个无解的环（2026-10-31 实测 repro 红）。
    /// 旁车由发布流程随包上传、不进 git，所以它的指纹永远描述当次发布的那个 zip。
    ///
    /// 旁车格式与 <c>Get-FileHash</c>/<c>sha256sum</c> 一致：一行 `<hex>  <文件名>`。
    /// 对不上、或读不到旁车，一律删文件并返回原因 —— 没有校验值的更新包不能装。
    ///
    /// 成功时把**校验通过的那一串**也一并交出去：它是待安装标记里唯一有意义的完整性字段，
    /// 「用户点了稍后更新、下次启动再装」这一整条路上，那之后没有人会再去核对一遍文件。
    /// </summary>
    public async Task<(string? File, string? Sha256, string? Error)> DownloadAsync(
        UpdateFeed feed,
        string directory,
        CancellationToken token)
    {
        if (!feed.Url.StartsWith(AllowedDownloadPrefix, StringComparison.Ordinal))
        {
            return (null, null, "bad-download-url");
        }

        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "PetDock-" + feed.Version + "-win-x64.zip");
        try
        {
            (System.Net.Http.HttpResponseMessage? response, string? laneError) = await GitHubHttp
                .GetAsync(feed.Url, token)
                .ConfigureAwait(false);
            if (response is null)
            {
                return (null, null, laneError);
            }

            bool copied;
            string? copyError;
            using (response)
            {
                if ((int)response.StatusCode != 200)
                {
                    return (null, null, GitHubHttp.StatusReason(response.RequestMessage?.RequestUri?.Host ?? "github.com", response));
                }

                // 上限在搬运过程里就生效（先看 Content-Length、再按块数），不是读完之后才判。
                await using FileStream file = File.Create(target);
                (copied, copyError) = await GitHubHttp
                    .CopyBoundedAsync(response.Content, file, UpdateFeed.MaxBytes, token)
                    .ConfigureAwait(false);
            }

            if (!copied)
            {
                SafeDelete(target);
                return (null, null, copyError);
            }

            long actual = new FileInfo(target).Length;
            if (actual <= 0)
            {
                SafeDelete(target);
                return (null, null, $"size-bad-{actual}");
            }

            (string? expected, string? sidecarError) = await FetchSidecarAsync(feed, token).ConfigureAwait(false);
            if (expected is null)
            {
                File.Delete(target);
                return (null, null, sidecarError ?? "sidecar-missing");
            }

            PluginMarketIndex.VerifySha256(target, expected, out string? mismatch);
            if (mismatch is not null)
            {
                File.Delete(target);
                return (null, null, mismatch);
            }

            return (target, expected, null);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(target);
            throw;
        }
        catch (IOException error)
        {
            SafeDelete(target);
            return (null, null, error.GetType().Name);
        }
    }

    /// <summary>拉旁车并取出其中的 sha256（一行 `<hex>  <文件名>` 的第一段）。</summary>
    private static async Task<(string? Sha256, string? Error)> FetchSidecarAsync(
        UpdateFeed feed,
        CancellationToken token)
    {
        (System.Net.Http.HttpResponseMessage? response, string? laneError) = await GitHubHttp
            .GetAsync(feed.SidecarUrl, token)
            .ConfigureAwait(false);
        if (response is null)
        {
            return (null, laneError);
        }

        using (response)
        {
            if ((int)response.StatusCode != 200)
            {
                return (null, GitHubHttp.StatusReason(response.RequestMessage?.RequestUri?.Host ?? "github.com", response));
            }

            string text = (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim();
            string first = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts
                ? parts[0]
                : string.Empty;
            if (first.Length != 64 || !IsHex(first))
            {
                return (null, "sidecar-bad-shape");
            }

            return (first.ToLowerInvariant(), null);
        }
    }

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

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 删不掉就留着：它在 _update 暂存区里，下次下载会覆盖。
        }
    }
}
