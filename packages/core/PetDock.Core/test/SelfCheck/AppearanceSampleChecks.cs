// 外观判据的第六块：自检用的宠物艺术必须是真素材，不是代码画的几何块。
//
// 为什么单开一个文件：D-03c 单文件 400 行上限（AppearanceChecks.cs 已经贴着线）。
// 与材质那块同样是 partial 类的一块，入口仍由 AppearanceChecks.Run 统一调用。
using System;
using System.Drawing;

namespace PetDock.Hosting;

internal static partial class AppearanceChecks
{
    /// <summary>
    /// ⑥：自检用的宠物艺术必须是真素材。
    /// 为什么要有这条：所有者 2026-10-28 指出「不是有示例宠物的图片和样本吗，不要再用纯色图片来测试了」——
    /// 于是 PetContractStubs/ThrottleChecks 的夹具改成读本机素材（经 SampleArt 统一取）。
    /// 该素材是本机自用覆盖件（.gitignore），别人的检出里可能没有：缺图按环境跳过，
    /// 但必须把 note 打出来，读的人才知道这一条这次没被测到。
    /// </summary>
    private static void SampleArtPresent(Report check)
    {
        // 不要 using：这张图是 SampleArt 的进程级缓存，夹具还要用；在这里 Dispose 掉
        // 会让后面的窗口夹具拿到已释放的 Bitmap（2026-10-28 实测：--uitest 直接 0xC0000005 崩）。
        Bitmap? frame = SampleArt.TryFrame(128, 128);
        if (frame is null)
        {
            check("ui-appearance-sample-art", true, "跳过：" + SampleArt.Note);
            return;
        }

        int opaque = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                if (frame.GetPixel(x, y).A >= 64)
                {
                    opaque++;
                }
            }
        }

        bool loaded = SampleArt.Note.StartsWith("loaded", StringComparison.Ordinal);
        check(
            "ui-appearance-sample-art",
            loaded && opaque > (frame.Width * frame.Height / 10),
            $"note={SampleArt.Note} opaque={opaque}/{frame.Width * frame.Height}");
    }
}