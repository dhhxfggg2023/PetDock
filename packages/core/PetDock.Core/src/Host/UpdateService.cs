// 程序自身更新的**联网**那一半（2026-10-31）。
//
// 边界与 PluginMarket 同一条纪律（那是本程序另一处主动联网，两者是两个仅有的出口）：
//   1. 地址写死，不接受配置、不接受页面传入 —— 判据 ui-update-fixed-endpoint 钉住这一行；
//   2. 只走 https，不跟随重定向到别的域（重定向会让「只连一个域名」失效）；
//   3. 安装包带 sha256，落盘后**先校验再交给安装器**，对不上就删；
//   4. 用户不点「检查更新」就不联网：本类不持有定时器，也不在启动路径上被调用。
//
// 为什么这里**不**直接替换正在运行的 exe：
//   在 Windows 上覆盖自己的映像会失败（文件被占用），要替换得先起一个外部脚本、退出进程、
//   等它搬文件再重启 —— 一旦中途出错，用户面对的是一台「桌宠没了、原来的 exe 也不见了」的机器。
//   所以这一版只做到「下载 + 校验 + 解到暂存目录」，然后**把新版本原样摆在那里**，
//   由用户点「打开所在文件夹」自己去用新版本。少一步自动重启，换掉的是「更新把它自己弄坏了」
//   这一整类故障。这一步做不做、什么时候做，是所有者的事，不是我在没人要求时替他决定的。
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PetDock.Hosting;

/// <summary>更新源的唯一出处，以及下载/校验两个动作。</summary>
internal sealed class UpdateService
{
    /// <summary>唯一允许的更新源。写死：判据 ui-update-fixed-endpoint 会读这一行的值。</summary>
    internal const string Endpoint =
        "https://raw.githubusercontent.com/dhhxfggg2023/PetDock/main/update.json";

    /// <summary>安装包地址必须在这个前缀下（本仓库的 Releases）。</summary>
    internal const string AllowedDownloadPrefix =
        "https://github.com/dhhxfggg2023/PetDock/releases/download/";

    private const int MaxFeedBytes = 16 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PetDock", "0.2"));
        return client;
    }

    /// <summary>拉更新源。失败返回原因短码（调用方翻成人话）；「没有新版本」不是失败。</summary>
    public async Task<(UpdateFeed? Feed, string? Error)> FetchAsync(CancellationToken token)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(Endpoint, token).ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
            {
                return (null, $"feed-status-{(int)response.StatusCode}");
            }

            byte[] body = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            if (body.Length > MaxFeedBytes)
            {
                return (null, "feed-too-large");
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            return UpdateFeed.TryParse(doc.RootElement, out UpdateFeed? feed, out string? why)
                ? (feed, null)
                : (null, why);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, error.GetType().Name);
        }
    }

    /// <summary>
    /// 下载安装包到 <paramref name="directory"/>。**sha256 对上才返回路径**；
    /// 对不上、体积不符、地址前缀不对都删文件并返回 null。
    /// </summary>
    public async Task<(string? File, string? Error)> DownloadAsync(
        UpdateFeed feed,
        string directory,
        CancellationToken token)
    {
        if (!feed.Url.StartsWith(AllowedDownloadPrefix, StringComparison.Ordinal))
        {
            return (null, "bad-download-url");
        }

        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "PetDock-" + feed.Version + "-win-x64.zip");
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(feed.Url, token).ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
            {
                return (null, $"download-status-{(int)response.StatusCode}");
            }

            await using (FileStream file = File.Create(target))
            {
                await response.Content.CopyToAsync(file, token).ConfigureAwait(false);
            }

            long actual = new FileInfo(target).Length;
            if (actual != feed.Bytes)
            {
                File.Delete(target);
                return (null, $"size-expected-{feed.Bytes}-actual-{actual}");
            }

            PluginMarketIndex.VerifySha256(target, feed.Sha256, out string? mismatch);
            if (mismatch is not null)
            {
                File.Delete(target);
                return (null, mismatch);
            }

            return (target, null);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(target);
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            SafeDelete(target);
            return (null, error.GetType().Name);
        }
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
