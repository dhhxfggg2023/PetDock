// 程序自身更新的宿主侧接线（2026-10-31）。
//
// 与 UiHost.Market.cs 同一形态：页面只发意图，判定与落盘在这一侧；联网只在用户点的那一下发生。
//
// 四个动作：
//   查（拉 update.json + 比版本）
//   下（下载 + 校验 sha256 + **顺手解压到暂存树**）
//   装（两种时机二选一：立即重启装，或稍后 —— 退出程序，下次启动装）
//   开（把暂存目录递给资源管理器）
//
// 为什么下载时就解压：解压失败要趁用户还看着面板时告诉他，而不是等到交接那一刻
// （那时本进程正要退出，没有地方显示错误）。交接因此只剩「拷文件 + 起进程」。
//
// 为什么不在这里「边运行边替换自己」：见 UpdateStartup 文件头 —— Windows 不允许覆盖
// 自己加载中的映像。本类只准备、只交接，真正的搬运在 PetDock.Update.exe 里。
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

    /// <summary>下载 + 校验通过、已经解开、等人点「重启更新 / 稍后更新」的那一份。</summary>
    private UpdatePending? _staged;

    private UpdateService Updates => _updates ??= new UpdateService();

    /// <summary>暂存在数据区下的 <c>_update/</c>（与插件根、市场暂存都分开）。</summary>
    private string? UpdateStagingDir =>
        _store is null ? null : UpdateStage.Dir(_store.Root);

    Task<UpdateCheckLine> IUiPageContext.CheckUpdateAsync() => CheckUpdateAsync();

    Task<UpdateInstallLine> IUiPageContext.DownloadUpdateAsync() => DownloadUpdateAsync();

    Task<UpdateInstallLine> IUiPageContext.InstallUpdateAsync(bool restartNow) =>
        InstallUpdateAsync(restartNow);

    string? IUiPageContext.OpenUpdateFolder() => OpenUpdateFolder();

    /// <summary>有没有一份「已经下载好、等着装」的版本（设置页据此决定显示哪些按钮）。</summary>
    internal bool UpdateStaged => _staged is not null;

    /// <summary>磁盘上等着装的那一份的版本号（面板重开也要看得见，所以读盘不读字段）。</summary>
    string? IUiPageContext.PendingUpdateVersion => _store is null
        ? null
        : UpdateStage.ReadPending(_store.Root)?.Version;

    string? IUiPageContext.LastUpdateReport => _updateReport;

    /// <summary>这次的启动结论（Program 在装配时告诉宿主：上一次装成功了没有）。</summary>
    private string? _updateReport;

    /// <summary>Program 用：把「上一次启动装没装上」交给面板显示（null = 没有可说的）。</summary>
    internal void SetUpdateReport(string? report) => _updateReport = report;

    /// <summary>登记好/装好之后把结果推给面板（页面在 Changed 里重读 Snapshot，不缓存旧值）。</summary>
    private void PublishUpdateReport(string? report)
    {
        _updateReport = report;
        RefreshUi();
    }



    internal string? UpdateReport => _updateReport;

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

    /// <summary>
    /// 下载 + 校验 + 解压到暂存树。**不替换正在运行的程序**（那是下一次启动或重启时的事）。
    /// </summary>
    internal async Task<UpdateInstallLine> DownloadUpdateAsync()
    {
        UpdateFeed? feed = _pendingUpdate;
        if (feed is null)
        {
            return UpdateInstallLine.Fail(I18n.Get("cp.settings.update.notChecked"));
        }

        string? staging = UpdateStagingDir;
        string dataRoot = _store?.Root ?? string.Empty;
        if (staging is null || dataRoot.Length == 0)
        {
            return UpdateInstallLine.Fail(I18n.Get("cp.settings.update.unavailable"));
        }

        try
        {
            (string? file, string? sha256, string? error) = await Updates
                .DownloadAsync(feed, staging, CancellationToken.None)
                .ConfigureAwait(true);
            if (file is null)
            {
                KernelLog.Write($"update-download-failed version={feed.Version} why={error}");
                return UpdateInstallLine.Fail(I18n.Get("cp.settings.update.downloadFailed", error ?? "?"));
            }

            long bytes = new FileInfo(file).Length;
            KernelLog.Write($"update-downloaded version={feed.Version} bytes={bytes} path={file}");

            // 解压在这里做（不是等到交接）：失败时用户还看着面板，可以立刻重试。
            if (!UpdateStage.Extract(file, UpdateStage.TreeDir(dataRoot), out int files, out string? unzipWhy))
            {
                KernelLog.Write($"update-unzip-failed version={feed.Version} why={unzipWhy}");
                return UpdateInstallLine.Fail(I18n.Get("cp.settings.update.unzipFailed", unzipWhy ?? "?"));
            }

            var pending = new UpdatePending(feed.Version, file, sha256 ?? string.Empty);
            if (!UpdateStage.WritePending(dataRoot, pending, out string? writeWhy))
            {
                KernelLog.Write($"update-pending-write-failed version={feed.Version} why={writeWhy}");
                return UpdateInstallLine.Fail(I18n.Get("cp.settings.update.failed"));
            }

            _staged = pending;
            KernelLog.Write($"update-staged version={feed.Version} files={files} bytes={bytes}");
            return UpdateInstallLine.Staged(new UpdateCheckLine(AppVersion, feed.Version, bytes, feed.Notes, null));
        }
        catch (Exception error)
        {
            KernelLog.Write($"update-download-crashed {error.GetType().Name}: {error.Message}");
            return UpdateInstallLine.Fail(I18n.Get("cp.settings.update.failed"));
        }
    }

    /// <summary>
    /// 装。两种时机，走的是**同一条**交接（<see cref="UpdateStartup.TryHandOff"/>）：
    ///   restartNow = true  写完标记就把本进程关掉，搬运工接手，装完把程序开回来；
    ///   restartNow = false 只留标记 —— 用户下次自己点开程序时，启动路径看到它就装。
    ///
    /// 返回 null = 交接已经发生（调用方要把进程关掉）；非 null = 给用户看的原因，本次不更新。
    /// </summary>
    internal async Task<UpdateInstallLine> InstallUpdateAsync(bool restartNow)
    {
        string dataRoot = _store?.Root ?? string.Empty;
        if (dataRoot.Length == 0)
        {
            return UpdateInstallLine.Fail(I18n.Get("cp.settings.update.unavailable"));
        }

        UpdatePending? pending = _staged ?? UpdateStage.ReadPending(dataRoot);
        if (pending is null)
        {
            return UpdateInstallLine.Fail(I18n.Get("cp.settings.update.notChecked"));
        }

        if (!restartNow)
        {
            // 稍后更新：什么都不搬，只确认「下次启动会装」这一件事真的落盘了。
            PublishUpdateReport(I18n.Get("cp.settings.update.laterQueued", pending.Version));
            KernelLog.Write($"update-later version={pending.Version} pending={UpdateStage.PendingPath(dataRoot)}");
            return UpdateInstallLine.Restart(new UpdateCheckLine(AppVersion, pending.Version, 0, string.Empty, null));
        }

        var plan = new UpdateStartPlan(UpdateStartStep.HandOff, pending, "manual-restart");
        string installRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (!UpdateStartup.TryHandOff(plan, dataRoot, installRoot))
        {
            return UpdateInstallLine.Fail(I18n.Get("cp.settings.update.handoffFailed"));
        }

        await Task.Yield();

        // 交接成功：关掉消息循环。搬运工在等本进程退出，退出之后它才动手。
        KernelLog.Write($"update-restart-now version={pending.Version} install={installRoot}");
        var line = new UpdateCheckLine(AppVersion, pending.Version, 0, string.Empty, null);

        // 先让这一帧画出去（按钮变成「正在重启…」），再关消息循环：
        // 用户点完立刻消失，会以为程序崩了。
        await Task.Delay(150).ConfigureAwait(true);
        ExitThread();
        return UpdateInstallLine.Restart(line);
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
