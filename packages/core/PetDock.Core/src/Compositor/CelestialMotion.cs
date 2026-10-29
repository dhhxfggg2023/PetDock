// 天体模式（2026-10-29）：多只宠物一起存在时，让它们绕桌面中心做椭圆运动。
//
// 这不是「碰撞」，也不抢行为插件的位置：它是宿主级的一个**可选运动模式**（设置里开关）。
// 打开时宿主独占运动（把每只的 ExternalMotion 置真，行为层让位）；拖动 / 抛掷期间让位给
// 指针会话与物理，落手之后按当前位置接着转 —— 所以「角度」不单独存状态，每次都从当前位置反推。
//
// 轨道参数：
//   · 半径 = 宠物当前到中心的距离（压扁方向按 FLATTEN 折算），所以开模式不会让它跳一下；
//   · 角速度随半径变小而变快（近处快、远处慢），看起来像行星而不是一条传送带；
//   · 椭圆在竖直方向压扁，模拟「斜着看一个盘面」。
using System;
using System.Drawing;
using PetDock.Core.PetRuntime;

namespace PetDock.Core.Compositor;

internal static class CelestialMotion
{
    /// <summary>竖直方向的压扁比例（1 = 正圆）。</summary>
    public const double Flatten = 0.62;

    /// <summary>最小半径（像素）：太靠近中心时给一条轨道，否则原地打转。</summary>
    public const double MinRadius = 90;

    /// <summary>半径上限相对工作区短边的比例：别把宠物甩到屏幕外。</summary>
    private const double MaxRadiusRatio = 0.45;

    /// <summary>角速度（弧度/秒）：近处快、远处慢。</summary>
    public static double AngularSpeed(double radius) => 0.35 + (900.0 / Math.Max(MinRadius, radius));

    /// <summary>
    /// 走一步：从当前位置反推角度与半径，推进 <paramref name="seconds"/> 秒，再夹回工作区。
    /// 纯函数 —— 判据直接喂坐标，不需要建窗口。
    /// </summary>
    public static PetPoint Next(
        PetPoint position,
        PetPoint center,
        double seconds,
        Rectangle area,
        int width,
        int height)
    {
        double dx = position.X - center.X;
        double dy = (position.Y - center.Y) / Flatten;
        double radius = Math.Sqrt((dx * dx) + (dy * dy));
        if (radius < 1)
        {
            radius = Math.Max(MinRadius, Math.Min(area.Width, area.Height) * 0.18);
        }

        radius = Math.Clamp(radius, MinRadius, Math.Max(MinRadius, Math.Min(area.Width, area.Height) * MaxRadiusRatio));
        double angle = Math.Atan2(dy, dx) + (AngularSpeed(radius) * Math.Max(0, seconds));

        double x = center.X + (radius * Math.Cos(angle));
        double y = center.Y + (radius * Math.Sin(angle) * Flatten);
        return new PetPoint(
            Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width)),
            Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height)));
    }
}
