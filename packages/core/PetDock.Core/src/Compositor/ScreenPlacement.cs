// 窗口摆放的纯策略（RFC-22 §3.4）：只回答「这个位置还看得见吗」。
//
// 为什么单独一个文件、为什么没有任何 Win32 调用：本文件要能在**没有桌面**的环境里被判据断言
// （--uitest 在无人值守机器与 CI 上也要跑）。屏幕对象由调用方取好、以 Rectangle 传进来，
// 这里只做几何。
using System;
using System.Collections.Generic;
using System.Drawing;

namespace PetDock.Core.Compositor;

internal static class ScreenPlacement
{
    /// <summary>
    /// 左上角为 <paramref name="topLeft"/>、尺寸为 <paramref name="size"/> 的矩形，
    /// 落在工作区里的面积占整窗面积的比例（0..1）。全部在屏外、或尺寸非正时返回 0。
    ///
    /// 口径是**工作区**（不含任务栏）而不是 `Bounds`：把宠物恢复到「只露出任务栏压住的那一条」
    /// 等于丢了（用户点不到）。多屏时对每个工作区分别求交后累加，最后钳到 1
    /// （屏幕矩形理论上不重叠，累加只是防御）。
    /// </summary>
    public static double VisibleRatio(Point topLeft, Size size, IEnumerable<Rectangle> workAreas)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return 0;
        }

        var window = new Rectangle(topLeft, size);
        double windowArea = (double)size.Width * size.Height;
        double visible = 0;
        foreach (Rectangle area in workAreas)
        {
            Rectangle hit = Rectangle.Intersect(window, area);
            if (hit.IsEmpty)
            {
                continue;
            }

            visible += (double)hit.Width * hit.Height;
        }

        return Math.Min(1.0, visible / windowArea);
    }

    /// <summary>有没有任何一点落在工作区里。判据「可见」的最小定义 = 比例 &gt; 0。</summary>
    public static bool IsVisible(Point topLeft, Size size, IEnumerable<Rectangle> workAreas) =>
        VisibleRatio(topLeft, size, workAreas) > 0;
}
