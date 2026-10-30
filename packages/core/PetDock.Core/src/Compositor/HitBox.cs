// 素材包声明的命中区（2026-10-29）：用归一化矩形（0..1，相对画面）描述「点哪里算点中」。
//
// 为什么是归一化而不是像素：画面会随设置里的尺寸滑块缩放，用像素写死的热区在 64 与 256 下
// 会各错一次。归一化之后同一个包在任何尺寸下都对。
//
// 内核这一侧只认矩形（不知道素材包格式）：Host 负责读包、这里负责判定。
namespace PetDock.Core.Compositor;

/// <summary>一块命中区（归一化坐标，左上角原点）。</summary>
internal readonly record struct HitBox(double X, double Y, double W, double H)
{
    /// <summary>点是否落在框里；画面尺寸未知（0）时一律不算。</summary>
    public bool Contains(int pixelX, int pixelY, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        double nx = (double)pixelX / width;
        double ny = (double)pixelY / height;
        return nx >= X && nx <= X + W && ny >= Y && ny <= Y + H;
    }
}
