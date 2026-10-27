// StubView 的帧绘制（partial 的另一半，D-03c 单文件 400 行上限）。
//
// 为什么单独放：这段要同时说明「为什么用真素材」与「为什么克隆」，注释比代码长；
// 放在主文件里会把 PetContractStubs.cs 顶过 400 行。
using System.Drawing;
using System.Drawing.Imaging;

namespace PetDock.Hosting;

internal sealed partial class StubView
{
    private Bitmap BitmapFor(int index)
    {
        if (_frames.TryGetValue(index, out Bitmap? cached))
        {
            return cached;
        }

        int width = _target?.PixelWidth ?? 8;
        int height = _target?.PixelHeight ?? 8;

        // 有真素材就用真素材（2026-10-28 所有者指出：纯色几何图看不出是宠物还是占位块）。
        // 克隆而不是共享：这些帧在 DisposeAsync 里会被逐张释放，共享缓存会被提前 Dispose。
        Bitmap? art = SampleArt.TryFrame(width, height);
        if (art is not null)
        {
            _frames[index] = (Bitmap)art.Clone();
            return _frames[index];
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(0, 0, 0, 0));
        using var brush = new SolidBrush(Color.FromArgb(255, 30, 60 + (index * 20), 200));
        g.FillEllipse(brush, 1, 1, width - 2, height - 2);
        _frames[index] = bitmap;
        return bitmap;
    }
}