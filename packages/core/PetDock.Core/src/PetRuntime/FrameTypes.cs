// docs/11 §2.1 / §3.2 / §8：帧（宿主与 IPetView 之间传递的唯一绘制单元）与资源所有权。
// 位图句柄由宿主（core 的 registry）发放、宿主在帧同步点释放；插件从不 new 窗口、不自己发句柄。
// 预乘 alpha 的 32bpp：GDI+ 的 Format32bppPArgb 就是预乘格式，registry 只接受它，
// 非预乘输入在 Acuire 处立即被拒（拒规则见 GetFrameContent 的调用方：一帧都没有）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace PetDock.Core.PetRuntime;

/// <summary>
/// docs/11 §2.1 `Frame.bitmapHandle` 的宿主侧形态：一张已登记的位图 + 它的物理像素尺寸。
/// 句柄存活到宿主下一次 Release 为止；帧同步点之后引用它就废（宿主拥有所有权）。
/// </summary>
public sealed class BitmapHandle
{
    internal BitmapHandle(long id, Bitmap bitmap)
    {
        Id = id;
        Bitmap = bitmap;
    }

    /// <summary>句柄编号（审计与自检用，单调递增，不复用）。</summary>
    public long Id { get; }

    /// <summary>位图本体。格式恒为 Format32bppPArgb（构造处强制）。</summary>
    public Bitmap Bitmap { get; }

    /// <summary>物理像素尺寸（帧缓冲取图的尺寸，不是 docs/11 的逻辑像素 Size）。</summary>
    public int PixelWidth => Bitmap.Width;

    public int PixelHeight => Bitmap.Height;

    /// <summary>物理像素总数——hitmap 的长度必须是它（docs/11 §2.2 与位图同尺寸）。</summary>
    public int PixelCount => Bitmap.Width * Bitmap.Height;

    public override string ToString() => $"bitmap#{Id} {PixelWidth}x{PixelHeight}";
}

/// <summary>
/// docs/11 §2.1 Frame。Size 是逻辑像素，scale 是 DPI 缩放；
/// hitmap 是与位图同尺寸的每像素 0|1（1 = 可命中）。clearRect 缺省 = 整帧重绘。
/// </summary>
public sealed record Frame(
    BitmapHandle BitmapHandle,
    PetSize Size,
    double Scale,
    PetRect? ClearRect = null,
    byte[]? Hitmap = null)
{
    /// <summary>
    /// RFC-41 §3.3 的命中阈值：2 的幂（`alpha &gt;&gt; 4 != 0` 即可判），约 6.3% 不透明。
    /// 为什么是 16：实测（RFC-41 §2.4）`alpha ∈ [1,15]` 只有 23 个像素（0.14%），
    /// 而 `alpha ∈ [1,127]` 有 220 个（1.34%）—— 只吃最外那圈抗锯齿边，不啃进轮廓。
    /// </summary>
    public const int HitAlphaThreshold = 16;

    /// <summary>
    /// docs/11 §2.2 的命中仲裁：hitmap &gt; bitmap alpha，且 **alpha === 0 永远放行穿透**。
    /// 两个方向都定性：
    /// - alpha === 0 时永远返回 false（不可命中），无论 hitmap 说什么——穿透区不许被任何 surface 变可点（docs/11:190）；
    /// - hitmap 缺失时退回读预乘位图的 alpha 通道，并按 <see cref="HitAlphaThreshold"/> 阈值化
    ///   （RFC-41 §3.3：`alpha ∈ [1,15]` 由「命中」改为「穿透」，这是一次语义变更）；
    /// - hitmap 存在时先按 alpha===0 放行，其余按 hitmap 的 0|1 判定（hitmap 仍然优先于阈值）。
    /// hitmap 长度与位图物理像素数不一致时抛 <see cref="ArgumentException"/>：
    /// 长度不对的命中图是插件写坏了，不许静默当命中（那会把整窗变成可点）。
    /// </summary>
    public bool HitsAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= BitmapHandle.PixelWidth || y >= BitmapHandle.PixelHeight)
        {
            return false; // 帧外：穿透
        }

        byte alpha = AlphaAt(x, y);
        if (alpha == 0)
        {
            return false;
        }

        if (Hitmap is null)
        {
            return alpha >= HitAlphaThreshold;
        }

        int index = (y * BitmapHandle.PixelWidth) + x;
        if (index >= Hitmap.Length)
        {
            throw new ArgumentException(
                $"hitmap length {Hitmap.Length} < pixel count {BitmapHandle.PixelCount}");
        }

        return Hitmap[index] != 0;
    }

    /// <summary>预乘位图某点的 alpha。越界返回 0（穿透）。</summary>
    public byte AlphaAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= BitmapHandle.PixelWidth || y >= BitmapHandle.PixelHeight)
        {
            return 0;
        }

        unsafe
        {
            BitmapData data = BitmapHandle.Bitmap.LockBits(
                new Rectangle(x, y, 1, 1), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                byte* p = (byte*)data.Scan0.ToPointer();
                return p[3];
            }
            finally
            {
                BitmapHandle.Bitmap.UnlockBits(data);
            }
        }
    }

    /// <summary>诊断行（自检打印用，不含像素本体）。</summary>
    public string Describe() =>
        $"bitmap={BitmapHandle} size={Size} scale={PetPoint.Fmt(Scale)}"
        + $" clear={ClearRect?.ToString() ?? "full"} hitmap={(Hitmap is null ? "alpha" : $"{Hitmap.Length}B")}";
}

