// 天体模式的判据（2026-10-29；2026-10-30 随万有引力重写）：命令形态 `--uitest`，判据行 `ui-celestial-*`。
//
// 运动好不好看只能人眼说，但「有没有互相吸引、近到贴住会不会塌成一点、
// 长时间跑会不会飞出屏幕或出现 NaN」这些是纯函数能回答的 —— 也正是这类代码最容易悄悄错的地方。
using System;
using System.Collections.Generic;
using System.Drawing;
using PetDock.Core.PetRuntime;
using PetDock.Core.Ui;
using PetDock.Hosting;

namespace PetDock.Core.Compositor;

internal static class CelestialChecks
{
    internal delegate void Report(string name, bool ok, string detail);

    private static readonly Rectangle Area = new(0, 0, 1920, 1080);

    public static void Run(Report check)
    {
        try
        {
            Attracts(check);
            NoCollapse(check);
            Bounded(check);
            ZeroStep(check);
            SinglePet(check);
            DefaultOff(check);
        }
        catch (Exception error)
        {
            check("ui-celestial-crash", false, error.GetType().Name + ": " + error.Message);
        }
    }

    private static double Distance(CelestialBody a, CelestialBody b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>两只离得远 → 互相吸引（距离随时间变小）。这就是「万有引力」这回事本身。</summary>
    private static void Attracts(Report check)
    {
        var bodies = new List<CelestialBody>
        {
            new(300, 500, 0, 0),
            new(1500, 500, 0, 0),
        };

        double before = Distance(bodies[0], bodies[1]);
        IReadOnlyList<CelestialBody> after = bodies;
        for (int i = 0; i < 20; i++)
        {
            after = CelestialMotion.Step(after, Area, 192, 192, 0.05);
        }

        double now = Distance(after[0], after[1]);
        bool fasterWhenCloser = CelestialMotion.GravityAt(200) > CelestialMotion.GravityAt(800);
        check(
            "ui-celestial-attracts",
            now < before - 1 && fasterWhenCloser,
            $"distance {before:0} -> {now:0} (应互相靠近；越近加速度越大) "
            + $"a(200)={CelestialMotion.GravityAt(200):0.00} a(800)={CelestialMotion.GravityAt(800):0.00}");
    }

    /// <summary>贴到一起也不会塌成一个点：软核把距离顶住。跑 30 秒看最小间距。</summary>
    private static void NoCollapse(Report check)
    {
        var bodies = new List<CelestialBody>
        {
            new(900, 500, 0, 0),
            new(910, 500, 0, 0),
        };

        // 从「几乎重叠」（10px）出发，跑 30 秒看它们落在哪。
        // 看的是**终态**：起点就是 10px，取全程最小值量不出「会不会塌」——
        // 这条判据一开始写错过（min-distance=10 直接判红），错在断言而不是物理。
        IReadOnlyList<CelestialBody> state = bodies;
        double closest = double.MaxValue;
        bool finite = true;
        for (int i = 0; i < 900; i++)
        {
            state = CelestialMotion.Step(state, Area, 192, 192, 1.0 / 30.0);
            double d = Distance(state[0], state[1]);
            if (d < closest)
            {
                closest = d;
            }

            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                finite = false;
                break;
            }
        }

        double settled = Distance(state[0], state[1]);
        check(
            "ui-celestial-no-collapse",
            finite && settled > 60 && settled < 420 && closest > 1,
            $"settled={settled:0} closest={closest:0} (软核与引力应在百来像素处平衡，既不合体也不分开) "
            + $"core={CelestialMotion.CorePushAcceleration:0} G={CelestialMotion.GravitationalConstant:0}");
    }

    /// <summary>长时间跑：三只都在屏幕里、都是有限值、速度不超上限。</summary>
    private static void Bounded(Report check)
    {
        IReadOnlyList<CelestialBody> state = new List<CelestialBody>
        {
            new(200, 200, 0, 0),
            new(1600, 300, 0, 0),
            new(900, 900, 0, 0),
        };

        bool ok = true;
        double maxSpeed = 0;
        for (int i = 0; i < 1200 && ok; i++)
        {
            state = CelestialMotion.Step(state, Area, 192, 192, 1.0 / 30.0);
            foreach (CelestialBody body in state)
            {
                double speed = Math.Sqrt((body.Vx * body.Vx) + (body.Vy * body.Vy));
                if (speed > maxSpeed)
                {
                    maxSpeed = speed;
                }

                if (double.IsNaN(body.X) || double.IsNaN(body.Y)
                    || body.X < Area.Left - 0.5 || body.X > Area.Right - 192 + 0.5
                    || body.Y < Area.Top - 0.5 || body.Y > Area.Bottom - 192 + 0.5
                    || speed > CelestialMotion.MaxSpeed + 0.5)
                {
                    ok = false;
                    break;
                }
            }
        }

        check("ui-celestial-bounded", ok, $"40 秒后仍在屏内且有限 max-speed={maxSpeed:0}");
    }

    /// <summary>零步不动：dt=0 必须原样返回（自检与暂停帧都依赖它）。</summary>
    private static void ZeroStep(Report check)
    {
        var bodies = new List<CelestialBody>
        {
            new(300, 300, 12, -7),
            new(1200, 700, -4, 3),
        };

        IReadOnlyList<CelestialBody> same = CelestialMotion.Step(bodies, Area, 192, 192, 0);
        check(
            "ui-celestial-zero-step",
            same.Count == 2 && Math.Abs(same[0].X - 300) < 0.001 && Math.Abs(same[1].Y - 700) < 0.001,
            $"same={same.Count} first=({same[0].X:0},{same[0].Y:0})");
    }

    /// <summary>只有一只：没有引力对象，朝工作区中心靠拢并停住（不是原地绕圈）。</summary>
    private static void SinglePet(Report check)
    {
        IReadOnlyList<CelestialBody> state = new List<CelestialBody> { new(100, 100, 0, 0) };
        double centreX = Area.Left + (Area.Width / 2.0);
        double centreY = Area.Top + (Area.Height / 2.0);

        for (int i = 0; i < 900; i++)
        {
            state = CelestialMotion.Step(state, Area, 192, 192, 1.0 / 30.0);
        }

        double dx = state[0].X - centreX;
        double dy = state[0].Y - centreY;
        double offset = Math.Sqrt((dx * dx) + (dy * dy));
        double speed = Math.Sqrt((state[0].Vx * state[0].Vx) + (state[0].Vy * state[0].Vy));
        check(
            "ui-celestial-single-pet",
            offset < 40 && speed < 5,
            $"offset-from-centre={offset:0} speed={speed:0.0} (一只时应靠拢中心并停下)");
    }

    private static void DefaultOff(Report check)
    {
        check(
            "ui-celestial-default-off",
            UserSettings.Default.CelestialMode is null && !SettingsView.Default.CelestialMode,
            $"settings={(UserSettings.Default.CelestialMode?.ToString() ?? "(unset)")}"
            + $" view={SettingsView.Default.CelestialMode}");
    }
}
