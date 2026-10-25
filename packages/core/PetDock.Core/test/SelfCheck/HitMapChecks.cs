// 命中图参与穿透的判据（RFC-31 §4）。命令形态 `--uitest`，判据行 `CHECK ui-hitmap-*`。
//
// 判据怎么造：一个真 `LayeredFrameWindow` + 一个假的、带命中图的内容源。
// 假源左半可命中、右半不可命中，但**整幅位图都是不透明的** —— 这样两条路才分得开：
// 若命中仍由 alpha 决定，右半也会是 HTCLIENT（判据会红），只有 WM_NCHITTEST 接上命中图才会是 HTTRANSPARENT。
//
// RFC-41 §4.2b 已删掉 `ui-hitmap-alpha-only-*` 整族：那个桩**无条件**实现 IFrameHitSurface，
// 而它的 `Hits` 在 hitmap=false 时对任何坐标都返回 false，于是窗口对左右两点都回 HTTRANSPARENT，
// 判据断言的「右半 == -1」与它宣称的「alpha 路径放行透明像素」不是同一个理由 —— 那是同义反复。
// 它的两处职责由 RFC-41 的判据接管：alpha 语义（含阈值）归 `hit-threshold-*`（纯函数，无窗口），
// 「真路由按 alpha 判」归 `ui-hit-tier1-consumes`（见 HitThresholdChecks.cs）。
using System;
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal static class HitMapChecks
{
    /// <summary>判据出口（与 SettingsChecks/PerfChecks 同形）。</summary>
    internal delegate void Report(string name, bool ok, string detail);

    /// <summary>WM_NCHITTEST 的两种答案（判据直接比对这两个数，不读实现里的常量）。</summary>
    private const int HtClient = 1;
    private const int HtTransparent = -1;

    public static void Run(Report check)
    {
        try
        {
            HitmapPierces(check);
        }
        catch (Exception error)
        {
            check("ui-hitmap-crash", false, $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>有命中图：位图全不透明，左半命中、右半穿透 —— 穿透必须按命中图判，不按 alpha。</summary>
    private static void HitmapPierces(Report check)
    {
        using var source = new SplitSource();
        using var window = new LayeredFrameWindow(source);
        window.Show();
        window.BringToFrontOfTopmost();
        window.PresentFirstFrame();
        Application.DoEvents();

        (int left, int right) = ProbeSides(window);
        check("ui-hitmap-pierce-hit", left == HtClient,
            $"left={left} expect={HtClient} alpha=opaque hitmap=hit");
        check("ui-hitmap-pierce-pass", right == HtTransparent,
            $"right={right} expect={HtTransparent} alpha=opaque hitmap=miss");
    }

    private static (int Left, int Right) ProbeSides(LayeredFrameWindow window)
    {
        (int X, int Y) pos = window.CurrentPosition;
        Size size = window.GetPhysicalSize();
        int y = pos.Y + (size.Height / 2);
        var leftPoint = new POINT { X = pos.X + (size.Width / 4), Y = y };
        var rightPoint = new POINT { X = pos.X + ((size.Width * 3) / 4), Y = y };
        int left = Win32.HitTest(window.WindowHandle, leftPoint);
        int right = Win32.HitTest(window.WindowHandle, rightPoint);
        return (left, right);
    }

    /// <summary>
    /// 假内容源：**整幅位图都不透明**（两半都白），命中只由命中图决定 ——
    /// 左半可命中、右半不可命中。位图刻意不留透明像素：这样「右半穿透」只可能来自命中图。
    /// </summary>
    private sealed class SplitSource : IFrameContentSource, IFrameHitSurface
    {
        private int _width;
        private Bitmap? _bitmap;

        public Bitmap? GetFrameContent(Size frameSize)
        {
            _bitmap?.Dispose();
            _width = frameSize.Width;
            _bitmap = new Bitmap(frameSize.Width, frameSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(_bitmap);
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.White);
            g.FillRectangle(brush, new Rectangle(0, 0, frameSize.Width, frameSize.Height));
            return _bitmap;
        }

        /// <summary>左半可命中、右半不可命中。</summary>
        public bool Hits(int x, int y) => x < (_width / 2);

        public void Dispose() => _bitmap?.Dispose();
    }
}
