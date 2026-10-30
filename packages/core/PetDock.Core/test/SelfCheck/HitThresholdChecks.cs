// RFC-41 的判据体：命中阈值（alpha >= 16）与「内容源申报的命中面真的被消费」。
// 命令形态 `--uitest`，判据行 `CHECK hit-threshold-*` / `hit-surface-wired*` / `ui-hit-tier1-consumes`。
//
// 为什么阈值那四条不建窗口：它们判的是 Frame.HitsAt 的纯函数语义（RFC-41 §4.1）——
// 没有窗口就没有 Z 序、没有遮挡、没有消息泵，可在无人值守机器上重复跑。
// 窗口那一条只回答 §4.1 答不了的那个问题：**有人消费这张命中面吗**（RFC-41 §4.2）。
//
// 判定通道与 RFC-41 §4.2 的原文不一致，理由来自已定案的排障报告
// 交接档案 §5 第 1、3 条：§4.2 要求用 WindowFromPoint，
// 但那条路只按 alpha == 0 过滤（同报告 §3.2 Phase B vs H：全透明帧落到下层，不透明帧返回自己），
// 于是 alpha = 8 在它眼里与不透明像素没有区别 —— 阈值在这条通道上**永远判不出来**。
// 本判据因此用 Win32.HitTest（即 SendMessage(WM_NCHITTEST)）：窗口侧 WndProc 一旦拿到
// IFrameHitSurface 就按它答话（同报告 §3.1 H7 / Phase I 实测 -1），只有这条路带得动阈值。
// WindowFromPoint 的读数一并打印，只作诊断、不当判据。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

internal static class HitThresholdChecks
{
    /// <summary>判据出口（与 HitMapChecks/SettingsChecks 同形）。</summary>
    internal delegate void Report(string name, bool ok, string detail);

    /// <summary>WM_NCHITTEST 的两种答案（判据直接比对这两个数，不读实现里的常量）。</summary>
    private const int HtClient = 1;
    private const int HtTransparent = -1;

    /// <summary>RFC-41 §4.1 的测试帧边长（16×16，照任务描述的形状）。</summary>
    private const int FrameSide = 16;

    /// <summary>中心 4×4 的起止（含）。</summary>
    private const int CentreFrom = 6;
    private const int CentreTo = 9;

