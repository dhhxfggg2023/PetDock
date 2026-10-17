// M3 t8 t7-1：DyberPet 夹具的金图接线（docs/32 §7 第 3 条的第一条真证据）。
//
// 基准源不是真实素材（D-13a 零真实素材入库），而是 docs/41 D-13b 合成夹具
// tests/fixtures/dyberpet-min/dyberpet-flat 经 petdock.importer.dyberpet 导入后的
// pack（petdock-sample.json + anim/*.json，与示例宠物同一 sprite-seq schema）。
// 渲染链与 sample-pet 金图完全一致（GoldenHarness.RenderFirstFrame），只是 pack 由
// PETDOCK_SAMPLE_PACK 环境变量选定。基准图 + meta.json 落 baselines/dyberpet-default/。
//
// 生成流程（人工一次性）：
//   1. importer --in tests/fixtures/dyberpet-min/dyberpet-flat --out artifacts/dyberpet-min-golden
//   2. PETDOCK_SAMPLE_PACK=artifacts\dyberpet-min-golden core --golden-baseline <tmp>（取 default）
//      —— 或直接由 DyberPetBaselineTests 的 render 模式写基准（见 Program.cs --golden-baseline-dyberpet）
//   3. Program --write-meta-dyberpet 写 meta.json（sha256 + GPU 认证，D-12c）
// 负例红→绿（D-12b）由 GoldenNegativeTests 的 PixelDiffer 容差体系承担（同一 differ）。
using System.Drawing;
using System.Security.Cryptography;
using Xunit;

namespace PetDock.Golden.Tests;

// 与 GoldenBaselineTests 同一串行 collection：渲染测试共用 PETDOCK_SAMPLE_PACK 环境变量。
[Collection("GoldenRenderSerial")]
public sealed class DyberPetBaselineTests
{
    public const string Clip = "default";
    public const string BaselineDir = "dyberpet-default";
    public const string SourceAsset = "fixtures/dyberpet-min/dyberpet-flat（经 importer）";

    [Fact]
    public void DyberPetFirstFrameMatchesBaseline()
    {
        string baselineDir = Path.Combine(AppContext.BaseDirectory, "baselines", BaselineDir);
        string baselinePng = Path.Combine(baselineDir, "frame-000.png");
        string metaPath = Path.Combine(baselineDir, "frame-000.meta.json");
        Assert.True(File.Exists(baselinePng), $"missing dyberpet baseline {baselinePng}（先跑 --golden-baseline-dyberpet）");
        Assert.True(File.Exists(metaPath), $"missing dyberpet meta {metaPath}（先跑 --write-meta-dyberpet）");

        using Bitmap expected = new(baselinePng);
        using Bitmap actual = GoldenHarness.RenderFirstFrameFromPack(FindPackRoot(), Clip);

        Assert.Equal(GoldenHarness.FrameWidth, actual.Width);
        Assert.Equal(GoldenHarness.FrameHeight, actual.Height);

        PixelDiffer.Report report = PixelDiffer.Compare(expected, actual);
        if (report.Pass)
        {
            return; // GOLDEN-PASS
        }

        string diffPath = Path.Combine(
            Path.GetTempPath(), $"petdock-golden-{BaselineDir}-{Guid.NewGuid():N}.diff.png");
        using (Bitmap diff = PixelDiffer.DiffImage(expected, actual))
        {
            diff.Save(diffPath, System.Drawing.Imaging.ImageFormat.Png);
        }

        string meta = BaselineMeta.IsGpuCertified(metaPath, GpuProbe.GpuModel())
            ? "certified"
            : "uncertified";
        Assert.Fail(
            $"GOLDEN-FAIL dyberpet {BaselineDir} differs={report.PixelDiffers}" +
            $"/{report.TotalPixels} max-delta={report.MaxChannelDelta} " +
            $"ratio={report.OutlierRatio:F5} meta={meta} diff={diffPath}");
    }

    // 基准自洽：提交进仓的 meta.json 的 sha256 必须等于基准 PNG 实算值，且 sourceAsset
    // 指向 D-13b 合成夹具（防止有人把真实素材渲染结果混进基准）。独立于
    // BaselineGenerator.CommittedBaselineIsSelfConsistent（那只覆盖 sample-pet 三 clip）。
    [Fact]
    public void DyberPetCommittedBaselineIsSelfConsistent()
    {
        string baselineDir = Path.Combine(AppContext.BaseDirectory, "baselines", BaselineDir);
        string baselinePng = Path.Combine(baselineDir, "frame-000.png");
        string metaPath = Path.Combine(baselineDir, "frame-000.meta.json");
        Assert.True(File.Exists(baselinePng), $"missing dyberpet baseline {baselinePng}");
        Assert.True(File.Exists(metaPath), $"missing dyberpet meta {metaPath}");

        string sha = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(baselinePng)));
        Assert.Equal(sha, BaselineMeta.Sha256Of(metaPath));
        Assert.Equal(SourceAsset, BaselineMeta.SourceAssetOf(metaPath));
    }

    // 找 importer 产物 pack：优先 artifacts/dyberpet-min-golden（相对源树根），找不到则
    // 允许环境变量直接指定（CI 可预生成到别处）。源树根 = 向上找 tests/fixtures 标记。
    private static string FindPackRoot()
    {
        string? overridden = Environment.GetEnvironmentVariable("PETDOCK_DYBERPET_PACK");
        if (!string.IsNullOrEmpty(overridden))
        {
            return overridden;
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "dyberpet-min-golden", "petdock-sample.json");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "dyberpet pack not found（先跑 importer --in tests/fixtures/dyberpet-min/dyberpet-flat " +
            "--out artifacts/dyberpet-min-golden，或设 PETDOCK_DYBERPET_PACK）");
    }
}
