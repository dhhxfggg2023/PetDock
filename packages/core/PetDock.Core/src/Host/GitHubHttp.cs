// 与 GitHub 说话的**唯一一条**通道（2026-10-31）。
//
// 为什么非有不可 —— 一次真实的致命缺陷：
//   更新源与插件市场各自 new 了一个 HttpClient，并且都写着 AllowAutoRedirect = false，
//   理由是「只连一个写死的域名」。可 GitHub 的 Release 资产**必然**先回一次 302，
//   把请求指到 release-assets.githubusercontent.com（本机实测：zip 与它的 .sha256 都是 302）。
//   于是真实网络上「下载新版本」「从市场装插件」永远只会拿到 download-status-302 ——
//   功能整块不可用，而八道静态门与 282 条 UI 判据全绿：判据只解析 JSON、
//   打包自检自造 zip、联网那一步用的是它自己的、跟随重定向的下载器。产品代码从没被真实流量碰过。
//
// 纪律（把「只连一个域名」升级成「只连一组写死的主机」）：
//   1. 只走 https；重定向自己跟、最多三跳，**每一跳的目标主机**都要在 AllowedHosts 里；
//      校验在发请求之前，所以被投毒的 Location 连一次请求都发不出去；
//   2. 主机名写死，不接受配置，也不接受索引/页面传进来的地址；
//   3. 仓库里的文件走 contents API（api.github.com）——不再走 raw.githubusercontent.com：
//      后者在所有者这台机器上被 hosts 指到 127.0.0.1，表现为 30 秒超时。
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PetDock.Hosting;

internal static class GitHubHttp
{
    internal const string ApiHost = "api.github.com";
    internal const string MainHost = "github.com";
    internal const string AssetHost = "release-assets.githubusercontent.com";
    internal const string BlobHost = "objects.githubusercontent.com";

    /// <summary>本仓库（所有者+仓库名）。两个出口共用这一份，改这里就是改唯一的来源。</summary>
    internal const string Repository = "dhhxfggg2023/PetDock";

    /// <summary>读仓库文件用的分支。写死：换分支要过门禁，不能让远端决定读哪一份。</summary>
    internal const string Branch = "main";

    private const int MaxRedirects = 3;

    /// <summary>
    /// 单跳「等到响应头」的时限。被 hosts / 本地加速器劫持的主机是**接了 TCP 不回话**，
    /// 不是连不上：默认要等满整体超时才给结论，用户点两次就是一分多钟的空白。
    /// </summary>
    private static readonly TimeSpan HopTimeout = TimeSpan.FromSeconds(12);

    /// <summary>下载体每读一块的闲置时限：卡住的连接不该让「正在下载…」永远转下去。</summary>
    private static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Client = CreateClient();

    internal static HttpClient CreateClient()
    {
        // 用 SocketsHttpHandler 而不是 HttpClientHandler：只有它能在连接阶段单独限时
        // （默认要等满整体超时才报连不上）。它本来就是 .NET Core 上的默认实现。
        var handler = new SocketsHttpHandler
        {
            // 关掉自动跟随：跟之前我要先看一眼目标主机（见 TryRedirect）。
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PetDock", "0.3"));
        return client;
    }

    /// <summary>允许说话的主机：只有这几个，其它一律拒绝（含 raw.githubusercontent.com）。</summary>
    internal static bool IsAllowedHost(string host) =>
        host is ApiHost or MainHost or AssetHost or BlobHost;

    internal static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    /// <summary>仓库文件的 contents API 地址（返回 base64 包的 JSON）。</summary>
    internal static string ContentsUrl(string path) =>
        $"https://{ApiHost}/repos/{Repository}/contents/{path}?ref={Branch}";

    /// <summary>
    /// 判一跳重定向：目标必须 https 且在 AllowedHosts 里；相对 Location 按当前地址解析。
    /// 单独抽出来是为了能被判据直接喂样例 —— 「跟随重定向」这件事不联网也要判得动。
    /// </summary>
    internal static bool TryRedirect(Uri current, Uri? location, out Uri? next, out string? error)
    {
        next = null;
        error = null;
        if (location is null)
        {
            error = $"redirect-no-location-{current.Host}";
            return false;
        }

        Uri target = location.IsAbsoluteUri ? location : new Uri(current, location);
        if (target.Scheme != Uri.UriSchemeHttps)
        {
            error = $"redirect-not-https-{target.Host}";
            return false;
        }

        if (!IsAllowedHost(target.Host))
        {
            error = $"redirect-host-not-allowed-{target.Host}";
            return false;
        }

        next = target;
        return true;
    }

    /// <summary>发一次 GET 并跟随重定向（每跳都先校验）。返回的响应由调用方释放。</summary>
    internal static async Task<(HttpResponseMessage? Response, string? Error)> GetAsync(
        string url,
        CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed is null || parsed.Scheme != Uri.UriSchemeHttps)
        {
            return (null, "url-not-https");
        }

        if (!IsAllowedHost(parsed.Host))
        {
            return (null, $"host-not-allowed-{parsed.Host}");
        }

        Uri cursor = parsed;

        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            HttpResponseMessage response;
            using var hopTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            hopTimeout.CancelAfter(HopTimeout);
            try
            {
                response = await Client
                    .SendAsync(
                        new HttpRequestMessage(HttpMethod.Get, cursor),
                        HttpCompletionOption.ResponseHeadersRead,
                        hopTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                // 点名主机与**哪一种**不通：用户不用去猜「网络不通」是哪一个域名、
                // 也不必自己发现那个域名被 hosts 指到了 127.0.0.1。
                return (null, FailureReason(cursor.Host, error));
            }

            if (!IsRedirect(response.StatusCode))
            {
                return (response, null);
            }

            if (!TryRedirect(cursor, response.Headers.Location, out Uri? next, out string? why) || next is null)
            {
                response.Dispose();
                return (null, why);
            }

            response.Dispose();
            cursor = next;
        }

        return (null, $"too-many-redirects-{cursor.Host}");
    }

