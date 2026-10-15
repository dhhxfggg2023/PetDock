// 实现 docs/12 §2.3「命中：交给操作系统」的自检探针：读 alpha 后的 OS 命中结果
using System.Drawing;

namespace PetDock.Core.Compositor;

/// <summary>
/// 命中判定探针（--selftest 用）。命中测试完全由 user32 读分层窗的 alpha 完成，
/// 这里只取 OS 的答案，不做任何像素比较。
/// </summary>
internal static class HitProbe
{
    /// <summary>透明角取样点相对窗口左上角的偏移（贴图四角完全透明）。</summary>
    private const int TransparentCornerInset = 2;

    /// <summary>
    /// 对宿主窗口取两组判定结果：
    /// 命中点 = 窗口矩形中心，应命中本窗（opaque=hit）；
    /// 穿透点 = 窗口矩形左上角 +2,+2（贴图透明角），应不命中本窗（transparent=pass）。
    /// </summary>
    public static HitReport Probe(IntPtr hwnd)
    {
        Rectangle rect = Win32.ScreenRect(hwnd);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            throw new InvalidOperationException($"窗口矩形无效：{rect}");
        }

        POINT center = new()
        {
            X = rect.Left + (rect.Width / 2),
            Y = rect.Top + (rect.Height / 2),
        };

        POINT corner = new()
        {
            X = rect.Left + TransparentCornerInset,
            Y = rect.Top + TransparentCornerInset,
        };

        IntPtr hitAtCenter = Win32.TopLevelWindowFromPoint(center);
        IntPtr hitAtCorner = Win32.TopLevelWindowFromPoint(corner);

        return new HitReport(
            OpaqueHit: hitAtCenter == hwnd,
            TransparentPass: hitAtCorner != hwnd,
            WindowRect: rect,
            Center: center,
            Corner: corner,
            HitAtCenter: hitAtCenter,
            HitAtCorner: hitAtCorner,
            NcHitTestCenter: Win32.HitTest(hwnd, center),
            NcHitTestCorner: Win32.HitTest(hwnd, corner),
            ScreenCenter: Win32.SampleScreen(center),
            ScreenCorner: Win32.SampleScreen(corner),
            ClassAtCenter: Win32.ClassNameAtPoint(center),
            ClassAtCorner: Win32.ClassNameAtPoint(corner));
    }
}

/// <summary>一次命中探针的完整结果。</summary>
internal sealed record HitReport(
    bool OpaqueHit,
    bool TransparentPass,
    Rectangle WindowRect,
    POINT Center,
    POINT Corner,
    IntPtr HitAtCenter,
    IntPtr HitAtCorner,
    int NcHitTestCenter,
    int NcHitTestCorner,
    string ScreenCenter,
    string ScreenCorner,
    string ClassAtCenter,
    string ClassAtCorner);
