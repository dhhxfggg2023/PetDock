// M3 t8 t7-1：DyberPet 基准生成器——渲染 default 首帧 + 写 meta.json，一次跑完。
//
// 与 sample-pet 的分工：core --golden-baseline 渲染 sample-pet 三 clip（写 stdout 由人拷贝）；
// 这里一条命令产出 dyberpet-default 的 frame-000.png + frame-000.meta.json 直落源树
// baselines/dyberpet-default/。pack 由 importer 预先导入（artifacts/dyberpet-min-golden），
// 渲染链 = GoldenHarness.RenderFirstFrameFromPack（PETDOCK_SAMPLE_PACK 入口，零内核改动）。
using System.Drawing;
using System.Security.Cryptography;

namespace PetDock.Golden.Tests;

public static class DyberPetBaselineGen
{
    public static int Run()
    {
        string baselineDir = FindSourceRoot();
        baselineDir = Path.Combine(baselineDir, "baselines", DyberPetBaselineTests.BaselineDir);
        Directory.CreateDirectory(baselineDir);

        Bitmap frame;
        try
        {
            using DyberPetPackLocator.PackHandle pack = DyberPetPackLocator.Acquire();
            frame = GoldenHarness.RenderFirstFrameFromPack(
                pack.Root, DyberPetBaselineTests.Clip);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DYBERPET-BASELINE-FAIL render {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        using (frame)
        {
            if (frame.Width != GoldenHarness.FrameWidth || frame.Height != GoldenHarness.FrameHeight)
            {
                Console.WriteLine($"DYBERPET-BASELINE-FAIL size {frame.Width}x{frame.Height} != 128x128");
                return 1;
            }

            string png = Path.Combine(baselineDir, "frame-000.png");
            frame.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"DYBERPET-BASELINE-WROTE {png}");
        }

        string metaPath = Path.Combine(baselineDir, "frame-000.meta.json");
        string sha = Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(baselineDir, "frame-000.png"))));
        BaselineMeta.Write(metaPath, DyberPetBaselineTests.SourceAsset, sha);
        Console.WriteLine($"DYBERPET-BASELINE-OK clip={DyberPetBaselineTests.Clip} sha256={sha}");
        return 0;
    }

    // 与 BaselineGenerator.FindSourceRoot 同法：从输出目录向上找名为
    // PetDock.Golden.Tests 的目录（含 baselines/ 或 csproj 的源树位置）。
    private static string FindSourceRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "PetDock.Golden.Tests");
            if (Directory.Exists(Path.Combine(candidate, "baselines"))
                || File.Exists(Path.Combine(candidate, "PetDock.Golden.Tests.csproj")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("PetDock.Golden.Tests source dir not found");
    }
}
