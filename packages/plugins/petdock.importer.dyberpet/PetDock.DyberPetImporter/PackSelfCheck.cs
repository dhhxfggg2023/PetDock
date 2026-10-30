using System.Drawing.Imaging;

namespace PetDock.DyberPet;

/// <summary>
/// 导入收尾自检（契约 §4.3 B 路径的插件侧证据；canonical 悬停驱动面归 t2/t6 的 --drivetest）：
///   1. 产出的每一帧路径都存在（PackLoader.LoadClip 会逐帧 File.Exists，提前在这里失败更可读）；
///   2. 悬停命中前提（原规格 11:53 alpha 语义）：focus 帧 0 粘到 naturalSize 画板上后，
///      画板里同时存在不透明像素（可命中）与透明像素（可穿透）——没有这一对，悬停反应不可能发生。
/// 审计行一律 `selfcheck-…`，与 importer 的 `dyberpet-…`、core 的 `CHECK-…` 三个前缀互不混用。
/// </summary>
internal static class PackSelfCheck
{
    private const int OpaqueAlphaThreshold = 128;

    public static void Run(string outDir, IReadOnlyList<ClipOutput> clips, int boardW, int boardH, string? focus)
    {
        int total = 0;
        int missing = 0;
        foreach (ClipOutput clip in clips)
        {
            foreach (string frame in clip.Frames)
            {
                total++;
                if (!File.Exists(frame))
                {
                    missing++;
                    Console.Error.WriteLine($"selfcheck-frames missing frame={frame} clip={clip.Name}");
                }
            }
        }

        Console.Error.WriteLine($"selfcheck-frames total={total} missing={missing}");
        if (missing > 0)
        {
            throw new InvalidDataException($"产出包含 {missing} 个不存在的帧路径（PackLoader 必拒，导入期失败）");
        }

        if (string.IsNullOrEmpty(focus))
        {
            Console.Error.WriteLine("selfcheck-hover skipped reason=no-focus-key");
            return;
        }

        ClipOutput? focusClip = clips.FirstOrDefault(c => string.Equals(c.Name, focus, StringComparison.Ordinal));
        if (focusClip is null)
        {
            Console.Error.WriteLine($"selfcheck-hover skipped reason=no-focus-clip focus={focus}");
            return;
        }

        CheckHoverPremise(focusClip, boardW, boardH);
    }

    /// <summary>把 focus 帧 0 按契约 §2.7 左上 (0,0) 粘到画板，扫描出一对 (不透明, 透明) 像素坐标作为命中/穿透前提证据。</summary>
    private static void CheckHoverPremise(ClipOutput clip, int boardW, int boardH)
    {
        using var board = new System.Drawing.Bitmap(boardW, boardH, PixelFormat.Format32bppPArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(board))
        {
            using var frame = new System.Drawing.Bitmap(clip.Frames[0]);
            using var pArgb = frame.Clone(new System.Drawing.Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format32bppPArgb);
            graphics.DrawImageUnscaled(pArgb, 0, 0); // 与修正后的 SpriteSeqView.Compose 同一粘贴语义
        }

        var rect = new System.Drawing.Rectangle(0, 0, board.Width, board.Height);
        var data = board.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                int stride = data.Stride;
                int opaqueX = -1, opaqueY = -1, opaqueA = -1;
                int clearX = -1, clearY = -1;
                for (int y = 0; y < board.Height && (opaqueX < 0 || clearX < 0); y++)
                {
                    byte* row = basePtr + y * stride;
                    for (int x = 0; x < board.Width; x++)
                    {
                        int a = row[(x * 4) + 3];
                        if (opaqueX < 0 && a >= OpaqueAlphaThreshold)
                        {
                            opaqueX = x;
                            opaqueY = y;
                            opaqueA = a;
                        }

                        if (clearX < 0 && a == 0)
                        {
                            clearX = x;
                            clearY = y;
                        }

                        if (opaqueX >= 0 && clearX >= 0)
                        {
                            break;
                        }
                    }
                }

                Console.Error.WriteLine(
                    $"selfcheck-hover-pixel premise=hit focus={clip.Name} board={board.Width}x{board.Height}"
                    + $" pixel=({opaqueX},{opaqueY}) a={opaqueA} threshold=a>={OpaqueAlphaThreshold}"
                    + $" status={(opaqueX >= 0 ? "PASS" : "FAIL")}");
                Console.Error.WriteLine(
                    $"selfcheck-hover-pixel premise=passthrough focus={clip.Name} board={board.Width}x{board.Height}"
                    + $" pixel=({clearX},{clearY}) a=0"
                    + $" status={(clearX >= 0 ? "PASS" : "FAIL")}");
                if (opaqueX < 0 || clearX < 0)
                {
                    throw new InvalidDataException(
                        $"悬停命中前提不成立（focus={clip.Name} 帧全透或画板无透明位），原规格 11:53 语义下悬停反应不可能发生");
                }
            }
        }
        finally
        {
            board.UnlockBits(data);
        }
    }
}
