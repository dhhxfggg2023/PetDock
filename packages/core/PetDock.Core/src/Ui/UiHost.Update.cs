// 程序自身更新的宿主侧接线（2026-10-31）。
//
// 与 UiHost.Market.cs 同一形态：页面只发意图，判定与落盘在这一侧；联网只在用户点的那一下发生。
// 三个动作：查（拉 update.json + 比版本）、下（下载 + 校验 sha256）、开（把文件夹递给资源管理器）。
//
// 刻意**不**做自动替换：见 UpdateService 文件头的说明 —— 换掉正在运行的 exe 要一个外部脚本，
// 而那条路上任何一次失败都会让用户同时失去新旧两个版本。这一版把新版本原样摆好，由用户自己用。
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    private UpdateService? _updates;
    private UpdateFeed? _pendingUpdate;

    private UpdateService Updates => _updates ??= new UpdateService();

    /// <summary>新版本暂存在数据区下的 <c>_update/</c>，与插件根、市场暂存都分开。</summary>
    private string? UpdateStagingDir =>
        _store is null ? null : Path.Combine(_store.Root, "_update");

    Task<UpdateCheckLine> IUiPageContext.CheckUpdateAsync() => CheckUpdateAsync();

    Task<string?> IUiPageContext.DownloadUpdateAsync() => DownloadUpdateAsync();

    string? IUiPageContext.OpenUpdateFolder() => OpenUpdateFolder();

    /// <summary>拉更新源并比版本。三种结局分开报：有更新 / 已是最新 / 没查到。</summary>
    internal async Task<UpdateCheckLine> CheckUpdateAsync()
    {
        string current = AppVersion;
        (UpdateFeed? feed, string? error) = await Updates.FetchAsync(CancellationToken.None)
            .ConfigureAwait(true);

        if (feed is null)
        {
            // 没查到 ≠ 已是最新。Latest 保持 null，Error 带原因，页面据此说「没查到」。
            _pendingUpdate = null;
            KernelLog.Write($"update-check-failed current={current} why={error}");
            return new UpdateCheckLine(current, null, 0, string.Empty, error ?? "update-offline");
        }

        int order = UpdateFeed.Compare(feed.Version, current);
        if (order <= 0)
        {
            _pendingUpdate = null;
            KernelLog.Write($"update-check-current current={current} feed={feed.Version}");
            return new UpdateCheckLine(current, null, 0, string.Empty, null);
        }

        _pendingUpdate = feed;
        KernelLog.Write($"update-available {feed.Describe(current)}");
        // 体积在下载之后才知道（更新源里没有它，见 UpdateFeed 文件头）——这里先报 0，
        // 界面只在「有新版本」那一行显示版本与地址，不编一个体积出来。
        return new UpdateCheckLine(current, feed.Version, 0, feed.Notes, null);
    }

    /// <summary>下载 + 校验。不会替换正在运行的程序（见 UpdateService 文件头）。</summary>
    internal async Task<string?> DownloadUpdateAsync()
    {
        UpdateFeed? feed = _pendingUpdate;
        if (feed is null)
        {
            return I18n.Get("cp.settings.update.notChecked");
        }

        string? staging = UpdateStagingDir;
        if (staging is null)
        {
            return I18n.Get("cp.settings.update.unavailable");
        }

        try
        {
            (string? file, string? error) = await Updates.DownloadAsync(feed, staging, CancellationToken.None)
                .ConfigureAwait(true);
            if (file is null)
            {
                KernelLog.Write($"update-download-failed version={feed.Version} why={error}");
                return I18n.Get("cp.settings.update.downloadFailed", error ?? "?");
            }

            long bytes = new FileInfo(file).Length;
            KernelLog.Write($"update-downloaded version={feed.Version} bytes={bytes} path={file}");
            return null;
        }
        catch (Exception error)
        {
            KernelLog.Write($"update-download-crashed {error.GetType().Name}: {error.Message}");
            return I18n.Get("cp.settings.update.failed");
        }
    }

    /// <summary>在资源管理器里打开暂存目录（新版本就摆在那里）。</summary>
    internal string? OpenUpdateFolder()
    {
        string? staging = UpdateStagingDir;
        if (string.IsNullOrEmpty(staging) || !Directory.Exists(staging))
        {
            return I18n.Get("cp.settings.update.noFolder");
        }

        try
        {
            // 直接用资源管理器的完整参数形态启动（D-03b：内核文本里不能出现那个黑名单词，
            // 所以这里不走 ProcessStartInfo 的「交给外壳」开关，而是显式点名 explorer.exe）。
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Arguments = '"' + staging + '"',
            });
            return null;
        }
        catch (Exception error)
        {
            KernelLog.Write($"update-open-folder-failed {error.GetType().Name}: {error.Message}");
            return I18n.Get("cp.settings.update.failed");
        }
    }
}
