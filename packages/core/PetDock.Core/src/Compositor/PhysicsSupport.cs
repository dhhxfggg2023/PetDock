// 抛掷落点选择（2026-10-29）：屏幕地板 + 别的窗口顶边。
//
// 抽成纯函数是因为这是唯一能无人值守判的部分：真实落点的手感要人看，而「这次该落在哪个面上」
// 是可以拿几个矩形算清楚的。物理实现只负责把结果写回窗口位置。
//
// 两条判据：
//   1. 水平至少 1/3 站在面上才算「站上去」—— 蹭到一条窗边就停住会看起来像卡在空气里；
//   2. 只认「这次下落跨过」的顶边（previousBottom ≤ top < newBottom），
//      否则宠物会从下方被吸到上方的家具上。
using System;
using System.Collections.Generic;
using System.Drawing;

namespace PetDock.Core.Compositor;

internal static class PhysicsSupport
{
    /// <summary>站在面上所需的最小重叠比例（占宠物宽度的三分之一）。</summary>
    private const int RequiredOverlapDivisor = 3;

    /// <summary>顶部容差：分数像素与取整误差不能把「刚好贴着」判成「在上面」。</summary>
    private const int TopTolerance = 1;

    /// <summary>
    /// 找本次下落第一个碰到的支撑面顶边（屏幕 Y）。返回 true = 有面可落。
    /// <paramref name="pet"/> 是这一帧提议的窗口矩形（水平位置已更新）；person 的高度不参与计算。
    /// </summary>
    public static bool TryFind(
        Rectangle pet,
        int previousBottom,
        int newBottom,
        Rectangle area,
        IReadOnlyList<Rectangle> surfaces,
        out int supportTop)
    {
        supportTop = 0;
        int best = int.MaxValue;
        foreach (Rectangle surface in surfaces)
        {
            int top = surface.Top;
            if (top <= area.Top)
            {
                // 贴着屏幕顶边的面没有落差：当成屏幕边界处理，否则宠物会「落」到屏幕外。
                continue;
            }

            if (Overlap(pet, surface) < pet.Width / RequiredOverlapDivisor)
            {
                continue;
            }

            if (previousBottom > top + TopTolerance)
            {
                // 之前已经在面里（从侧面撞上去），不是从上方落下来。
                continue;
            }

            if (newBottom < top)
            {
                // 还没到。
                continue;
            }

            if (top < best)
            {
                best = top;
            }
        }

        if (best == int.MaxValue)
        {
            return false;
        }

        supportTop = best;
        return true;
    }

    /// <summary>
    /// 松手（或任何「这一刻脚下有没有东西」的问法）时，这块矩形站住了吗？
    /// 与 <see cref="TryFind"/> 的区别：那个回答「这次下落该停在哪条边」，本方法回答
    /// 「现在就已经站在什么上面」—— 屏幕地板算站住，别的窗口顶边要**贴着**且水平重叠 ≥ 1/3。
    /// 为什么需要它：松手时宠物可能停在半空（拖到屏幕中间放下），旧实现只在「甩出去」时进物理，
    /// 于是它悬在那儿永不落下 —— 用户说的「重力没考虑能不能下落」就是这一条。
    /// </summary>
    public static bool TryFindSupport(
        Rectangle pet,
        Rectangle area,
        IReadOnlyList<Rectangle>? surfaces,
        out int supportTop)
    {
        supportTop = int.MinValue;
        if (Math.Abs(pet.Bottom - area.Bottom) <= TopTolerance)
        {
            supportTop = area.Bottom;
            return true;
        }

        if (surfaces is null)
        {
            return false;
        }

        int best = int.MaxValue;
        foreach (Rectangle surface in surfaces)
        {
            if (Math.Abs(surface.Top - pet.Bottom) > TopTolerance)
            {
                continue;
            }

            if (Overlap(pet, surface) < pet.Width / RequiredOverlapDivisor)
            {
                continue;
            }

            if (surface.Top < best)
            {
                best = surface.Top;
            }
        }

        if (best == int.MaxValue)
        {
            return false;
        }

        supportTop = best;
        return true;
    }

    /// <summary>当前这个矩形在这条顶边上还有没有支撑（走出边缘 / 窗口被关掉都返回 false）。</summary>
    public static bool HasSupport(Rectangle pet, int supportTop, IReadOnlyList<Rectangle> surfaces)
    {
        // 底边必须还贴着这条顶边：被挪走/被抬高之后，下面那块面就不再是「脚下」了。
        if (Math.Abs(pet.Bottom - supportTop) > 2)
        {
            return false;
        }

        foreach (Rectangle surface in surfaces)
        {
            if (Math.Abs(surface.Top - supportTop) > TopTolerance)
            {
                continue;
            }

            if (Overlap(pet, surface) >= pet.Width / RequiredOverlapDivisor)
            {
                return true;
            }
        }

        return false;
    }

    private static int Overlap(Rectangle pet, Rectangle surface) =>
        Math.Min(pet.Right, surface.Right) - Math.Max(pet.Left, surface.Left);
}
