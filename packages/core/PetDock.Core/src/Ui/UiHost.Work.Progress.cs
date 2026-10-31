// 长任务的进度上报（2026-10-29）：复制字节与第三方转换帧共用同一套节流与 UI 投递。
// 为什么单开一个文件：UiHost.Work.cs 加上转换进度后越过 D-03c 的 400 行上限；
// 职责上也独立 —— 这里只管「后台线程怎么把进度写到面板上」。
using System;
using System.Globalization;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>复制进度（后台线程调用）：节流到约 10 Hz，切成确定态进度条；最后一字节必报。</summary>
    private void ReportCopyProgress(long copied, long total)
    {
        if (!ShouldReportProgress(copied >= total))
        {
            return;
        }

        double ratio = total <= 0 ? 1.0 : Math.Clamp(copied / (double)total, 0.0, 1.0);
        string text = I18n.Get(
            "ui.work.copyProgress",
            (copied / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture),
            (total / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture));
        ReportWorkProgress(ratio, text);
    }

    /// <summary>第三方转换进度（后台线程调用）：按帧计，节流与复制共用同一个时钟。</summary>
    private void ReportConvertProgress(int done, int total)
    {
        if (!ShouldReportProgress(done >= total))
        {
            return;
        }

        double ratio = total <= 0 ? 1.0 : Math.Clamp(done / (double)total, 0.0, 1.0);
        ReportWorkProgress(ratio, I18n.Get("ui.work.convertProgress", done, total));
    }

    /// <summary>进度节流：最后一帧/字节必报，其余最多 10 Hz。</summary>
    private bool ShouldReportProgress(bool final)
    {
        long now = _workClock.ElapsedMilliseconds;
        if (!final && now - _lastProgressMs < ProgressThrottleMs)
        {
            return false;
        }

        _lastProgressMs = now;
        return true;
    }

    /// <summary>把确定态进度写进面板（后台线程按控件线程投递）。</summary>
    private void ReportWorkProgress(double ratio, string text)
    {
        ControlPanel? panel = _panel;
        if (panel is null || panel.IsDisposed)
        {
            return;
        }

        if (panel.InvokeRequired)
        {
            panel.BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
            {
                if (!panel.IsDisposed)
                {
                    panel.Workspace.SetWorkProgress(ratio, text);
                }
            }));
            return;
        }

        panel.Workspace.SetWorkProgress(ratio, text);
    }
}
