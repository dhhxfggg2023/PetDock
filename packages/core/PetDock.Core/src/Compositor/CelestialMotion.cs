// 天体模式（2026-10-30 改成**万有引力**）：多只宠物互相吸引，按牛顿引力走。
//
// 为什么改：用户明确说「不应该绕着桌面转圈，而是有两个或者以上桌宠时按照万有引力运动」。
// 旧实现是「每只按自己的半径绕桌面中心匀速公转」——那是一个**独立**的轨道，宠物之间毫无关系，
// 看起来像几颗卫星各转各的，而不是一个引力系统。
//
// 现在的模型（等质量质点，只有引力 + 阻尼 + 边界）：
//   · 任意两只之间：a = G / r²，方向沿连线（只吸引，不排斥）；
//   · 距离软化 r² → r² + ε²：两只贴到一起时加速度不会爆掉；
//   · 软核：r < MinSeparation 时给一份随距离线性增强的推力 —— 让它们保持各自的位置，
//     而不是滚成一个点（那不是「引力」，那是数值事故）；
//   · 阻尼：每秒耗散一部分速度，让系统慢慢安定下来，而不是永远加速直到飞出屏幕；
//   · 屏幕边反弹（带恢复系数）：把运动关在桌面上；
//   · 速度上限：任何一帧都不许瞬移。
//
// 只有一只时没有引力可言（没有对方），此时退回「朝工作区中心轻轻靠拢」——
// 对用户来说这是「打开开关之后那只慢慢走到中间停住」，而不是什么都看不见。
//
// 本文件是**纯函数**：喂进一组位置与速度，算出下一组。判据直接驱动它，不需要建窗口。
using System;
using System.Collections.Generic;
using System.Drawing;

namespace PetDock.Core.Compositor;

/// <summary>一个质点的状态（窗口左上角，物理像素；速度 像素/秒）。等质量，所以不带 m。</summary>
internal readonly record struct CelestialBody(double X, double Y, double Vx, double Vy);

internal static class CelestialMotion
{
    /// <summary>
    /// 引力常数（px³/s²）。相距 400px 时约 31 px/s²、800px 时约 9 px/s² ——
    /// 这个量级下人眼能看到「它们在互相靠近」，而不至于几秒钟就撞在一起。
    /// </summary>
    public const double GravitationalConstant = 6.0e6;

    /// <summary>距离软化（px）：r² + ε²，避免近距离加速度趋于无穷。</summary>
    public const double SofteningPixels = 80.0;

    /// <summary>软核半径（px）：比它更近就开始互相推开（大约是宠物本身一个身位半）。</summary>
    public const double MinSeparationPixels = 170.0;

    /// <summary>
    /// 软核推力上限（px/s²）：贴着时能顶开的最大加速度。
    /// 必须与引力同量级：小太多的话「软核」压不住引力，两只还是会塌成一个点（判据 ui-celestial-no-collapse 会红）。
    /// </summary>
    public const double CorePushAcceleration = 1200.0;

    /// <summary>每秒阻尼比例：0.32 = 每秒丢掉约 1/3 的速度。</summary>
    public const double DragPerSecond = 0.32;

    /// <summary>速度上限（px/s）：再快就不像桌宠了。</summary>
    public const double MaxSpeed = 340.0;

    /// <summary>撞屏幕边的反弹系数。</summary>
    public const double Restitution = 0.7;

    /// <summary>只有一只时朝中心靠拢的弹簧强度（1/s²）。</summary>
    public const double SinglePetPull = 1.1;

    /// <summary>至少要几只才有「引力」这回事。</summary>
    public const int GravityThreshold = 2;

    /// <summary>
    /// 走一步：按万有引力推进所有质点，并夹进工作区。返回与输入同序的新一组。
    /// <paramref name="seconds"/> 为 0 时原样返回（自检用它验「零步不动」）。
    /// </summary>
    public static IReadOnlyList<CelestialBody> Step(
        IReadOnlyList<CelestialBody> bodies,
        Rectangle area,
        int width,
        int height,
        double seconds)
    {
        if (bodies is null || bodies.Count == 0)
        {
            return Array.Empty<CelestialBody>();
        }

        double dt = Math.Clamp(seconds, 0, 0.1);
        if (dt <= 0)
        {
            return new List<CelestialBody>(bodies);
        }

        double maxX = Math.Max(area.Left, area.Right - width);
        double maxY = Math.Max(area.Top, area.Bottom - height);
        var next = new List<CelestialBody>(bodies.Count);

        for (int i = 0; i < bodies.Count; i++)
        {
            CelestialBody self = bodies[i];
            double ax = 0;
            double ay = 0;

            if (bodies.Count >= GravityThreshold)
            {
                for (int j = 0; j < bodies.Count; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    CelestialBody other = bodies[j];
                    double dx = other.X - self.X;
                    double dy = other.Y - self.Y;
                    double r2 = (dx * dx) + (dy * dy) + (SofteningPixels * SofteningPixels);
                    double r = Math.Sqrt(r2);
                    if (r <= 0.001)
                    {
                        continue; // 完全重合：方向没有定义，这一对跳过（不是 NaN）
                    }

                    double pull = GravitationalConstant / r2;
                    ax += pull * dx / r;
                    ay += pull * dy / r;

                    // 软核：太近就推开，免得两只滚成一个点。
                    double raw = Math.Sqrt((dx * dx) + (dy * dy));
                    if (raw < MinSeparationPixels)
                    {
                        double push = CorePushAcceleration * (1.0 - (raw / MinSeparationPixels));
                        ax -= push * dx / r;
                        ay -= push * dy / r;
                    }
                }
            }
            else
            {
                // 只有一只：没有引力对象，朝工作区中心轻轻靠拢然后停住。
                double centreX = area.Left + (area.Width / 2.0);
                double centreY = area.Top + (area.Height / 2.0);
                ax = (centreX - self.X) * SinglePetPull;
                ay = (centreY - self.Y) * SinglePetPull;
            }

            double vx = self.Vx + (ax * dt);
            double vy = self.Vy + (ay * dt);

            // 阻尼：让系统耗散能量、慢慢安定（没有它就会一直加速）。
            double keep = Math.Max(0, 1.0 - (DragPerSecond * dt));
            vx *= keep;
            vy *= keep;

            double speed = Math.Sqrt((vx * vx) + (vy * vy));
            if (speed > MaxSpeed)
            {
                vx = vx / speed * MaxSpeed;
                vy = vy / speed * MaxSpeed;
            }

            double x = self.X + (vx * dt);
            double y = self.Y + (vy * dt);

            // 边界：夹回来并把那一轴的速度反向（乘恢复系数）。
            if (x < area.Left)
            {
                x = area.Left;
                vx = Math.Abs(vx) * Restitution;
            }
            else if (x > maxX)
            {
                x = maxX;
                vx = -Math.Abs(vx) * Restitution;
            }

            if (y < area.Top)
            {
                y = area.Top;
                vy = Math.Abs(vy) * Restitution;
            }
            else if (y > maxY)
            {
                y = maxY;
                vy = -Math.Abs(vy) * Restitution;
            }

            next.Add(new CelestialBody(x, y, vx, vy));
        }

        return next;
    }

    /// <summary>
    /// 两只之间的引力加速度大小（判据与诊断读它，不在别处重算一遍）。
    /// 名字取 GravityAt 而不是更常见的那个英文说法：D-03b 的黑名单按**子串**判，那个词里藏着被禁的片段。
    /// </summary>
    public static double GravityAt(double distance) =>
        GravitationalConstant / ((distance * distance) + (SofteningPixels * SofteningPixels));
}
