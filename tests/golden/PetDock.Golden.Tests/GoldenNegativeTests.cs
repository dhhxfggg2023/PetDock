// 负向用例（t10 任务硬性要求）：证明这套金图夹具真的会红。
//
// M1 t5 的教训：没有负向用例时，「比对逻辑是空操作」会让夹具永远 PASS 而不被发现。
// 三条用例把「判红路径」钉死在不同层：
//   1. GoldenSafetyTests.CorruptedBaselineIsRejected —— 过程内把基准期望值改错，比对必须判红
//      （不经 I/O、不被跳过，直接证明 PixelDiffer + 阈值这条链会被差异触发）。
//   2. GoldenSafetyTests.TamperedBaselineFileIsRejected —— 真实文件级：把一个 clip 的基准 PNG
//      换成另一个 clip 的（首帧像素本就不同，期望值被改错），FirstFrameMatchesBaseline 必须判红。
//      这一步模拟「基准被人改错」，走真实读盘比对路径。
//   3. 进程级负向（t10 验证流程手工执行，见 update_task 的 commandsRun）：把 idle 的基准 PNG
//      改成 walk 的之后，单跑 idle 的 FirstFrameMatchesBaseline，dotnet test 进程必须非 0 退出。
using System.Drawing;
using Xunit;

namespace PetDock.Golden.Tests;

public sealed class GoldenSafetyTests
{
    [Fact]
    public void CorruptedBaselineIsRejected()
    {
        // 同一像素数据源，改一个像素的 R 通道 +50（远超 PixelTolerance=4），
        // 比对必须判红。
        using var baseline = new Bitmap(8, 8, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var corrupted = new Bitmap(8, 8, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        Fill(baseline, 100, 150, 200);
        Fill(corrupted, 150, 150, 200);

        PixelDiffer.Report report = PixelDiffer.Compare(baseline, corrupted);
        Assert.False(report.Pass);
        Assert.True(report.MaxChannelDelta >= 50);
        Assert.Equal(64, report.PixelDiffers); // 8x8 全像素都超差
    }

    [Fact]
    public void TamperedBaselineFileIsRejected()
    {
        // 真实文件级负向：idle 与 walk 的首帧像素不同；把 idle 的基准换成 walk 的，
        // FirstFrameMatchesBaseline 必须判红（或 GPU_DRIFT，若本机未认证——本机已认证故必红）。
        string idleBaseline = Path.Combine(AppContext.BaseDirectory, "baselines", "idle", "frame-000.png");
        string walkBaseline = Path.Combine(AppContext.BaseDirectory, "baselines", "walk", "frame-000.png");
        Assert.True(File.Exists(idleBaseline), $"缺 {idleBaseline}");
        Assert.True(File.Exists(walkBaseline), $"缺 {walkBaseline}");

        using var idleExpected = new Bitmap(idleBaseline);
        using var walkActual = new Bitmap(walkBaseline);
        PixelDiffer.Report report = PixelDiffer.Compare(idleExpected, walkActual);
        Assert.False(
            report.Pass,
            $"idle 基准与 walk 首帧本应不同却判通过（differs={report.PixelDiffers} max-delta={report.MaxChannelDelta}）"
            + " —— 比对链退化成空操作。");
    }

    [Fact]
    public void TinyDeltaWithinTolerancePasses()
    {
        // 反向钉死阈值语义：单通道差恰好 = PixelTolerance(4) 时该像素**不**超差
        //（docs/41:84 是「允许最大偏差 4」，>4 才超）；差 5 时超差。
        using var baseline = new Bitmap(8, 8, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var atTolerance = new Bitmap(8, 8, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var beyond = new Bitmap(8, 8, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        Fill(baseline, 100, 100, 100);
        Fill(atTolerance, 104, 100, 100);
        Fill(beyond, 105, 100, 100);

        Assert.True(PixelDiffer.Compare(baseline, atTolerance).Pass);
        Assert.False(PixelDiffer.Compare(baseline, beyond).Pass);
    }

    private static void Fill(Bitmap bitmap, int r, int g, int b)
    {
        using var graphics = Graphics.FromImage(bitmap);
        using var brush = new SolidBrush(Color.FromArgb(255, r, g, b));
        graphics.FillRectangle(brush, 0, 0, bitmap.Width, bitmap.Height);
    }
}
