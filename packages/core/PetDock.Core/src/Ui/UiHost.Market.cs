// 插件市场的宿主侧接线（2026-10-30）。
//
// 这一片只做三件事：把索引翻成界面用的行、把「用户在市场上点了安装」接回既有的安装管线、
// 把本地已装的版本填进每一行（好让页面能显示「已装 v0.1.0 / 可更新 v0.2.0」）。
//
// 刻意不做的事：
//   * 不缓存索引到磁盘 —— 用户点了才拉，拉到的只在这一次面板会话里用。
//   * 不在启动路径上调用 —— 构造与常驻循环都不碰 <see cref="PluginMarket"/>。
//   * 不新造安装语义 —— 下载完仍然交给 PluginInstaller.TryPrepare/TryCommit，
//     清单校验、入口越界、体积上限、取消、进度全复用同一条。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    private PluginMarket? _market;

    /// <summary>市场暂存的下载文件放在数据区下的 <c>_market/</c>，与插件根分开。</summary>
    private string? MarketStagingDir =>
        _store is null ? null : Path.Combine(_store.Root, "_market");

    private PluginMarket Market => _market ??= new PluginMarket();

    /// <summary>拉索引。联网只发生在这里；失败与「市场是空的」必须分开报。</summary>
    public async Task<MarketResult> LoadMarketAsync()
    {
        PluginInventory? inventory = _inventory;
        if (inventory is null)
        {
            return MarketResult.Failed("cp.market.unavailable");
        }

        (PluginMarketIndex? index, string? error) = await Market.FetchIndexAsync(CancellationToken.None)
            .ConfigureAwait(true);
        if (index is null)
        {
            KernelLog.Write($"market-index-failed why={error}");
            return MarketResult.Failed(error ?? "cp.market.offline");
        }

        // 本地已装版本：按 id 找，找不到就是 null（页面据此显示「安装」还是「更新」）。
        var installed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (PluginEntry row in inventory.Rows())
        {
            installed[row.Id] = row.Version;
        }

        var lines = new List<MarketEntryLine>(index.Entries.Count);
        foreach (MarketEntry entry in index.Entries)
        {
            installed.TryGetValue(entry.Id, out string? localVersion);
            lines.Add(new MarketEntryLine(
                entry.Id,
                entry.Name,
                entry.Version,
                entry.Summary,
                entry.Bytes,
                entry.Capabilities,
                localVersion));
        }

        KernelLog.Write($"market-index-loaded entries={lines.Count} installed={installed.Count}");
        return new MarketResult(lines, null);
    }

    /// <summary>从市场装一只：下载 → 校验 → 复用 InstallPluginFromPanelAsync 的整条管线。</summary>
    public async Task<UiCommandResult> InstallFromMarketAsync(string pluginId)
    {
        PluginMarket? market = _market;
        string? staging = MarketStagingDir;
        if (staging is null)
        {
            return UiCommandResult.Failure(I18n.Get("cp.market.unavailable"));
        }

        // 重新拉一次索引再下 —— 不接受页面传来的 URL：地址只能来自索引，
        // 而索引只有一处来源（PluginMarket.Endpoint）。
        (PluginMarketIndex? index, string? error) = await Market.FetchIndexAsync(CancellationToken.None)
            .ConfigureAwait(true);
        if (index is null)
        {
            return UiCommandResult.Failure(I18n.Get("cp.market.offline"));
        }

        MarketEntry? entry = null;
        foreach (MarketEntry candidate in index.Entries)
        {
            if (string.Equals(candidate.Id, pluginId, StringComparison.Ordinal))
            {
                entry = candidate;
                break;
            }
        }

        if (entry is null)
        {
            return UiCommandResult.Failure(I18n.Get("cp.market.notInIndex", pluginId));
        }

        (string? file, string? downloadError) = await Market.DownloadAsync(entry, staging, CancellationToken.None)
            .ConfigureAwait(true);
        if (file is null)
        {
            KernelLog.Write($"market-download-failed id={pluginId} why={downloadError}");
            return UiCommandResult.Failure(I18n.Get("cp.market.downloadFailed", downloadError ?? "?"));
        }

        KernelLog.Write($"market-downloaded id={pluginId} version={entry.Version} bytes={entry.Bytes}");
        try
        {
            return await InstallPluginFromPanelAsync(file, enable: true).ConfigureAwait(true);
        }
        finally
        {
            TryDeleteFile(file);
        }
    }

    private static void TryDeleteFile(string path)
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
            // 删不掉就留着：它在 _market 下，下次同名下载会覆盖。
        }
    }
}
