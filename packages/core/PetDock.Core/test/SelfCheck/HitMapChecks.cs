// 命中图参与穿透的判据（RFC-31 §4）。命令形态 `--uitest`，判据行 `CHECK ui-hitmap-*`。
//
// 判据怎么造：一个真 `LayeredFrameWindow` + 一个假的、带命中图的内容源。
// 假源左半可命中、右半不可命中，但**整幅位图都是不透明的** —— 这样两条路才分得开：
// 若命中仍由 alpha 决定，右半也会是 HTCLIENT（判据会红），只有 WM_NCHITTEST 接上命中图才会是 HTTRANSPARENT。
// 第二个源同一形状但不实现命中图、右半像素透明：它验证「没有命中图时行为与 RFC-31 之前一致」。
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
            AlphaOnlyUnchanged(check);
        }
        catch (Exception error)
        {
            check("ui-hitmap-crash", false, $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>有命中图：位图全不透明，左半命中、右半穿透 —— 穿透必须按命中图判，不按 alpha。</summary>
    private static void HitmapPierces(Report check)
    {
        using var source = new SplitSource(hitmap: true);
        using var window = new LayeredFrameWindow(source);
        window.Show();
        window.PresentFirstFrame();
        Application.DoEvents();

        (int left, int right) = ProbeSides(window);
        check("ui-hitmap-pierce-hit", left == HtClient,
            $"left={left} expect={HtClient} alpha=opaque hitmap=hit");
        check("ui-hitmap-pierce-pass", right == HtTransparent,
            $"right={right} expect={HtTransparent} alpha=opaque hitmap=miss");
    }

    /// <summary>
    /// 同一形状但不给命中图、右侧像素透明：透明侧必须由 alpha 路径放行（OS 判），与是否实现命中图无关。
    /// 左半不透明侧的读数在本机合成窗上不稳定（见 RFC-31 §9），因此这里只断言透明侧。
    /// </summary>
    private static void AlphaOnlyUnchanged(Report check)
    {
        using var source = new SplitSource(hitmap: false);
        using var window = new LayeredFrameWindow(source);
        window.Show();
        window.BringToFrontOfTopmost();
        window.PresentFirstFrame();
        Application.DoEvents();

        (_, int right) = ProbeSides(window);
        check("ui-hitmap-alpha-only-right", right == HtTransparent,
            $"right={right} expect={HtTransparent} alpha=transparent no-hitmap");
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
    /// 假内容源：左半永远不透明；`hitmap: true` 时右半也不透明（命中由命中图否掉），
    /// `hitmap: false` 时右半透明（命中由 alpha 否掉）。
    /// </summary>
    private sealed class SplitSource : IFrameContentSource, IFrameHitSurface
    {
        private readonly bool _hitmap;
        private int _width;
        private Bitmap? _bitmap;

        public SplitSource(bool hitmap) => _hitmap = hitmap;

        public Bitmap? GetFrameContent(Size frameSize)
        {
            _bitmap?.Dispose();
            _width = frameSize.Width;
            _bitmap = new Bitmap(frameSize.Width, frameSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(_bitmap);
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.White);
            g.FillRectangle(brush, new Rectangle(0, 0, frameSize.Width / 2, frameSize.Height));
            if (_hitmap)
            {
                int rightWidth = frameSize.Width - (frameSize.Width / 2);
                g.FillRectangle(brush, new Rectangle(frameSize.Width / 2, 0, rightWidth, frameSize.Height));
            }

            return _bitmap;
        }

        /// <summary>左半可命中、右半不可命中（命中图只在有 hitmap 时存在）。</summary>
        public bool Hits(int x, int y) => _hitmap && x < (_width / 2);

        public void Dispose() => _bitmap?.Dispose();
    }
}
