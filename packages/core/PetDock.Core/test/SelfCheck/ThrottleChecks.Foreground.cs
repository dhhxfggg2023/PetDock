// RFC-21 §4 的判据体（第四块：前台世界的构造与取证）。
//
// 为什么单独一个文件（D-03c）：世界状态那半边（ThrottleChecks.Windows.cs）只该管「摆出某种窗口」，
// 而「谁来当前台、抢没抢到、抢不到怎么报」是另一件事 —— 它是**每一条依赖前台的判据都得先做的
// 准备工作**，包括不属于节流判据的那些（ResidentLoopChecks 也调它）。
//
// 抢前台必须「释放 ALT + SetForegroundWindow」（申报在 decisions.md）：实测裸
// SetForegroundWindow / AttachThreadInput / SwitchToThisWindow 全都拿不到前台，
// 而前台锁对「用户刚按过 ALT」有豁免。抢不到时调用方必须 FAIL 并打印读数 ——
// 绝不在「前台其实不是我的」状态下断言依赖前台的读数。
using System;
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal static partial class ThrottleChecks
{
    private static Form? _foreground;

    private static void CloseLeftoverWindows() => CloseForeground();

    /// <summary>
    /// 把一个小窗（明显不盖满显示器）推成前台窗 —— 「前台不是全屏」这一态。
    /// 返回值 = 是否真的抢到前台（调用方必须看它，见 <see cref="EnsureNotFullscreenWorld"/>）。
    ///
    /// **复用同一台窗**（2026-10-22 用户反馈「屏幕一直闪烁」）：旧实现每次先关再新建，
    /// 一轮自检要开关几十次前台窗，桌面跟着闪。现在只在没有可用窗时才建。
    /// </summary>
    private static bool SmallForeground()
    {
        Form? form = _foreground;
        if (form is null || form.IsDisposed)
        {
            form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                AutoScaleMode = AutoScaleMode.None,
                Bounds = new Rectangle(80, 80, 320, 240),
            };
            form.Show();
            Application.DoEvents();
            _foreground = form;
        }

        bool acquired = AcquireForeground(form.Handle);
        Application.DoEvents();
        return acquired;
    }

    /// <summary>
    /// 把「前台不是全屏」这一世界状态重建出来，并**当场证出来**（RFC-21 §4：抢不到前台时
    /// 绝不假装成立）。返回 null = 成立；返回字符串 = 可打印的失败原因。
    ///
    /// 为什么每次断言前都要重建：宠物窗是 `WS_EX_NOACTIVATE`，它永远拿不到前台；
    /// 覆盖窗关掉之后前台会**回落到桌面上别的程序**（2026-10-22 实测：回落到一个盖满
    /// 显示器的浏览器窗，于是后面几条判据在「全屏」世界里跑，`tier=fullscreen` 恒成立）。
    /// 那是判据侧的世界错了，不是产品错了。
    /// </summary>
    internal static string? EnsureNotFullscreenWorld()
    {
        bool acquired = SmallForeground();
        IntPtr self = _foreground?.Handle ?? IntPtr.Zero;
        IntPtr actual = Win32Extra.GetForegroundWindow();
        if (acquired && self != IntPtr.Zero && actual == self)
        {
            return null;
        }

        return "foreground-not-acquired acquired=" + Flag(acquired) + " foreground=" + ForegroundDescription();
    }

    /// <summary>把世界状态失败原因拼进判据读数（null ⇒ 空串）。</summary>
    private static string WorldSuffix(string? world) => world is null ? string.Empty : " world=" + world;

    private static void CloseForeground()
    {
        Form? form = _foreground;
        _foreground = null;
        if (form is null)
        {
            return;
        }

        try
        {
            form.Close();
        }
        catch (Exception)
        {
            // 关不掉也不影响判据：下一次 SmallForeground 会覆盖它。
        }

        form.Dispose();
        Application.DoEvents();
    }

    /// <summary>
    /// 抢前台：释放 ALT（前台锁对「用户刚按过 ALT」有豁免）后 SetForegroundWindow。
    /// 返回是否真的抢到（GetForegroundWindow() == hwnd）—— 抢不到时调用方必须 FAIL，不许假装。
    /// </summary>
    private static bool AcquireForeground(IntPtr hwnd)
    {
        Win32Extra.keybd_event(Win32Extra.VK_MENU, 0, Win32Extra.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Win32Extra.keybd_event(
            Win32Extra.VK_MENU, 0, Win32Extra.KEYEVENTF_EXTENDEDKEY | Win32Extra.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Win32Extra.SetForegroundWindow(hwnd);
        Application.DoEvents();
        return Win32Extra.GetForegroundWindow() == hwnd;
    }

    private static string ForegroundDescription() => Win32.WindowDescription(Win32Extra.GetForegroundWindow());
}
