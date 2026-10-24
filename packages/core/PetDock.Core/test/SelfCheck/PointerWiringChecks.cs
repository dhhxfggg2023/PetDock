// RFC-35 §4：交互层（docs/11 §5）真的接到窗口鼠标消息上的判据。
//
// 为什么值得单独立一片：`PetInstance.PointerDown/Move/Up` 在接线之前**全仓零调用方**——
// 优先级仲裁、命中性判定、10 秒会话预算三样都实现了，却一次也没在真实链路上跑过。
// 所以判据必须走**真窗口消息**（WM_LBUTTONDOWN/MOUSEMOVE/LBUTTONUP），而不是直接调
// `PointerDown` —— 后者只能证明方法自身可调用，证明不了「鼠标按下去会发生什么」。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

internal static class PointerWiringChecks
{
    /// <summary>判据的输出与计数回调（与 UiSelfTest 的 Check 同形）。</summary>
    internal delegate void PointerCheck(string name, bool ok, string detail);

    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmActivate = 0x0006;
    private const int WaInactive = 0;

    /// <summary>
    /// 消息里带的像素坐标，必须落在 64×64 帧内：`HitsAt(64, 64)` 是越界坐标，
    /// 直接返回 false（第一版判据就死在这里——四条全红而日志全空，看起来像「窗口没收到消息」）。
    /// </summary>
    private const int InsideX = 32;
    private const int InsideY = 32;

    public static void Run(PointerCheck check)
    {
        SessionLifecycle(check);
        CancelsAndBudget(check);
        DragFallback(check);
    }

    /// <summary>按下 → 会话开始；移动 → 会话内移动；抬起 → 会话结束。</summary>
    private static void SessionLifecycle(PointerCheck check)
    {
        var log = new List<string>();
        var surface = new StubSurface("s1", priority: 10, hit: true, log);
        try
        {
            PetInstance pet = PetDriveTest.MakeInstance(out _, out _, new List<IPointerSurface> { surface });
            using var window = new LayeredFrameWindow();
            FrameComposition.WirePointer(window, pet);

            // 会话要过「穿透判定」那一关（PetInstance.PointerDown 第一件事是 HitsAt），
            // 所以先合成一帧：桩件视图的帧是整幅不透明，中心点必然命中。
            // 而 QueryFrame 要求实例处于 Running（停在 Discovered 会抛 InvalidOperationException），
            // 所以先走生产同款的两步：附加尺寸 → Start()。
            pet.AttachAndStart(64, 64, 1.0);
            pet.Start();
            pet.QueryFrame(0);
            IntPtr hwnd = window.Handle;
            Send(hwnd, WmLeftButtonDown, (IntPtr)1, Pack(InsideX, InsideY));
            bool started = pet.SessionsStarted == 1 && pet.ActiveSession is not null
                && pet.ActiveSurfaceId == "s1" && log.Contains("s1:down");
            check("ui-pointer-session-down", started,
                $"sessions={pet.SessionsStarted} surface={pet.ActiveSurfaceId ?? "(none)"} log={Join(log)}");

            // 按下被交互层接手时，窗口不许同时开始拖自己。
            check("ui-pointer-down-blocks-drag", !window.IsDraggingNow, $"dragging={window.IsDraggingNow}");

            Send(hwnd, WmMouseMove, (IntPtr)1, Pack(InsideX + 1, InsideY + 2));
            Send(hwnd, WmLeftButtonUp, IntPtr.Zero, Pack(InsideX + 1, InsideY + 2));
            bool ended = pet.SessionsEnded == 1 && pet.ActiveSession is null
                && log.Contains("s1:move") && log.Contains("s1:up");
            check("ui-pointer-session-move-up", ended,
                $"ended={pet.SessionsEnded} active={pet.ActiveSession is not null} log={Join(log)}");
        }
        catch (Exception error)
        {
            check("ui-pointer-session-down", false, $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>失焦 = 宿主取消（docs/11:183）；10 秒预算 = 强制取消（docs/11:260）。</summary>
    private static void CancelsAndBudget(PointerCheck check)
    {
        var log = new List<string>();
        var surface = new StubSurface("s2", priority: 0, hit: true, log);
        try
        {
            PetInstance pet = PetDriveTest.MakeInstance(out _, out _, new List<IPointerSurface> { surface });
            using var window = new LayeredFrameWindow();
            FrameComposition.WirePointer(window, pet);
            pet.AttachAndStart(64, 64, 1.0);
            pet.Start();
            pet.QueryFrame(0);
            IntPtr hwnd = window.Handle;

            Send(hwnd, WmLeftButtonDown, (IntPtr)1, Pack(InsideX, InsideY));
            Send(hwnd, WmActivate, (IntPtr)WaInactive, IntPtr.Zero);
            bool cancelled = log.Contains("s2:cancel") && pet.ActiveSession is null && pet.SessionsEnded == 1;
            check("ui-pointer-cancel-on-deactivate", cancelled,
                $"log={Join(log)} active={pet.ActiveSession is not null} ended={pet.SessionsEnded}");

            // 预算：直接从会话开始时刻往后推 10s + 1ms。
            int now = (int)Environment.TickCount64;
            bool reopened = pet.PointerDown(InsideX, InsideY, PointerButton.Left, now) || pet.ActiveSession is not null;
            bool forced = pet.EnforceSessionBudget(now + PetInstance.SessionCancelBudgetMs + 1);
            bool budgetOk = reopened && forced && pet.SessionsForcedCancel == 1 && log.Contains("s2:cancel");
            check("ui-pointer-session-budget", budgetOk,
                $"reopened={reopened} forced={forced} forcedCount={pet.SessionsForcedCancel}");
        }
        catch (Exception error)
        {
            check("ui-pointer-cancel-on-deactivate", false, $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>没有交互面接住时，窗口的拖窗行为必须原样保留（M1 以来的能力）。</summary>
    private static void DragFallback(PointerCheck check)
    {
        try
        {
            PetInstance pet = PetDriveTest.MakeInstance(out _, out _, new List<IPointerSurface>());
            using var window = new LayeredFrameWindow();
            FrameComposition.WirePointer(window, pet);
            pet.AttachAndStart(64, 64, 1.0);
            pet.Start();
            pet.QueryFrame(0);

            Send(window.Handle, WmLeftButtonDown, (IntPtr)1, Pack(InsideX, InsideY));
            bool drags = window.IsDraggingNow && pet.SessionsStarted == 0;
            check("ui-pointer-drag-when-unclaimed", drags,
                $"dragging={window.IsDraggingNow} sessions={pet.SessionsStarted}");
            Send(window.Handle, WmLeftButtonUp, IntPtr.Zero, Pack(InsideX, InsideY));
        }
        catch (Exception error)
        {
            check("ui-pointer-drag-when-unclaimed", false, $"{error.GetType().Name}: {error.Message}");
        }
    }

    private static string Join(List<string> log) => log.Count == 0 ? "(empty)" : string.Join(",", log);

    private static IntPtr Pack(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    private static void Send(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam) =>
        SendMessage(hwnd, message, wParam, lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
