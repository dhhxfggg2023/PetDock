// 内核自检兜底内容源：内核不含任何素材定位/解码逻辑（原规格 40 D-03b），
// 没有内容源注入时用「中央不透明圆盘 + 四角全透明」的占位帧回答两件事：
// 窗口合成了一帧（pixel-on-screen 对得上兜底色）、命中穿透真实有效
// （矩形中心应命中、左上角 +2,+2 应穿透——纯色 Clear 全不透明会让后者永远 fail，本机实测）。
// 命名与词汇按 tools/lint-core.mjs 黑名单规避上层概念词。
using System.Drawing;

namespace PetDock.Core.Compositor;

/// <summary>占位帧：与帧缓冲同尺寸；中央不透明圆盘，其余像素 alpha=0。</summary>
internal sealed class SolidFrameContent : IFrameContentSource
{
    private readonly Color _color;
    private Bitmap? _frame;
    private Size _size;

    private SolidFrameContent(Color color)
    {
        _color = color;
    }

    /// <summary>内核兜底色：纯蓝。自检的 dib-sample 与 pixel-on-screen 按它对色。</summary>
    public static SolidFrameContent KernelFallback()
    {
        return new SolidFrameContent(Color.FromArgb(0xFF, 0x3A, 0x6E, 0xC0));
    }

    public Bitmap? GetFrameContent(Size frameSize)
    {
        if (frameSize.Width <= 0 || frameSize.Height <= 0)
        {
            return null;
        }

        if (_frame is null || _size != frameSize)
        {
            _frame?.Dispose();
            _frame = new Bitmap(frameSize.Width, frameSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(_frame);
            graphics.Clear(Color.FromArgb(0, _color));
            using var brush = new SolidBrush(_color);
            int diameter = Math.Min(frameSize.Width, frameSize.Height);
            graphics.FillEllipse(brush, (frameSize.Width - diameter) / 2,
                (frameSize.Height - diameter) / 2, diameter, diameter);
            _size = frameSize;
        }

        return _frame;
    }

    public void Dispose()
    {
        _frame?.Dispose();
        _frame = null;
    }
}
