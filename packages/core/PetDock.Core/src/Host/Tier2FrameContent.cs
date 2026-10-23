// RFC-28 §3.2-§3.3：把一个 tier-2 句柄的帧面变成内核能用的内容源。
//
// 三件事：按帧缓冲尺寸出图（左上角对齐、不缩放）、把预乘 BGRA 还原成 Format32bppArgb、
// 把命中图交出去（IFrameHitSurface）。失败一律返回 null → 宿主退回纯色兜底帧，
// 窗口仍然可见、仍然可命中（FrameSource.cs:3-4 的同一纪律）；判据的非 0 由自检给，不由这里给。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PetDock.Core.Compositor;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

internal sealed class Tier2FrameContent :
    IFrameContentSource,
    IFrameSourceStats,
    IFrameContentInspector,
    IFrameReuseInspector,
    IFrameHitSurface
{
    private readonly string _pluginId;
    private readonly Tier2Sandbox _sandbox;
    private readonly long _startedAt = Environment.TickCount64;
    private Tier2Frame? _last;

    public Tier2FrameContent(string pluginId, Tier2Sandbox sandbox)
    {
        _pluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
    }

    /// <summary>装配说明（内核原样转述，不解释）。</summary>
    public string Note =>
        $"tier2 plugin={_pluginId} pid={_sandbox.Pid} abi={_sandbox.Hello?.Id ?? "(none)"}";

    /// <summary>累计交付的帧数（RFC-28 §4 判据 3）。</summary>
    public long FramesServed { get; private set; }

    /// <summary>交付过至少一帧才算「真内容」（FrameSource.cs:37-41 的同一判据）。</summary>
    public bool DeliversRealContent => FramesServed > 0;

    /// <summary>tier-2 的 wire 帧号严格递增（RFC-27 §3.2），因此这里恒 false（RFC-28 §3.2 第 4 条）。</summary>
    public bool LastFrameReused => false;

    /// <summary>最近一帧的命中判定（`docs/11 §2.2`：命中图优先，否则看 alpha）。</summary>
    public bool Hits(int x, int y) => _last is not null && _last.Hits(x, y);

    public Bitmap? GetFrameContent(Size frameSize)
    {
        int nowMs = (int)Math.Min(int.MaxValue, Environment.TickCount64 - _startedAt);
        if (!_sandbox.TryQueryFrame(nowMs, out Tier2Frame? frame, out _) || frame is null)
        {
            return null;
        }

        _last = frame;
        FramesServed++;
        return Blit(frame, frameSize);
    }

    /// <summary>
    /// 把 wire 帧搬到一张帧缓冲尺寸的位图上：左上角对齐、不缩放，其余区域全透明。
    /// 像素按 RFC-28 §3.2 第 3 条做预乘还原（wire 上预乘、内容源契约非预乘）。
    /// </summary>
    private static Bitmap Blit(Tier2Frame frame, Size frameSize)
    {
        int width = frameSize.Width > 0 ? frameSize.Width : 1;
        int height = frameSize.Height > 0 ? frameSize.Height : 1;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            int stride = data.Stride;
            var buffer = new byte[stride * height];
            int columns = Math.Min(frame.Width, width);
            int rows = Math.Min(frame.Height, height);
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < columns; x++)
                {
                    int from = ((y * frame.Width) + x) * 4;
                    int to = (y * stride) + (x * 4);
                    byte alpha = frame.PremultipliedBgra[from + 3];
                    buffer[to + 0] = Unpremultiply(frame.PremultipliedBgra[from + 0], alpha);
                    buffer[to + 1] = Unpremultiply(frame.PremultipliedBgra[from + 1], alpha);
                    buffer[to + 2] = Unpremultiply(frame.PremultipliedBgra[from + 2], alpha);
                    buffer[to + 3] = alpha;
                }
            }

            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    /// <summary>预乘 → 直通的还原；a=0 时值必须为 0（乘法逆元不存在）。</summary>
    private static byte Unpremultiply(byte value, byte alpha)
    {
        if (alpha == 0 || value == 0)
        {
            return 0;
        }

        int restored = ((value * 255) + (alpha / 2)) / alpha;
        return (byte)Math.Min(255, restored);
    }

    public void Dispose()
    {
        // 沙箱归句柄所有（PluginHandle.Tier2），这里不 Dispose 进程。
    }
}
