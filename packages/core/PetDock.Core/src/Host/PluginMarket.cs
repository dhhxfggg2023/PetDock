// 插件市场（2026-10-30 用户要求：其他人下载后能在面板里装到所有者上传的插件）。
//
// 边界先说清楚 —— 这是**程序里唯一一处主动联网**：
//
//   1. 只连一个写死的域名（见 Endpoint），不接受配置、不接受插件传入的地址。
//      判据 ui-market-fixed-endpoint 钉住这一点：改地址必须改代码并过门禁。
//   2. 只走 HTTPS，不跟随重定向到别的域（HttpClient 默认跟随，这里显式关掉再自己判）。
//   3. 每个条目都带 sha256；下载完**先校验再交给安装器**，对不上就丢弃。
//      索引本身也被签名摘要覆盖（见 PluginMarketIndex），改一个字节就整份拒绝。
//   4. 用户没点「市场」就不联网：本类不持有定时器、不在启动路径上被调用。
//
// 为什么不做「任意 URL 安装」：那等于把程序变成下载器，用户被骗一次就装了别人的东西。
// 市场是**所有者上传的白名单**，不是通用安装通道。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
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
/// 两者都只在用户点「浏览插件市场」之后才被调用。
/// </summary>
internal sealed class PluginMarket
{
    /// <summary>唯一允许的索引地址。写死：判据 ui-market-fixed-endpoint 会读这一行的值。</summary>
    internal const string Endpoint =
        "https://raw.githubusercontent.com/dhhxfggg2023/PetDock/main/plugins/index.json";

    /// <summary>索引里所有下载地址都必须在这个前缀下。</summary>
    internal const string AllowedDownloadPrefix =
        "https://github.com/dhhxfggg2023/PetDock/releases/download/";

    private const int MaxIndexBytes = 256 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            // 关掉自动跟随：重定向到别的域会让上面的「只连一个域名」失效。
            AllowAutoRedirect = false,
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PetDock", "0.1"));
        return client;
    }

    /// <summary>拉索引。失败返回 null 并给出原因（调用方翻成界面文案）。</summary>
    public async Task<(PluginMarketIndex? Index, string? Error)> FetchIndexAsync(CancellationToken token)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(Endpoint, token).ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
            {
                return (null, $"index-status-{(int)response.StatusCode}");
            }

            byte[] body = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            if (body.Length > MaxIndexBytes)
            {
                return (null, "index-too-large");
            }

            // 索引是 JSON；解析只认这个文件自己声明的形状，多余字段直接忽略。
            JsonDocument doc = JsonDocument.Parse(body);
            if (!PluginMarketIndex.TryParse(doc.RootElement, out PluginMarketIndex? index, out string? why))
            {
                return (null, why);
            }

            return (index, null);
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
            using HttpResponseMessage response = await Http.GetAsync(entry.DownloadUrl, token).ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
            {
                return (null, $"download-status-{(int)response.StatusCode}");
            }

            await using (FileStream file = File.Create(target))
            {
                await response.Content.CopyToAsync(file, token).ConfigureAwait(false);
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
            // 删不掉就留着 —— 它在 _market 暂存区里，下次安装会覆盖。
        }
    }
}
