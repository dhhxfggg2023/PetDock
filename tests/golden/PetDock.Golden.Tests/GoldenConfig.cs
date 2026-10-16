// t1 契约 §6.2（docs/41 D-12a 语义）：金图比对的两个阈值。
// D-12a 原文把阈值写在 tests/golden.config.ts；t1 裁决 5 明确「遵守语义、不遵守扩展名」，
// 阈值落成 C# 常量（本工程无 Node 测试运行时），该冲突已登记 docs/51（由文档任务落）。
//
//   PixelTolerance = 4        —— GOLDEN_PIXEL_TOLERANCE：单个像素的 RGB 各通道允许最大偏差（0-255 尺度）
//   TotalTolerance = 0.005    —— GOLDEN_TOTAL_TOLERANCE = 0.5%：整图允许「超阈值像素占比」上限
//
// 判定（docs/41:84 逐字）：逐像素取 RGB 三通道各自差的绝对值，任一通道 > 4 即该像素超阈值；
// 超阈值像素数 / 总像素数 > 0.005 即失败。**两项任一超限即失败**——两条都计算，任一超标就红。
// alpha 通道不参与容差比较（docs/41:84 只写 RGB）。
namespace PetDock.Golden.Tests;

internal static class GoldenConfig
{
    public const int PixelTolerance = 4;

    public const double TotalTolerance = 0.005;
}
