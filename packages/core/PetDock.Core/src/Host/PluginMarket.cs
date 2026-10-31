// 插件市场（2026-10-30 用户要求：其他人下载后能在面板里装到所有者上传的插件）。
//
// 边界先说清楚 —— 这是**程序里唯一一处用户主动触发的联网之一**（另一处是检查更新），
// 两条出口共用同一个通道 GitHubHttp：
//
//   1. 只连一组写死的主机（见 GitHubHttp.AllowedHosts），不接受配置、不接受页面传入的地址；
//      判据 ui-market-fixed-endpoint 钉住索引地址，改地址必须改代码并过门禁。
//   2. 只走 HTTPS；重定向自己跟，每一跳的目标主机都要在允许表里。
//   3. 每个条目都带 sha256；下载完**先校验再交给安装器**，对不上就丢弃。
//      索引本身也被逐字段校验（见 PluginMarketIndex），形状不对整份拒绝。
//   4. 用户没点「浏览插件市场」就不联网：本类不持有定时器、不在启动路径上被调用。
//
// 为什么不做「任意 URL 安装」：那等于把程序变成下载器，用户被骗一次就装了别人的东西。
// 市场是**所有者上传的白名单**，不是通用安装通道。
//
// 为什么索引走 contents API 而不是 raw.githubusercontent.com（2026-10-31）：见 GitHubHttp 文件头。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PetDock.Hosting;

/// <summary>市场里的一个插件条目（来自索引文件，不是本地清单）。</summary>
internal sealed record MarketEntry(
    string Id,
    string Name,
    string Version,
    string Summary,
    string DownloadUrl,
    string Sha256,
    long Bytes,
    IReadOnlyList<string> Capabilities);

/// <summary>
/// 拉索引 + 下插件包。<see cref="FetchIndexAsync"/> 与 <see cref="DownloadAsync"/> 是仅有的两个出网动作；
/// 两者都只在用户点了「浏览插件市场」或「安装」之后才被调用。
/// </summary>
internal sealed class PluginMarket
{
    /// <summary>索引在仓库里的路径（写死）。</summary>
    internal const string IndexPath = "plugins/index.json";

    /// <summary>唯一允许的索引地址。写死：判据 ui-market-fixed-endpoint 会读这一行的值。</summary>
    internal static readonly string Endpoint = GitHubHttp.ContentsUrl(IndexPath);

    /// <summary>索引里所有下载地址都必须在这个前缀下。</summary>
    internal const string AllowedDownloadPrefix =
        "https://github.com/dhhxfggg2023/PetDock/releases/download/";

    private const int MaxIndexBytes = 256 * 1024;

    /// <summary>拉索引。失败返回原因串（点名主机与状态码），与「市场是空的」分开报。</summary>
    public async Task<(PluginMarketIndex? Index, string? Error)> FetchIndexAsync(CancellationToken token)
    {
        (string? text, string? error) = await GitHubHttp
            .FetchRepoFileAsync(IndexPath, MaxIndexBytes, token)
            .ConfigureAwait(false);
        if (text is null)
        {
            return (null, error);
        }

        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(text);
            return PluginMarketIndex.TryParse(doc.RootElement, out PluginMarketIndex? index, out string? why)
                ? (index, null)
                : (null, why);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, "index-not-json");
        }
    }

    /// <summary>
    /// 把一条插件下到临时文件。**校验 sha256 通过才返回路径**；对不上就删掉并返回 null。
    /// 这里不做解压：解压与清单校验还是走 <see cref="PluginInstaller.TryPrepare"/> 那条唯一路径。
    /// </summary>
    public async Task<(string? File, string? Error)> DownloadAsync(
        MarketEntry entry,
        string directory,
        CancellationToken token)
    {
        if (!entry.DownloadUrl.StartsWith(AllowedDownloadPrefix, StringComparison.Ordinal))
        {
            // 索引里出现别的地址 = 索引被改过。拒绝，不改写、不猜。
            return (null, "bad-download-url");
        }

        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, entry.Id.Replace('/', '_') + "-" + entry.Version + ".zip");
        try
        {
            (System.Net.Http.HttpResponseMessage? response, string? laneError) = await GitHubHttp
                .GetAsync(entry.DownloadUrl, token)
                .ConfigureAwait(false);
            if (response is null)
            {
                SafeDelete(target);
                return (null, laneError);
            }

            bool copied;
            string? copyError;
            using (response)
            {
                if ((int)response.StatusCode != 200)
                {
                    return (null, GitHubHttp.StatusReason(response.RequestMessage?.RequestUri?.Host ?? "github.com", response));
                }

                // 下载也要有上限：索引里的 bytes 只够界面显示，不能当护栏（坏掉的响应会填满磁盘）。
                await using FileStream file = File.Create(target);
                (copied, copyError) = await GitHubHttp
                    .CopyBoundedAsync(response.Content, file, PluginMarketIndex.MaxEntryBytes, token)
                    .ConfigureAwait(false);
            }

            if (!copied)
            {
                SafeDelete(target);
                return (null, copyError);
            }

            PluginMarketIndex.VerifySha256(target, entry.Sha256, out string? mismatch);
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
        catch (IOException error)
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
            // 删不掉就留着 —— 它在 _market 暂存区里，下次安装会覆盖。
        }
    }
}
