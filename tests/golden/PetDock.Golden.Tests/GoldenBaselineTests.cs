// 金图回归比对（docs/41 D-12）：对示例宠物的首帧渲染结果逐像素比对基准图。
//
// 三态退出（t1 契约 §6.4 / docs/41 D-12d）：
//   GOLDEN-PASS   容差内                     → 通过
//   GPU_DRIFT     超容差 且 本机 GPU 未认证    → 标记并产出差异图，但不阻断（仍通过，附警告）
//   GOLDEN-FAIL   超容差 且 本机 GPU 已认证    → 失败 + 差异图
// 本机只有一张卡（docs/41:6 单人裁剪），GPU_DRIFT 态在本机大概率不触发——它是为将来准备的。
//
// 基准图与 meta.json 由 csproj 复制到输出目录；比对只写 TempFile 差异图与一句诊断，
// 源树里的基准保持只读（D-12e 的四件套更新走人工 commit，不走测试）。
using System.Drawing;
using Xunit;
using Xunit.Abstractions;

namespace PetDock.Golden.Tests;

// 两类渲染测试共用 PETDOCK_SAMPLE_PACK 环境变量与全局 roster，必须串行（t8 t7-1）：
// xunit v2 默认按 collection 并行，同一 collection 内串行执行。
[Collection("GoldenRenderSerial")]
public sealed class GoldenBaselineTests
{
    private const string GpuDriftMarker = "GPU_DRIFT";
    private static readonly string[] Clips = { "idle", "walk", "hover" };

    private readonly ITestOutputHelper _output;

    public GoldenBaselineTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("idle")]
    [InlineData("walk")]
    [InlineData("hover")]
    public void FirstFrameMatchesBaseline(string clip)
    {
        using Bitmap actual = GoldenHarness.RenderFirstFrame(clip);
        string baselineDir = Path.Combine(AppContext.BaseDirectory, "baselines", clip);
        string baselinePath = Path.Combine(baselineDir, "frame-000.png");
        string metaPath = Path.Combine(baselineDir, "frame-000.meta.json");

        Assert.True(File.Exists(baselinePath), $"缺基准图 {baselinePath}（用 --update-baselines 生成）");
        Assert.True(File.Exists(metaPath), $"缺基准 meta {metaPath}");

        using var expected = new Bitmap(baselinePath);
        Assert.Equal(
            new Size(GoldenHarness.FrameWidth, GoldenHarness.FrameHeight),
            new Size(expected.Width, expected.Height));

        PixelDiffer.Report report = PixelDiffer.Compare(expected, actual);
        long actualSignature = GoldenHarness.SignatureOf(actual);

        if (report.Pass)
        {
            _output.WriteLine(
                $"{clip}: GOLDEN-PASS signature={actualSignature:x} differs={report.PixelDiffers}"
                + $" max-delta={report.MaxChannelDelta} ratio={report.OutlierRatio:F5}");
            return;
        }

        string diffPath = WriteDiff(clip, expected, actual);
        bool certified = BaselineMeta.IsGpuCertified(metaPath, GpuProbe.GpuModel());

        _output.WriteLine(
            $"{clip}: differs={report.PixelDiffers}/{report.TotalPixels}"
            + $" max-delta={report.MaxChannelDelta} ratio={report.OutlierRatio:F5}"
            + $" certified={certified} signature={actualSignature:x} diff={diffPath}");

        if (!certified)
        {
            // GPU_DRIFT：跨 GPU 数值差异可能是预期内的，误阻断会拖慢单人开发（docs/41:91）。
            // 不阻断，但仍产出差异图供人工核对；核对结论登记 docs/51（D-15b）。
            _output.WriteLine($"{clip}: {GpuDriftMarker} —— 本机 GPU 未在该基准认证，超容差不阻断。");
            return;
        }

        Assert.Fail(
            $"{clip}: GOLDEN-FAIL —— 超容差且本机 GPU 已认证。differs={report.PixelDiffers}"
            + $"/{report.TotalPixels} max-delta={report.MaxChannelDelta}"
            + $" ratio={report.OutlierRatio:F5} diff={diffPath}");
    }

    private static string WriteDiff(string clip, Bitmap expected, Bitmap actual)
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"petdock-golden-{clip}-{Guid.NewGuid():N}.diff.png");
        using Bitmap diff = PixelDiffer.DiffImage(expected, actual);
        diff.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }
}
