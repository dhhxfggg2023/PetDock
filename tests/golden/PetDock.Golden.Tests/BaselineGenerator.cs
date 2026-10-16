// 基准 PNG 的落点与 meta.json 生成。
//
// 基准 PNG 由 core 的一次性脚本渲染（`dotnet run --project packages/core -- --golden-baseline
// tests/golden/PetDock.Golden.Tests/baselines`，见 packages/core/src/SelfTest/GoldenBaselineGen.cs），
// 与金图比对走的是同一条链（Discover -> BuildInstance("animtest") -> AttachAndStart(128,128,1.0)
// -> Start -> SetAnimation(clip, restart:true) -> QueryFrame(0)），所以「渲染进没进测试进程」
// 不再是分叉点：同一链、同一份 fixtures/sample-pet，结果逐字节可比（D-12b 确定性）。
// 唯一区别是执行进程——基准在 core 的一次性脚本里渲染，比对 actual 由 GoldenHarness 在
// 测试进程内渲染（本机 testhost 起不来，故本工程用 Program.cs 的 AssemblyRunner 进程内跑）。
//
// meta.json（D-12c/D-12d）在这里生成：登记 GPU 型号 + 驱动版本 + sha256 + 已认证型号。
// 生成入口有两个（同一实现）：
//   - Program --write-meta 独立模式（验证流程用，只写 meta 不跑测试）；
//   - 本类 [Fact] WriteBaselineMeta（env PETDOCK_GOLDEN_WRITE_META=1 时触发）。
using System.Security.Cryptography;
using Xunit;

namespace PetDock.Golden.Tests;

public sealed class BaselineGenerator
{
    /// <summary>为已渲染好的基准 PNG 补 meta.json（GPU 型号 + 驱动 + sha256 + 已认证型号）。
    /// 返回 0=成功，1=缺基准 PNG（先跑 core golden-baseline 渲染）。</summary>
    public static int WriteMeta()
    {
        string sourceRoot = FindSourceRoot();
        foreach (string clip in new[] { "idle", "walk", "hover" })
        {
            string pngPath = Path.Combine(sourceRoot, "baselines", clip, "frame-000.png");
            if (!File.Exists(pngPath))
            {
                Console.WriteLine($"META-FAIL missing {pngPath}（先跑 core golden-baseline 渲染）");
                return 1;
            }

            string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pngPath)));
            BaselineMeta.Write(Path.Combine(sourceRoot, "baselines", clip, "frame-000.meta.json"),
                "fixtures/sample-pet", sha256);
            Console.WriteLine($"META-WROTE clip={clip} sha256={sha256}");
        }

        Console.WriteLine("META-OK");
        return 0;
    }

    /// <summary>
    /// 正向完整性断言（M1 t5 教训：夹具不许有永远 PASS 的 no-op 用例）：
    /// 每个 clip 的基准 PNG 与 meta.json 都在源树里，且 meta 登记的 sha256 与 PNG 实算一致。
    /// meta 的生成走 Program --write-meta（本用例不写文件，只验提交的基线自洽）。
    /// </summary>
    [Fact]
    public void CommittedBaselineIsSelfConsistent()
    {
        string sourceRoot = FindSourceRoot();
        foreach (string clip in new[] { "idle", "walk", "hover" })
        {
            string pngPath = Path.Combine(sourceRoot, "baselines", clip, "frame-000.png");
            string metaPath = Path.Combine(sourceRoot, "baselines", clip, "frame-000.meta.json");
            Assert.True(File.Exists(pngPath), $"缺基准 PNG {pngPath}（先跑 core golden-baseline 渲染）");
            Assert.True(File.Exists(metaPath), $"缺基准 meta {metaPath}（先跑 Program write-meta）");

            string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pngPath)));
            Assert.Equal(sha256, BaselineMeta.Sha256Of(metaPath));
            Assert.Equal("fixtures/sample-pet", BaselineMeta.SourceAssetOf(metaPath));
        }
    }

    private static string FindSourceRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "PetDock.Golden.Tests");
            if (Directory.Exists(Path.Combine(candidate, "baselines"))
                || File.Exists(Path.Combine(candidate, "PetDock.Golden.Tests.csproj")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"从 {AppContext.BaseDirectory} 向上找不到 PetDock.Golden.Tests 源树（含 baselines/ 或 csproj）。");
    }
}
