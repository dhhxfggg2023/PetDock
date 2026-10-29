// 命中图参与穿透（RFC-31 §3.2）。
//
// 为什么必须自己裁决 WM_NCHITTEST：原规格 12 §2.3 要求「alpha === 0 永远穿透、alpha > 0 才可命中」，
// 同时要求「插件给了 hitmap 就按 hitmap 判」。前一条交给 user32 读 alpha 就够了，后一条做不到
// ——user32 手里只有那张位图，看不见另一张命中图。把 hitmap 直接打进位图 alpha 会**改变外观**
// （本来不透明的地方被挖洞），那是拿渲染换命中，反过来违反 原规格 11:45「hitmap 只影响命中」。
// 所以只有一条路：alpha === 0 的像素 user32 根本不会来问（OS 判定照旧），
// alpha > 0 的像素由这里按当前内容源给出的命中图裁决。
//
// 2026-10-29 增：素材包可以声明命中区（HitBoxes）。声明之后**只在这些框里算命中** ——
// 装饰件（帽子、飘带）不再是可点区域；没声明就退回「内容源的命中图 / OS 的 alpha」。
// 注意：声明没法把命中扩到完全透明的像素上（user32 不会来问），它只能收窄。
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using PetDock.Core.Compositor;

namespace PetDock.Core.Compositor;

internal sealed partial class LayeredFrameWindow
{
    /// <summary>HTCLIENT：该点属于本窗口（可交互）。</summary>
    private const int HitClient = 1;

    /// <summary>HTTRANSPARENT：该点放行给下面的窗口（穿透）。</summary>
    private const int HitTransparent = -1;

    /// <summary>素材包声明的命中区（归一化）。null / 空 = 没声明，按老规则走。</summary>
    internal IReadOnlyList<HitBox>? HitBoxes { get; set; }

    /// <summary>
    /// 命中图裁决。返回 true 表示已经给出答案（调用方不要再走 base.WndProc）；
    /// 返回 false 表示这一问不归这里管（不是 WM_NCHITTEST、没有命中图也没有命中区、或坐标在窗口矩形外），
    /// 由 OS 默认裁决。
    /// </summary>
    private bool TryHitTestPixel(ref Message m)
    {
        if (m.Msg != Win32.WM_NCHITTEST || !TryFramePixel(m.LParam, out int x, out int y, out int width, out int height))
        {
            return false;
        }

        IReadOnlyList<HitBox>? boxes = HitBoxes;
        IFrameHitSurface? surface = _content as IFrameHitSurface;
        if ((boxes is null || boxes.Count == 0) && surface is null)
        {
            return false;
        }

        // 命中测试是 UI 线程上的消息处理：这里绝不能让异常冒到 WinForms 的默认
        // 「Unhandled exception」对话框（用户只会看到反复弹框）。真出了异常就当作
        // 这一点穿透，并把类型与消息留在内核日志里 —— 下一次移动还能恢复。
        try
        {
            bool hit = boxes is null || boxes.Count == 0 || InsideAny(boxes, x, y, width, height);
            if (hit && surface is not null)
            {
                // Hits 内部已经实现了「hitmap > alpha，且 alpha === 0 永远放行」（FrameTypes.cs:61）。
                hit = surface.Hits(x, y);
            }

            m.Result = hit ? (IntPtr)HitClient : (IntPtr)HitTransparent;
        }
        catch (Exception error)
        {
            KernelLog.Write($"hit-test-failed {error.GetType().Name}: {error.Message}");
            m.Result = (IntPtr)HitTransparent;
        }

        return true;
    }

    private static bool InsideAny(IReadOnlyList<HitBox> boxes, int x, int y, int width, int height)
    {
        foreach (HitBox box in boxes)
        {
            if (box.Contains(x, y, width, height))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把 WM_NCHITTEST 的屏幕坐标（lParam 低 16 位 x、高 16 位 y，均为有符号 16 位）换成帧内像素坐标。
    /// 越界返回 false —— 那时候鼠标根本不在窗口矩形里。
    /// </summary>
    private bool TryFramePixel(IntPtr lParam, out int x, out int y, out int width, out int height)
    {
        x = 0;
        y = 0;
        width = 0;
        height = 0;
        long packed = lParam.ToInt64();
        int screenX = (short)(packed & 0xFFFF);
        int screenY = (short)((packed >> 16) & 0xFFFF);

        var rect = Win32.ScreenRect(Handle);
        int localX = screenX - rect.Left;
        int localY = screenY - rect.Top;
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        if (localX < 0 || localY < 0 || localX >= width || localY >= height)
        {
            return false;
        }

        x = localX;
        y = localY;
        return true;
    }
}
