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
using System.Threading;
using System.Windows.Forms;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal static partial class ThrottleChecks
{
    private static Form? _foreground;

    /// <summary>世界检查的采样次数（跨一个泵周期；见 <see cref="EnsureNotFullscreenWorld"/>）。</summary>
    private const int WorldSamples = 3;

    /// <summary>两次世界采样之间的间隔：比 30fps 的一个周期略长，够前台回落暴露出来。</summary>
    private const int WorldSampleGapMs = 45;

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
    /// 把「前台不是全屏」这一世界状态重建出来，并**当场证出来**（RFC-21 §4：绝不假装成立）。
    /// 返回 null = 成立；返回字符串 = 可打印的失败原因。
    ///
    /// 为什么每次断言前都要重建：宠物窗是 `WS_EX_NOACTIVATE`，它永远拿不到前台；
    /// 覆盖窗关掉之后前台会**回落到桌面上别的程序**（2026-10-22 实测：回落到一个盖满
    /// 显示器的浏览器窗，于是后面几条判据在「全屏」世界里跑，`tier=fullscreen` 恒成立）。
    /// 那是判据侧的世界错了，不是产品错了。
    ///
    /// **证据是产品自己那一问，不是「我抢到前台了」**（2026-10-25 修正，supersedes 2026-10-22
    /// 那条「用 GetForegroundWindow() == 本窗 证出来」的做法）：判据需要的世界是「前台没有盖满
    /// 宠物所在显示器的窗」，而那正是 FrameThrottlePolicy 判全屏档时问的
    /// <see cref="ThrottleProbe.SampleFullscreen"/>。旧证据是**手段**不是世界 —— 别的程序占着
    /// 前台（终端 / 控制中心 / 任何普通窗）时世界其实成立，判据却报 `foreground-not-acquired`
    /// 并判红，于是同一份二进制绿/红/绿（2026-10-22、10-23 各记一次，10-25 复现）。
    /// 抢前台照做（让世界尽量可控），但它**不再**参与结论。
    /// </summary>
    /// <param name="petWindow">宠物窗句柄：全屏那一问要拿它定位显示器（多屏下别的屏上的全屏应用不该算）。</param>
    /// <summary>
    /// 负例探针（`--world-probe=unavailable`，仅诊断用）：把「世界不可用」人为立起来，
    /// 用来证明**跳过路径真的会生效** —— 只看代码说「建不出来就跳过」，没有任何判据能证明
    /// 那条分支跑得到，等于又一条靠嘴说的结论。默认 null，是一次 no-op，不影响正常运行。
    /// 与 SkipLedger.ProbeFromArgs 同源（那里证的是「跳过改变结论」，这里证的是「世界失败会走跳过」）。
    /// </summary>
    internal static string? ForcedUnavailable;

    internal static string? EnsureNotFullscreenWorld(IntPtr petWindow)
    {
        if (ForcedUnavailable is not null)
        {
            return ForcedUnavailable;
        }

        _ = SmallForeground();
        var probe = new ThrottleProbe();

        // 采样必须**跨一个泵周期**都在说「不是全屏」，只采一次不够：刚抢完前台那一刻，
        // `GetForegroundWindow()` 常常还是**我们自己那个小窗**，而几十毫秒后前台会回落给真正
        // 拿到焦点的那只窗（2026-10-25 实测：锁屏态下 SetForegroundWindow 看似成功，随后回落给
        // 锁屏界面，于是世界检查放行、产品第一个 tick 直接判全屏，窗口里全是 ticks=0）。
        // 采样不是「等运气」：任意一次说全屏就 FAIL 并打出是谁 —— 世界不成立时绝不继续断言。
        for (int i = 0; i < WorldSamples; i++)
        {
            if (probe.SampleFullscreen(petWindow))
            {
                return "foreground-is-fullscreen foreground=" + probe.LastForeground
                    + " pet=same-monitor sample=" + (i + 1) + "/" + WorldSamples;
            }

            if (i + 1 < WorldSamples)
            {
                Thread.Sleep(WorldSampleGapMs);
                Application.DoEvents();
            }
        }

        return null;
    }

    /// <summary>把世界状态失败原因拼进判据读数（null ⇒ 空串）。</summary>
    private static string WorldSuffix(string? world) => world is null ? string.Empty : " world=" + world;

    /// <summary>
    /// 世界状态（「前台不是全屏」）建不出来时，依赖它的判据按**环境跳过**登记（2026-10-30）。
    ///
    /// 为什么不再判红：判据要断言的是「前台不是全屏」这一世界里的产品行为。当前台真被一个盖满
    /// 显示器的窗（浏览器 / 播放器 / 全屏游戏）占着时，这个世界**造不出来**，产品按设计进全屏档
    /// 并停泵 —— 读数会是 ticks=0 / tier=fullscreen，读起来像产品坏了，其实桌面状态如此。
    /// 「绝不假装成立」那条纪律继续成立：这里既不判绿（没验到），也不判红（红的不是产品），
    /// 而是按 SkipLedger 的 Environment 口径登记，末行 marker 变 UI-OK skipped=N。
    ///
    /// 读数原样带出去：world 就是世界的实测失败原因（含 foreground=谁占着前台）。
    /// 只对**依赖该世界**的判据用它；与前台无关的判据照旧自己下结论。
    /// </summary>
    internal static void WorldVerdict(Check done, string name, bool ok, string detail, string? world)
    {
        if (world is null)
        {
            done(name, ok, detail);
            return;
        }

        done(name, true, SkipLedger.EnvironmentSkip(detail, world));
    }

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