    public static void Run(Report check)
    {
        try
        {
            var registry = new BitmapRegistry();
            using Bitmap bitmap = Premultiplied(FrameSide, FrameSide, AlphaMap);
            BitmapHandle handle = registry.Acquire(bitmap);
            Frame frame = new(handle, new PetSize(FrameSide, FrameSide), 1.0);

            TransparentFivePoints(check, frame);
            FringePasses(check, frame);
            AtThresholdHits(check, frame);
            AlphaZeroStillPasses(check, handle);
            SurvivesBitmapDispose(check);
            SurfaceWired(check);
            Tier1Consumes(check);
        }
        catch (Exception error)
        {
            check("hit-threshold-crash", false, $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// RFC-41 §4.1 的测试帧：中心 4×4 全不透明，其余全透明 —— **除了三点钉住阈值边界**：
    /// (2,2)=8（几乎透明）、(3,3)=16（恰好到阈值）、(4,3)=15（差一）。
    /// 三点都在中心 4×4 之外，所以四条判据可以共用这一张帧，失败仍能归到各自的判据行。
    /// </summary>
    private static byte AlphaMap(int x, int y)
    {
        if (x >= CentreFrom && x <= CentreTo && y >= CentreFrom && y <= CentreTo)
        {
            return 255;
        }

        if (x == 2 && y == 2)
        {
            return 8;
        }

        if (x == 3 && y == 3)
        {
            return 16;
        }

        return x == 4 && y == 3 ? (byte)15 : (byte)0;
    }

    /// <summary>判据 1：四角透明 → 穿透；中心 (8,8) → 命中。</summary>
    private static void TransparentFivePoints(Report check, Frame frame)
    {
        bool corners = !frame.HitsAt(0, 0) && !frame.HitsAt(15, 0)
            && !frame.HitsAt(0, 15) && !frame.HitsAt(15, 15);
        bool centre = frame.HitsAt(8, 8);
        check("hit-threshold-transparent-five-points", corners && centre,
            $"corner(0,0)={frame.HitsAt(0, 0)} (15,0)={frame.HitsAt(15, 0)}"
            + $" (0,15)={frame.HitsAt(0, 15)} (15,15)={frame.HitsAt(15, 15)} centre(8,8)={centre}");
    }

    /// <summary>判据 2：alpha=8 必须穿透（**本片的行为变更证据**：改前这里是 true）。</summary>
    private static void FringePasses(Report check, Frame frame)
    {
        bool hit = frame.HitsAt(2, 2);
        check("hit-threshold-fringe-passes", !hit,
            $"alpha={frame.AlphaAt(2, 2)} (2,2)={hit} expect=False"
            + $" threshold={Frame.HitAlphaThreshold} source=thresholded-alpha");
    }

    /// <summary>判据 3：边界方向 —— 16 命中、15 不命中（`>=` 与 `>` 写反的经典错误）。</summary>
    private static void AtThresholdHits(Report check, Frame frame)
    {
        bool at = frame.HitsAt(3, 3);
        bool below = frame.HitsAt(4, 3);
        check("hit-threshold-at-threshold-hits", at && !below,
            $"alpha16(3,3)={at} expect=True alpha15(4,3)={below} expect=False");
    }

    /// <summary>
    /// 判据 4：阈值不得侵蚀 alpha == 0 的绝对穿透 —— 给一条全 1 的 hitmap，
    /// (5,5) 仍必须穿透（原规格 11:190）。正控 (8,8) 证明这条路真的读了 hitmap。
    /// </summary>
    private static void AlphaZeroStillPasses(Report check, BitmapHandle handle)
    {
        var all = new byte[FrameSide * FrameSide];
        Array.Fill(all, (byte)1);
        Frame withMap = new(handle, new PetSize(FrameSide, FrameSide), 1.0, null, all);
        bool zero = withMap.HitsAt(5, 5);
        bool control = withMap.HitsAt(8, 8);
        check("hit-threshold-alpha-zero-still-passes", !zero && control,
            $"alpha={withMap.AlphaAt(5, 5)} (5,5)={zero} expect=False hitmap=all-1"
            + $" control(8,8)={control} expect=True");
    }

    /// <summary>
    /// 命中测试不得在 UI 线程碰 GDI+ 位图：CacheAlpha 之后即使位图被另一线程 dispose，
    /// PixelWidth/PixelHeight 与 alpha 平面仍是普通字段/数组，HitsAt 必须照常回答。
    /// 防的是真实弹框的根因（Image.get_Width → Object is currently in use elsewhere）。
    /// </summary>
    private static void SurvivesBitmapDispose(Report check)
    {
        var registry = new BitmapRegistry();
        Bitmap bitmap = Premultiplied(FrameSide, FrameSide, AlphaMap);
        BitmapHandle handle = registry.Acquire(bitmap);
        Frame frame = new(handle, new PetSize(FrameSide, FrameSide), 1.0);
        frame.CacheAlpha();
        int width = handle.PixelWidth;
        int height = handle.PixelHeight;
        bitmap.Dispose();

        bool centre;
        bool corner;
        try
        {
            centre = frame.HitsAt(8, 8);
            corner = frame.HitsAt(0, 0);
        }
        catch (Exception error)
        {
            check("hit-threshold-survives-bitmap-dispose", false,
                $"{error.GetType().Name}: {error.Message}");
            return;
        }

        check("hit-threshold-survives-bitmap-dispose", centre && !corner,
            $"centre={centre} corner={corner} size={width}x{height} source=cached-alpha");
    }

    /// <summary>
    /// RFC-41 §3.1(b) 的接线自证：生产类型真的申报了命中面，且代理**问当前那一份**。
    /// 为什么单列一条：§4.2 的窗口判据用的是判据自己的桩，它证明「窗口 ↔ surface」那条链，
    /// 证明不了生产类型接上了（M4 的教训：夹具全绿不等于生产接线）。
    /// </summary>
    private static void SurfaceWired(Report check)
    {
        bool sourceDeclared = typeof(PetFrameSource).GetInterface(nameof(IFrameHitSurface)) is not null;
        bool proxyDeclared = typeof(SwappableFrameSource).GetInterface(nameof(IFrameHitSurface)) is not null;
        check("hit-surface-wired-declared", sourceDeclared && proxyDeclared,
            $"pet-frame-source={sourceDeclared} proxy={proxyDeclared} abi={nameof(IFrameHitSurface)}");

        var first = new Tier1Stub(width => width / 2);
        var second = new Tier1Stub(width => (width * 3) / 4);
        using var proxy = new SwappableFrameSource(first);
        var size = new Size(FrameSide, FrameSide);
        proxy.GetFrameContent(size);
        bool before = proxy.Hits(10, 8);
        proxy.Replace(second);
        first.Dispose();
        proxy.GetFrameContent(size);
        bool after = proxy.Hits(10, 8);
        check("hit-surface-wired-forwards", !before && after,
            $"proxy(x=10) before={before} expect=False after={after} expect=True（换源后必须跟着走）");
    }

    /// <summary>
    /// RFC-41 §4.2：常驻路径真的消费内容源申报的命中面。
    /// 桩**原样转发** Frame.HitsAt，所以这一条测的是「阈值化 alpha 经 surface 到窗口」整条链。
    /// </summary>
    private static void Tier1Consumes(Report check)
    {
        using var source = new Tier1Stub(width => width / 2);
        using var window = new LayeredFrameWindow(source);
        window.Show();
        window.BringToFrontOfTopmost();
        window.PresentFirstFrame();
        Application.DoEvents();

        (int X, int Y) pos = window.CurrentPosition;
        Size size = window.GetPhysicalSize();
        int y = pos.Y + (size.Height / 2);
        var left = new POINT { X = pos.X + (size.Width / 4), Y = y };
        var right = new POINT { X = pos.X + ((size.Width * 3) / 4), Y = y };

        int leftCode = Win32.HitTest(window.WindowHandle, left);
        int rightCode = Win32.HitTest(window.WindowHandle, right);
        bool ok = leftCode == HtClient && rightCode == HtTransparent;
        check("ui-hit-tier1-consumes", ok,
            $"left={leftCode} expect={HtClient} alpha=255 right={rightCode} expect={HtTransparent} alpha=8"
            + $" wfp-left={OnTop(window, left)} wfp-right={OnTop(window, right)}");
    }

    /// <summary>
    /// 真实路由那条通道的读数（诊断用，不进判据）：WindowFromPoint 只按 alpha == 0 过滤，
    /// 所以 alpha = 8 的右点仍然解析到本窗 —— 这正是 §4.2 原文那条期望落不了地的原因。
    /// </summary>
    private static string OnTop(LayeredFrameWindow window, POINT point) =>
        Win32.TopLevelWindowFromPoint(point) == window.WindowHandle ? "self" : Win32.ClassNameAtPoint(point);

    /// <summary>
    /// RFC-41 §4.2 的 tier-1 形状桩：左半 alpha = 255、右半 alpha = 8，**都不给 Hitmap**
    /// （于是 Frame.HitsAt 走「Hitmap is null → 阈值化 alpha」那条分支）。
    /// Hits **原样转发** frame.HitsAt —— 桩自己不算命中语义：自算测到的是桩的算法，
    /// 而 Frame.HitsAt 才是产品的唯一仲裁者（原规格 11:56）。
    /// </summary>
    private sealed class Tier1Stub : IFrameContentSource, IFrameHitSurface
    {
        private readonly Func<int, int> _opaqueWidthOf;
        private readonly BitmapRegistry _registry = new();
        private Bitmap? _bitmap;
        private Frame? _frame;

        public Tier1Stub(Func<int, int> opaqueWidthOf) => _opaqueWidthOf = opaqueWidthOf;

        public Bitmap? GetFrameContent(Size frameSize)
        {
            _registry.ReleaseAll();
            _bitmap?.Dispose();
            int opaque = _opaqueWidthOf(frameSize.Width);
            _bitmap = Premultiplied(frameSize.Width, frameSize.Height,
                (x, _) => x < opaque ? (byte)255 : (byte)8);
            _frame = new Frame(
                _registry.Acquire(_bitmap),
                new PetSize(frameSize.Width, frameSize.Height),
                1.0);
            return _bitmap;
        }

        /// <summary>没有帧时返回 false（fail-closed：宁可点不到，也不许误点）。</summary>
        public bool Hits(int x, int y) => _frame is not null && _frame.HitsAt(x, y);

        public void Dispose()
        {
            _registry.ReleaseAll();
            _bitmap?.Dispose();
        }
    }

    /// <summary>
    /// 按 alpha 图造一张预乘 32bpp 位图，RGB 一并预乘成同值（白）。
    /// 为什么不用 SetPixel：预乘位图上 SetPixel 的预乘语义依 GDI+ 实现而定，
    /// 而这里要钉的是**字节 3 恰好等于给定的 alpha**，一点都不能含糊。
    /// </summary>
    private static Bitmap Premultiplied(int width, int height, Func<int, int, byte> alphaAt)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < height; y++)
                {
                    byte* row = (byte*)data.Scan0.ToPointer() + (y * data.Stride);
                    for (int x = 0; x < width; x++)
                    {
                        byte a = alphaAt(x, y);
                        row[x * 4] = a;
                        row[(x * 4) + 1] = a;
                        row[(x * 4) + 2] = a;
                        row[(x * 4) + 3] = a;
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }
}