/// <summary>
/// docs/11 §7 背压通道：宿主交给 IPetView 的自愿降级预算
/// （`ctx.resources.budget` 允许它降帧、减分辨率）。只读快照。
/// </summary>
public readonly record struct ResourceBudget(double TargetFrameMs, double IdleFrameMs, int MaxFramePixels)
{
    public override string ToString() =>
        $"frame<{PetPoint.Fmt(TargetFrameMs)}ms idle<{PetPoint.Fmt(IdleFrameMs)}ms px<={MaxFramePixels}";
}

/// <summary>
/// docs/11 §3 `attach(target: RenderTarget)`：宿主给插件的画板。
/// 宿主拥有 RenderTarget 与桌面窗口（docs/11 §8），插件只拿它的引用。
/// </summary>
public sealed class RenderTarget
{
    internal RenderTarget(int pixelWidth, int pixelHeight, double scale, ResourceBudget budget, BitmapRegistry bitmaps)
    {
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Scale = scale;
        Budget = budget;
        Bitmaps = bitmaps;
    }

    /// <summary>画板物理尺寸（帧缓冲尺寸，DPI 缩放后）。</summary>
    public int PixelWidth { get; }

    /// <summary>画板物理高度。</summary>
    public int PixelHeight { get; }

    /// <summary>画板 DPI 缩放（1.0 = 96dpi）。</summary>
    public double Scale { get; }

    /// <summary>宿主预算（降帧 / 减分辨率的依据）。</summary>
    public ResourceBudget Budget { get; }

    /// <summary>宿主位图 registry：插件用它把一张预乘位图交给宿主。</summary>
    public BitmapRegistry Bitmaps { get; }

    public override string ToString() =>
        $"target {PixelWidth}x{PixelHeight} scale={PetPoint.Fmt(Scale)} {Budget}";
}

/// <summary>
/// 宿主侧位图 registry（docs/11 §8 的资源所有权表：位图句柄由宿主分配、宿主在帧同步点后释放）。
/// 单线程使用（宿主帧循环线程）。每个 acquire 换一个句柄编号；release 之后句柄失效。
/// </summary>
public sealed class BitmapRegistry
{
    private readonly Dictionary<long, Bitmap> _live = new();
    private long _next = 1;

    /// <summary>已发放且未释放的句柄数（自检看它回零：没有泄漏 = 没有悬空位图）。</summary>
    public int LiveCount => _live.Count;

    /// <summary>累计发放的句柄数（单调计数器）。</summary>
    public long Acquired { get; private set; }

    /// <summary>累计释放的句柄数。</summary>
    public long Released { get; private set; }

    /// <summary>
    /// 登记一张位图并返回句柄。只接受预乘 32bpp（docs/11:49 硬要求）；
    /// 其他格式立即抛出——接受一张非预乘位图会让整只宠物在屏幕上变成错误亮度，
    /// 而且不报任何错（UpdateLayeredWindow 按 AC_SRC_ALPHA 解释，非预乘图直接偏色）。
    /// </summary>
    public BitmapHandle Acquire(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.PixelFormat != PixelFormat.Format32bppPArgb)
        {
            throw new ArgumentException(
                $"docs/11 §2.1 要求预乘 alpha 的 32bpp，实际 {bitmap.PixelFormat}", nameof(bitmap));
        }

        long id = _next++;
        _live[id] = bitmap;
        Acquired++;
        return new BitmapHandle(id, bitmap);
    }

    /// <summary>释放一个句柄（宿主帧同步点后调用）。未登记/已释放的句柄为无害 no-op。</summary>
    public void Release(BitmapHandle? handle)
    {
        if (handle is null)
        {
            return;
        }

        if (_live.Remove(handle.Id))
        {
            Released++;
        }
    }

    /// <summary>释放全部存活句柄（实例下线时的兜底）。</summary>
    public void ReleaseAll()
    {
        while (_live.Count > 0)
        {
            long first = long.MaxValue;
            foreach (long id in _live.Keys)
            {
                if (id < first)
                {
                    first = id;
                }
            }
            _live.Remove(first);
            Released++;
        }
    }
}
