// 常驻的**重启**（2026-10-31 用户要求：「还有就是加上常驻的重启功能」）。
//
// 为什么不是「自己再开一个自己」：单实例锁在 Main 最前面抢（RFC-32 §3），
// 新进程会在老进程还没退出时抢不到锁，于是它只会弹一句「已经在运行」然后退出 ——
// 用户看到的是「点了重启，什么也没发生」。所以新进程带 <c>--wait-pid</c> 起来：
// 它先等老进程真的没了，再走正常启动。整套动作不需要外部脚本，也不需要第二个可执行文件。
//
// 重启前的确认沿用退出那一句（原规格 18 §1：退出要有一次确认）：
// 重启对用户来说就是「桌宠会消失一下」，没有理由比退出更随便。
using System;
using System.Diagnostics;
using System.IO;
using PetDock.Core.Compositor;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>托盘与右键菜单里的「重启」：起一个新进程（它等本进程退出），然后关掉消息循环。</summary>
    private void RestartApp()
    {
        if (!ConfirmDialog.Ask(I18n.Get("ui.tray.restartConfirm"), I18n.Get("ui.tray.restartConfirmTitle")))
        {
            KernelLog.Write("app-restart-cancelled");
            return;
        }

        if (!TryStartRestart(out string? why))
        {
            KernelLog.Write($"app-restart-failed {why}");
            _panel?.SetStatus(I18n.Get("ui.tray.restartFailed", why ?? "?"));
            return;
        }

        KernelLog.Write($"app-restart pid={Environment.ProcessId}");
        ExitThread();
    }

    /// <summary>
    /// 起一个「等本进程退出之后再启动」的新实例。成功返回 true（调用方负责退出）。
    /// 失败不退出：重启没成功不是关掉桌宠的理由 —— 那会变成「点了重启，桌宠没了」。
    /// </summary>
    private static bool TryStartRestart(out string? why)
    {
        why = null;
        string exe = Path.Combine(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), "PetDock.Core.exe");
        if (!File.Exists(exe))
        {
            why = "no-exe";
            return false;
        }

        string pid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                // --open-panel：重启之后把面板打开。用户点的是托盘菜单，不该被丢回一个看不见的托盘。
                Arguments = $"--wait-pid {pid} --open-panel",
                WorkingDirectory = AppContext.BaseDirectory,
            });
            return true;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception
            or InvalidOperationException or UnauthorizedAccessException)
        {
            why = error.GetType().Name;
            return false;
        }
    }
}
