// 窗口拓扑与命中区的端到端判据（2026-10-29）：命令形态 `--uitest`。
//
// 纯函数判据（PhysicsSupportChecks / PackHitRegionChecks）证明「算得对」，
// 这一组证明「接上了」：真的建一个分层窗、真的抛、真的让它落在假拓扑上、真的走出边缘掉下去，
// 并且真的把 WM_NCHITTEST 的答案改成命中区说的那样。缺这一层，上面那两组的绿可能只是自说自话。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal static class WindowTopologyChecks
{
    internal delegate void Report(string name, bool ok, string detail);

    public static void Run(Report check)
    {
        LayeredFrameWindow? window = null;
        try
        {
            window = new LayeredFrameWindow();
            window.Show();
            Application.DoEvents();

            // 摆到工作区中间再来：默认位置贴着右下角，下面没有 100px 落差可造。
            window.MoveToCenter();
            Application.DoEvents();

            Rectangle bounds = window.Bounds;
            Rectangle area = Screen.FromPoint(bounds.Location).WorkingArea;
            int top = bounds.Bottom + 100;
            if (top + 80 > area.Bottom)
            {
                // 屏幕太矮时这条判据没法摆出「上方有落差」的场地：如实跳过，不假装通过。
                check("ui-topology-platform-land", false, $"screen-too-short area={area} bounds={bounds}");
                return;
            }

            LandOnPlatform(check, window, bounds, top);
            WalkOff(check, window, top);
            HitRegions(check, window);
        }
        catch (Exception error)
        {
            check("ui-topology-window-crash", false, error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            window?.Dispose();
        }
    }

    private static void LandOnPlatform(Report check, LayeredFrameWindow window, Rectangle bounds, int top)
    {
        // 假拓扑：一块横在宠物下方 100px 的「窗口顶边」，比宠物宽得多（水平重叠足够）。
        var platform = new Rectangle(bounds.Left - 200, top, bounds.Width + 400, 400);
        window.Platforms = () => new[] { platform };

        window.StartThrowForCheck(0, 300);
        bool landed = WaitFor(5000, () => !window.PhysicsActive && Math.Abs(window.Bounds.Bottom - top) <= 2);
        check(
            "ui-topology-platform-land",
            landed,
            $"landed={landed} bottom={window.Bounds.Bottom} expect={top}");
    }

    private static void WalkOff(Report check, LayeredFrameWindow window, int top)
    {
        // 支撑面消失（窗口被关掉）：必须重新落下去，而不是停在半空。
        window.Platforms = () => Array.Empty<Rectangle>();
        bool fell = WaitFor(3000, () => Math.Abs(window.Bounds.Bottom - top) > 20);
        bool settled = WaitFor(5000, () => !window.PhysicsActive);
        check(
            "ui-topology-walk-off",
            fell && settled,
            $"fell={fell} settled={settled} bottom={window.Bounds.Bottom} (platform top was {top})");
        window.Platforms = () => Array.Empty<Rectangle>();
    }

    private static void HitRegions(Report check, LayeredFrameWindow window)
    {
        // 只声明左上角一块：右下角应变成穿透（-1），左上角仍是可点（1）。
        window.HitBoxes = new[] { new HitBox(0, 0, 0.5, 0.5) };
        Rectangle rect = window.Bounds;
        var inside = new POINT { X = rect.Left + (rect.Width / 4), Y = rect.Top + (rect.Height / 4) };
        var outside = new POINT { X = rect.Left + (rect.Width * 3 / 4), Y = rect.Top + (rect.Height * 3 / 4) };

        int insideAnswer = Win32.HitTest(window.Handle, inside);
        int outsideAnswer = Win32.HitTest(window.Handle, outside);
        window.HitBoxes = null;

        check(
            "ui-pack-hits-window",
            insideAnswer == Win32.HTCLIENT && outsideAnswer == Win32.HTTRANSPARENT,
            $"inside={insideAnswer} outside={outsideAnswer} (1=可点 / -1=穿透)");
    }

    private static bool WaitFor(int timeoutMs, Func<bool> predicate)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate())
            {
                return true;
            }

            Application.DoEvents();
            System.Threading.Thread.Sleep(10);
        }

        return predicate();
    }
}
