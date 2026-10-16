// 逐像素比对（docs/41 D-12a）。容差常量见 GoldenConfig.cs（t1 契约 §6.2 的落点）。
//
// 算法（docs/41:84 逐字）：
//   逐像素取 RGB 三通道各自差的绝对值，任一通道 > 4 即该像素超阈值；
//   超阈值像素数 / 总像素数 > 0.005 即失败。alpha 通道不参与容差比较。
// 尺寸不同的两张图必然失败（pixel-differs = 全像素数），不做缩放对齐。
//
// 差异图：基准与实际的放大差异（diff = |a-b| * 8，饱和到 255），供人工核对（D-12d）。
using System.Drawing;
using System.Drawing.Imaging;

namespace PetDock.Golden.Tests;

internal static class PixelDiffer
{
    public sealed record Report(
        int Width,
        int Height,
        int TotalPixels,
        int PixelDiffers,     // 任一通道理差 > PixelTolerance 的像素数
        int MaxChannelDelta,  // 全图最大的单通道绝对差（诊断用）
        double OutlierRatio)  // PixelDiffers / TotalPixels
    {
        public bool Pass =>
            PixelDiffers == 0
            || (MaxChannelDelta <= GoldenConfig.PixelTolerance
                && OutlierRatio <= GoldenConfig.TotalTolerance);
    }

    /// <summary>把 expected 渲染成 baseline、actual 渲染成 candidate，逐像素比对并返回报告。</summary>
    public static Report Compare(Bitmap expected, Bitmap actual)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            return new Report(
                Math.Min(expected.Width, actual.Width),
                Math.Min(expected.Height, actual.Height),
                expected.Width * expected.Height,
                expected.Width * expected.Height,
                255,
                1.0);
        }

        BitmapData a = Lock(expected);
        BitmapData b = Lock(actual);
        try
        {
            int width = expected.Width;
            int height = expected.Height;
            int differs = 0;
            int maxDelta = 0;
            unsafe
            {
                for (int y = 0; y < height; y++)
                {
                    byte* pa = Row(a, y);
                    byte* pb = Row(b, y);
                    for (int x = 0; x < width; x++)
                    {
                        // BGRA 序（Format32bppPArgb 的内存序）；alpha 是 byte 3，不参与容差（docs/41:84 只写 RGB）。
                        int db = Math.Abs(pa[(x * 4) + 0] - pb[(x * 4) + 0]);
                        int dg = Math.Abs(pa[(x * 4) + 1] - pb[(x * 4) + 1]);
                        int dr = Math.Abs(pa[(x * 4) + 2] - pb[(x * 4) + 2]);
                        int channelMax = Math.Max(dr, Math.Max(dg, db));
                        if (channelMax > maxDelta)
                        {
                            maxDelta = channelMax;
                        }

                        if (channelMax > GoldenConfig.PixelTolerance)
                        {
                            differs++;
                        }
                    }
                }
            }

            int total = width * height;
            return new Report(width, height, total, differs, maxDelta, total == 0 ? 0.0 : differs / (double)total);
        }
        finally
        {
            expected.UnlockBits(a);
            actual.UnlockBits(b);
        }
    }

    /// <summary>差异图（|a-b|*8 饱和放大），D-12d 的 GPU_DRIFT 与 GOLDEN-FAIL 都要产出它。</summary>
    public static Bitmap DiffImage(Bitmap expected, Bitmap actual)
    {
        int width = expected.Width;
        int height = expected.Height;
        var diff = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        BitmapData a = Lock(expected);
        BitmapData b = Lock(actual);
        BitmapData d = diff.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < height; y++)
                {
                    byte* pa = Row(a, y);
                    byte* pb = Row(b, y);
                    byte* pd = Row(d, y);
                    for (int x = 0; x < width; x++)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            int amplified = Math.Abs(pa[(x * 4) + c] - pb[(x * 4) + c]) * 8;
                            pd[(x * 3) + c] = (byte)Math.Min(255, amplified);
                        }
                    }
                }
            }
        }
        finally
        {
            expected.UnlockBits(a);
            actual.UnlockBits(b);
            diff.UnlockBits(d);
        }

        return diff;
    }

    private static BitmapData Lock(Bitmap bitmap) => bitmap.LockBits(
        new Rectangle(0, 0, bitmap.Width, bitmap.Height),
        ImageLockMode.ReadOnly,
        PixelFormat.Format32bppPArgb);

    private static unsafe byte* Row(BitmapData data, int y) =>
        (byte*)data.Scan0.ToPointer() + (y * data.Stride);
}