    /// <summary>
    /// 有上限的搬运：先看 Content-Length，再按块读、一超限立刻停手。
    /// 为什么不是「先整份读进内存再判体积」：那在真正坏掉/恶意的响应上等于没有上限 ——
    /// 判据判的是读完之后的大小，而磁盘与内存那时已经被填满了。
    /// </summary>
    internal static async Task<(bool Ok, string? Error)> CopyBoundedAsync(
        HttpContent content,
        Stream destination,
        long maxBytes,
        CancellationToken token)
    {
        long? declared = content.Headers.ContentLength;
        if (declared is not null && declared > maxBytes)
        {
            return (false, $"size-too-large-{declared}");
        }

        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        await using Stream source = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        while (true)
        {
            // 每一块单独限时：连接卡住时不会让「正在下载」永远转下去。
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(ReadIdleTimeout);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return (false, "read-timeout");
            }

            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                return (false, $"size-too-large-{total}");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }

        return (true, null);
    }

    /// <summary>把一份响应读进内存，同样带上限（仓库文件只有几十 KB，够用且不失控）。</summary>
    internal static async Task<(byte[]? Bytes, string? Error)> ReadBoundedAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken token)
    {
        using var memory = new MemoryStream();
        (bool ok, string? error) = await CopyBoundedAsync(content, memory, maxBytes, token).ConfigureAwait(false);
        return ok ? (memory.ToArray(), null) : (null, error);
    }

    /// <summary>
    /// 连不上的原因串：点名主机，并区分「被指到本机」「没回话」「连不上」三种 ——
    /// 这三种的下一步动作完全不同（改 hosts / 等一会儿 / 查网络）。
    /// </summary>
    internal static string FailureReason(string host, Exception error)
    {
        if (ResolvesToLoopback(host))
        {
            return $"{host}-loopback";
        }

        return error is OperationCanceledException or TaskCanceledException
            ? $"{host}-timeout"
            : $"{host}-unreachable-{error.GetType().Name}";
    }

    /// <summary>这个主机名是不是被解析到了本机（hosts / 本地代理的典型手法）。</summary>
    private static bool ResolvesToLoopback(string host)
    {
        try
        {
            foreach (IPAddress address in Dns.GetHostAddresses(host))
            {
                if (IPAddress.IsLoopback(address))
                {
                    return true;
                }
            }
        }
        catch (SocketException)
        {
            // 解析都失败：交给上面按异常类型分类。
        }

        return false;
    }

    /// <summary>非 200 时的原因串：点名主机与状态码，日志与界面都能照着查。</summary>
    internal static string StatusReason(string host, HttpResponseMessage response) =>
        $"{host}-status-{(int)response.StatusCode}";

    /// <summary>
    /// 读仓库里的一个**文本**文件（contents API 外面包了一层 base64）。
    /// 更新源 update.json 与市场索引 plugins/index.json 共用这一条：
    /// 同一个机制只该有一个属主，两处各写一份就是两份会各自漂移的实现。
    /// </summary>
    internal static async Task<(string? Text, string? Error)> FetchRepoFileAsync(
        string path,
        int maxBytes,
        CancellationToken token)
    {
        string url = ContentsUrl(path);
        (HttpResponseMessage? response, string? error) = await GetAsync(url, token).ConfigureAwait(false);
        if (response is null)
        {
            return (null, error);
        }

        using (response)
        {
            if ((int)response.StatusCode != 200)
            {
                return (null, StatusReason(ApiHost, response));
            }

            // base64 会把体积撑成 4/3：编码后的上限留足余量，解码后再按真实上限判一次。
            (byte[]? body, string? tooLarge) = await ReadBoundedAsync(response.Content, (maxBytes * 2L) + 1024, token)
                .ConfigureAwait(false);
            if (body is null)
            {
                return (null, tooLarge);
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("content", out JsonElement content)
                    || content.ValueKind != JsonValueKind.String)
                {
                    return (null, "contents-missing-content");
                }

                string compact = string.Concat(content.GetString()!.Where(c => !char.IsWhiteSpace(c)));
                byte[] decoded = Convert.FromBase64String(compact);
                if (decoded.Length > maxBytes)
                {
                    return (null, $"{ApiHost}-too-large-{decoded.Length}");
                }

                return (Encoding.UTF8.GetString(decoded), null);
            }
            catch (Exception error2) when (error2 is JsonException or FormatException)
            {
                return (null, $"contents-not-base64-{error2.GetType().Name}");
            }
        }
    }
}
