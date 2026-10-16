// 金图工程的像素工具：进程内首帧渲染 + FNV-1a 整帧签名 + 预乘位图克隆。
//
// 渲染在本进程内做（本工程 OutputType=Exe，Program.cs 用 AssemblyRunner 进程内跑测试，
// 无需 testhost）：链与 core 的 GoldenBaselineGen.Run 完全一致（Discover -> BuildInstance
// ("animtest") -> AttachAndStart(128,128,1.0) -> Start -> SetAnimation(clip, restart:true)
// -> QueryFrame(0)）。基准 PNG 与比对 actual 走同一条链、同一份 fixtures/sample-pet
// （D-13a 零真实素材），QueryFrame(0) 即 t=0 首帧，确定性渲染、无时间戳水印（D-12b）。
using System.Drawing;
using System.Drawing.Imaging;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.Golden.Tests;

internal static class GoldenHarness
{
    public const int FrameWidth = 128;
    public const int FrameHeight = 128;

    /// <summary>
    /// 进程内渲染第 clip 个动画片段的首帧（t=0）。返回的是独立克隆：
    /// 原图归宿主 registry 所有，实例释放后即失效，克隆不随它失效。
    /// </summary>
    public static Bitmap RenderFirstFrame(string clip)
    {
        PetSurfaceRoster roster = PetSurfaceRoster.Discover(PluginHost.ProbePluginsRoot());
        using PetInstance? instance = roster.BuildInstance("animtest");
        if (instance is null)
        {
            throw new InvalidOperationException("build-instance-returned-null（roster 无视图实现）");
        }

        instance.AttachAndStart(FrameWidth, FrameHeight, 1.0);
        instance.Start();
        if (!instance.View.SetAnimation(clip, restart: true))
        {
            throw new InvalidOperationException($"set-animation-rejected clip={clip}");
        }

        Frame frame = instance.QueryFrame(0);
        Bitmap source = frame.BitmapHandle.Bitmap;
        return source.Clone(
            new Rectangle(0, 0, source.Width, source.Height),
            PixelFormat.Format32bppPArgb);
    }

    /// <summary>整帧像素的 FNV-1a 64 位签名（与 core 的 PetAnimTest.SignatureOf 同一算法）。</summary>
    public static long SignatureOf(Bitmap bitmap)
    {
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            long hash = unchecked((long)1469598103934665603UL);
            int bytes = data.Stride * bitmap.Height;
            unsafe
            {
                byte* p = (byte*)data.Scan0.ToPointer();
                for (int i = 0; i < bytes; i++)
                {
                    hash = unchecked((hash ^ p[i]) * 1099511628211L);
                }
            }

            return hash;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
