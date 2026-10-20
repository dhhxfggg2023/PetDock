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
        using var window = new LayeredFrameWindow();
        using var loop = new ResidentFrameLoop(window);

        bool visible = false;
        int framesAfterStart = 0;
        int framesAfterHide = 0;
        bool pumping = false;
        bool stopped = false;

        try
        {
            loop.Start();
            visible = window.Visible;
            pumping = loop.Pumping;

            // 让泵跑一会儿：帧计数必须增长。
            int before = loop.Frames;
            Spin(WaitMs);
            framesAfterStart = loop.Frames - before;

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
        check("ui-resident-frame-loop", pumping && framesAfterStart > 0,
            $"pumping={pumping} frames={framesAfterStart} waitMs={WaitMs}");
        check("ui-resident-hidden-stops-pump", stopped,
            $"framesAfterHide={framesAfterHide} pumping={loop.Pumping}");
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
