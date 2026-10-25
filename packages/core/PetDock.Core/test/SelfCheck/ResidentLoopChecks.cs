// 常驻帧循环的判据（RFC-14 §2.3）。
//
// 判据必须走**生产对象**：这里构造的是真的 LayeredFrameWindow 与真的 ResidentFrameLoop，
// 不是桩件。本项目栽过「自检自己造一份等价实现」这种门禁（恒真的 hook-not-installed 判据，
// 见 mistakes.md），所以这里的原则是：断言的对象就是生产路径上那个对象。
//
// 三条判据：
//   ui-resident-window-visible   Start() 之后窗口真的可见
//   ui-resident-frame-loop       帧泵真的在出帧（帧计数在等待窗内增长）
//   ui-resident-hidden-stops-pump Hide() 之后帧计数停止增长
using System;
using System.Threading;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class ResidentLoopChecks
{
    /// <summary>帧循环判据用的等待窗（毫秒）：够 30fps 的泵出若干帧，又不至于让自检变慢。</summary>
    private const int WaitMs = 600;

    /// <summary>判据的输出与计数回调（与 UiSelfTest 的 Check 同形）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    public static void Run(Check check)
    {
        WindowAndLoop(check);
    }

    private static void WindowAndLoop(Check check)
    {
        // 帧循环只在「前台不是全屏」时出帧：前台若是一个盖满显示器的程序，第一个 tick 就会把
        // 循环判成全屏档并停泵，帧计数恒为 0 —— 那是判据的世界没造对，不是产品缺陷
        // （2026-10-22 实测：同一份二进制浏览器最大化时 FAIL、否则 PASS）。
        // 世界那一问要宠物窗句柄（定位显示器），所以窗口先建、世界后证。
        using var window = new LayeredFrameWindow();
        string? world = ThrottleChecks.EnsureNotFullscreenWorld(window.WindowHandle);
        using var loop = new ResidentFrameLoop(window);

        bool visible = false;
        int framesAfterStart = 0;
        long ticksInWindow = 0;
        int framesAfterHide = 0;
        bool pumping = false;
        bool stopped = false;

        try
        {
            loop.Start();
            visible = window.Visible;
            pumping = loop.Pumping;

            // 让泵跑起来：先**等**它出帧（首帧是冷路径；本机若宠物窗被前台全屏窗盖住，
            // 遮挡档每 tick 还要做一次不透明取样，实测能到 ~0.9s/tick），再量它到底出没出帧。
            // 这一条断言的是「泵真的在出帧」，不是帧率 —— 帧率归 --perftest 的 D-14 判据。
            int before = loop.Frames;
            long ticksBefore = loop.Ticks;
            WaitForFrames(loop, before);
            long ticksWarm = loop.Ticks - ticksBefore;
            Spin(WaitMs);
            framesAfterStart = loop.Frames - before;
            ticksInWindow = loop.Ticks - ticksBefore - ticksWarm;

            // 隐藏 → 停泵：隐藏之后帧计数不得再增长。
            loop.Hide();
            Spin(120);
            int atHide = loop.Frames;
            Spin(WaitMs);
            framesAfterHide = loop.Frames - atHide;
            stopped = framesAfterHide == 0 && !loop.Pumping;
        }
        catch (Exception error)
        {
            check("ui-resident-window-visible", false, $"{error.GetType().Name}: {error.Message}");
            check("ui-resident-frame-loop", false, "exception");
            check("ui-resident-hidden-stops-pump", false, "exception");
            return;
        }
        finally
        {
            // 自检跑完不留窗口在桌面上。
            loop.Hide();
        }

        check("ui-resident-window-visible", visible, $"visible={visible}");
        check("ui-resident-frame-loop", world is null && pumping && framesAfterStart > 0,
            $"pumping={pumping} frames={framesAfterStart} waitMs={WaitMs}"
            + $" tier={loop.Tier} skipped={loop.Skipped} reused={window.LastFrameReused} ticks+{ticksInWindow}"
            + (world is null ? string.Empty : " world=" + world));
        check("ui-resident-hidden-stops-pump", stopped,
            $"framesAfterHide={framesAfterHide} pumping={loop.Pumping}");
    }

    /// <summary>
    /// 等帧计数超过 <paramref name="before"/>（最多 8s）。首帧是冷路径，遮挡档下每 tick 又更慢，
    /// 所以这里等的是「出没出帧」，不是「多快出帧」。
    /// </summary>
    private static void WaitForFrames(ResidentFrameLoop loop, int before)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 8000 && loop.Frames <= before)
        {
            Thread.Sleep(25);
        }
    }

    /// <summary>
    /// 等一小段时间。不用 Application.DoEvents —— 帧泵在**后台线程**上跑（FramePump），
    /// UI 线程只需要让出时间片；这里不泵消息，窗口的可见性判定已经由 Show() 定死。
    /// </summary>
    private static void Spin(int ms)
    {
        Thread.Sleep(ms);
        Application.DoEvents();
    }
}
