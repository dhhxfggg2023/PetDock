// PetInstance 的帧半边（partial 的第二块）：取帧、占位图降级、命中判定、帧超时挂起。
// 判据与出处：
//   docs/11 §2.1 Frame（预乘 32bpp、hitmap 可选）
//   docs/11 §2.2 命中仲裁：hitmap > bitmap alpha，且 alpha===0 永远放行穿透
//   docs/11 §7   帧预算（8.3 / 33.3 / 25ms）与「连续 8 次超时 → suspend」
//   docs/11 §8   资源所有权：宿主在帧同步点后释放句柄
//   docs/11 §9   QueryFrame 抛异常 → 该帧 dropped；连续 8 次 → 降级占位图
using System;
using System.Drawing;
using System.Drawing.Imaging;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

/// <summary>一只宠物实例的帧面。</summary>
public sealed partial class PetInstance
{
    /// <summary>当前帧（最近一次成功取到的帧；降级到占位图之后是占位帧）。</summary>
    public Frame? CurrentFrame { get; internal set; }

    /// <summary>累计交付给合成的帧数（含占位帧）。</summary>
    public long TotalFrames => Frames;

    /// <summary>
    /// 本帧与上一帧是否**同一个位图对象** —— docs/11:225「无内容变化时」的空闲档判据。
    /// 判据是对象身份而不是像素比较：像素比较要逐像素扫全帧（8.3ms 预算里做不起），
    /// 而「实现方复用了同一张位图」正是「内容没变」可判定、且零成本的等价条件。
    /// </summary>
    public bool LastFrameReused { get; private set; }

    /// <summary>
    /// docs/11 §3.2 / §7：取这一帧。抛异常 = 该帧 dropped —— 调用方**必须**随后调
    /// <see cref="NoteFrameDropped"/>，否则「连续 8 次」的计数无处累加，降级链永远不推进。
    /// 降级到占位图之后本方法改由内核占位帧回答（真实内容已不可信，不再向实现方取帧）。
    /// </summary>
    public Frame QueryFrame(int nowMs)
    {
        if (State != InstanceState.Running)
        {
            throw new InvalidOperationException(I18n.Get("host.pet.err.query-needs-running", State));
        }

        if (Display.Tier >= DisplayTier.Placeholder)
        {
            // §9：降级到占位图之后不再向呈现实现取帧。
            Target ??= new RenderTarget(128, 128, 1.0, default, Bitmaps);
            Bitmap placeholder = PlaceholderFrame.Get(Target.PixelWidth, Target.PixelHeight);
            Frame still = new(
                Bitmaps.Acquire(placeholder),
                new PetSize(Target.PixelWidth, Target.PixelHeight),
                Target.Scale);
            Swap(placeholder, still);
            Frames++;
            LastFailure = $"placeholder tier={Display.Tier}";
            return still;
        }

        Frame frame;
        // RFC-23 §3.3 第 2 点：取帧是调用进插件代码，归属作用域包住它。
        using (PluginLog.Scope(PluginId))
        {
            frame = View.QueryFrame(nowMs);
        }
        CheckFrame(frame);
        Swap(frame.BitmapHandle.Bitmap, frame);
        Frames++;
        return frame;
    }

    /// <summary>
    /// 本帧是否命中某点（docs/11 §2.2 仲裁 + §5:190 的穿透优先）。
    /// 隐藏档一律 false（隐藏的宠物不该被点到）；没有帧时也 false（无内容 = 穿透）。
    /// </summary>
    public bool HitsAt(int x, int y)
    {
        if (Display.Tier >= DisplayTier.Hidden || CurrentFrame is null)
        {
            return false;
        }

        return CurrentFrame.HitsAt(x, y);
    }

    /// <summary>
    /// docs/11 §9：`QueryFrame` 抛异常 → 当前帧标记 dropped；连续 8 次 → 降级到占位图。
    /// 「dropped」的语义是**这一帧没有内容**（调用方据此不合成），不是「沿用上一帧」——
    /// 后者会让「内容早就坏了」在屏幕上看起来完全正常。
    /// </summary>
    public void NoteFrameDropped(Exception error, long atMs = 0)
    {
        FrameDrops++;
        ConsecutiveFrameDrops++;
        LastFailure = $"queryFrame-failed {error.GetType().Name}: {error.Message}";
        if (ConsecutiveFrameDrops >= FrameBudgetGuard.ConsecutiveTimeoutLimit)
        {
            Display.Advance(
                DisplayTier.Placeholder,
                $"{FrameBudgetGuard.ConsecutiveTimeoutLimit} consecutive dropped frames",
                atMs);
            KernelLog.Write($"degrade-placeholder instance={Id} drops={FrameDrops}");
        }
    }

    /// <summary>帧缓冲尺寸（取占位图 / 校验帧尺寸用）；画板尚未分配时返回 0x0。</summary>
    public PetSize FrameSize => Target is null
        ? new PetSize(0, 0)
        : new PetSize(Target.PixelWidth, Target.PixelHeight);

    private void Swap(Bitmap bitmap, Frame frame)
    {
        // docs/11 §8 的所有权表：宿主在**帧同步点之后**释放上一帧的句柄。
        // registry 只注销句柄，不 Dispose 位图对象 —— 位图归资源库/实现方所有（ABI 不给宿主处置权）。
        LastFrameReused = bitmap is not null && ReferenceEquals(bitmap, LastFrameBitmap);
        Bitmaps.Release(_previousHandle);
        _previousHandle = frame.BitmapHandle;
        LastFrameBitmap = bitmap;
        CurrentFrame = frame;
        ConsecutiveFrameDrops = 0;
    }

    private void CheckFrame(Frame frame)
    {
        if (frame.BitmapHandle.PixelWidth <= 0 || frame.BitmapHandle.PixelHeight <= 0)
        {
            throw new ArgumentException(I18n.Get("host.pet.err.empty-bitmap", frame.BitmapHandle));
        }

        if (Target is not null && (Target.PixelWidth != frame.BitmapHandle.PixelWidth
            || Target.PixelHeight != frame.BitmapHandle.PixelHeight))
        {
            string frameSize = $"{frame.BitmapHandle.PixelWidth}x{frame.BitmapHandle.PixelHeight}";
            throw new ArgumentException(I18n.Get("host.pet.err.frame-size-mismatch", frameSize)
                + $" {Target.PixelWidth}x{Target.PixelHeight}");
        }
    }
}

/// <summary>
/// docs/11 §9 的占位图落点：档位 ≥ Placeholder 时宿主用内核占位帧回答。
/// 复用内核兜底图形的像素（SolidFrameContent），只做一次格式转换：
/// 兜底图是 Format32bppArgb，而 docs/11:49 要求 ABI 位图是预乘的 ——
/// 转换交给 GDI+（Clone 到 Format32bppPArgb），不自己逐像素乘。
/// </summary>
internal static class PlaceholderFrame
{
    private static SolidFrameContent? _content;
    private static Bitmap? _cached;
    private static Size _size;

    public static Bitmap Get(int width, int height)
    {
        var size = new Size(width, height);
        if (_cached is not null && _size == size)
        {
            return _cached;
        }

        _content ??= SolidFrameContent.KernelFallback();
        Bitmap? source = _content.GetFrameContent(size);
        _cached?.Dispose();
        _cached = source is null
            ? new Bitmap(width, height, PixelFormat.Format32bppPArgb)
            : source.Clone(new Rectangle(0, 0, width, height), PixelFormat.Format32bppPArgb);
        _size = size;
        return _cached;
    }
}
