// 天体模式的判据（2026-10-29）：命令形态 `--uitest`，判据行 `ui-celestial-*`。
//
// 运动本身只能靠人眼说好不好看，但「轨道半径不漂、近处比远处快、中心退化不产生 NaN、
// 结果永远在屏幕里」这些是纯函数能回答的 —— 也正是这类代码最容易悄悄错的地方。
using System;
using System.Drawing;
using PetDock.Core.PetRuntime;
using PetDock.Core.Ui;
using PetDock.Hosting;

namespace PetDock.Core.Compositor;

internal static class CelestialChecks
{
    internal delegate void Report(string name, bool ok, string detail);

    private static readonly Rectangle Area = new(0, 0, 1920, 1080);

    private static readonly PetPoint Center = new(960, 540);

    public static void Run(Report check)
    {
        try
        {
            Orbit(check);
            Kepler(check);
            Degenerate(check);
            Clamp(check);
            Flatten(check);
            DefaultOff(check);
        }
        catch (Exception error)
        {
            check("ui-celestial-crash", false, error.GetType().Name + ": " + error.Message);
        }
    }

    private static void Orbit(Report check)
    {
        var start = new PetPoint(1160, 540);
        PetPoint next = CelestialMotion.Next(start, Center, 0.5, Area, 192, 192);
        double radius = RadiusOf(next);
        bool moved = Math.Abs(next.X - start.X) > 1 || Math.Abs(next.Y - start.Y) > 1;
        check(
            "ui-celestial-orbit",
            moved && Math.Abs(radius - 200) < 2,
            $"next={next.X:0},{next.Y:0} radius={radius:0} (半径应保持 ≈200)");
    }

    private static void Kepler(Report check)
    {
        double near = CelestialMotion.AngularSpeed(120);
        double far = CelestialMotion.AngularSpeed(600);
        var start = new PetPoint(1160, 540);
        PetPoint still = CelestialMotion.Next(start, Center, 0, Area, 192, 192);
        check(
            "ui-celestial-kepler",
            near > far && Math.Abs(still.X - start.X) < 1 && Math.Abs(still.Y - start.Y) < 1,
            $"near={near:0.00} far={far:0.00} dt0=({still.X:0},{still.Y:0})");
    }

    private static void Degenerate(Report check)
    {
        PetPoint next = CelestialMotion.Next(Center, Center, 0.3, Area, 192, 192);
        double radius = RadiusOf(next);
        bool finite = !double.IsNaN(next.X) && !double.IsNaN(next.Y)
            && !double.IsInfinity(next.X) && !double.IsInfinity(next.Y);
        check(
            "ui-celestial-degenerate",
            finite && radius >= CelestialMotion.MinRadius - 1,
            $"next={next.X:0},{next.Y:0} radius={radius:0} (正好在中心时要给一条轨道)");
    }

    private static void Clamp(Report check)
    {
        var far = new PetPoint(9000, 9000);
        PetPoint next = CelestialMotion.Next(far, Center, 0.3, Area, 192, 192);
        bool inside = next.X >= Area.Left && next.X <= Area.Right - 192
            && next.Y >= Area.Top && next.Y <= Area.Bottom - 192;
        check("ui-celestial-clamp", inside, $"next={next.X:0},{next.Y:0} area={Area}");
    }

    private static void Flatten(Report check)
    {
        // 竖直方向按 0.62 压扁：正上方的点算半径时要做除法，结果仍要落在屏内且是有限值。
        var above = new PetPoint(960, 940);
        PetPoint next = CelestialMotion.Next(above, Center, 0.6, Area, 192, 192);
        bool finite = !double.IsNaN(next.X) && !double.IsNaN(next.Y);
        bool inside = next.X >= 0 && next.X <= Area.Right - 192 && next.Y >= 0 && next.Y <= Area.Bottom - 192;
        check(
            "ui-celestial-flatten",
            finite && inside,
            $"next={next.X:0},{next.Y:0} flatten={CelestialMotion.Flatten}");
    }

    private static void DefaultOff(Report check)
    {
        check(
            "ui-celestial-default-off",
            UserSettings.Default.CelestialMode is null && !SettingsView.Default.CelestialMode,
            $"settings={(UserSettings.Default.CelestialMode?.ToString() ?? "(unset)")}"
            + $" view={SettingsView.Default.CelestialMode}");
    }

    /// <summary>把位置换算成轨道半径（与 Next 的算法一致）。</summary>
    private static double RadiusOf(PetPoint position)
    {
        double dx = position.X - Center.X;
        double dy = (position.Y - Center.Y) / CelestialMotion.Flatten;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
