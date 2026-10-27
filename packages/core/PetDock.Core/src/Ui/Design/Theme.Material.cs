// 材质令牌（docs/18 §2.7，2026-10-28）：不透明 / 磨砂 / 玻璃三档。
//
// 口径：材质只改「画上去的颜色有多透」与「窗口怎么和系统合成」，不改任何布局数值。
// 这样四档缩放、文字放得下、控件种类这些判据在三种材质下是同一组事实，只需要额外量
// ①令牌的 alpha 单调递减 ②落到真窗上不抛、且回报的档位与请求的一致。
//
// 为什么要分文件：Theme.cs 已经接近 D-03c 的 400 行上限（这一片把配色表也撑长了），
// 材质是独立的一块，拆开比继续挤在一个文件里更容易读。
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

/// <summary>
/// 窗口材质（docs/18 §2.7）。默认不透明：系统给不了合成能力时这是唯一诚实的选项。
/// </summary>
internal enum MaterialKind
{
    /// <summary>不透明：自绘整块底色，与系统合成无关。</summary>
    Opaque = 0,

    /// <summary>磨砂：Win11 走 Mica，Win10 走系统模糊；界面半透明但保持高可读。</summary>
    Frosted = 1,

    /// <summary>玻璃：更透的一档，背景可辨；可读性靠更实的文字与更亮的描边兜住。</summary>
    Glass = 2,
}

internal static partial class Theme
{
    /// <summary>
    /// 当前窗口材质。磨砂/玻璃依赖系统合成（Win11 的 Mica/亚克力，Win10 的模糊），
    /// 系统给不了时由 <c>MaterialHost</c> 回退到不透明并留一行诊断——界面不因材质缺失而坏掉。
    /// </summary>
    public static MaterialKind Material { get; set; } = MaterialKind.Opaque;

    /// <summary>
    /// 把令牌颜色换成当前材质下的「画上去的颜色」：不透明材质原样返回，磨砂/玻璃给一层 alpha。
    /// 为什么只动绘制、不动 BackColor：WinForms 的 BackColor 不接受半透明值（会抛），
    /// 而自绘控件全部经过这里取色，所以一层 alpha 就能让整窗透出系统合成的那层背景。
    /// </summary>
    public static Color Paint(Color opaque)
    {
        int alpha = Material switch
        {
            MaterialKind.Frosted => 224,
            MaterialKind.Glass => 178,
            _ => 255,
        };

        return alpha >= 255 ? opaque : Color.FromArgb(alpha, opaque);
    }

    /// <summary>当前材质下该用的背景色（画到窗口客户区时用）。</summary>
    public static Color PaintBg => Paint(Bg);

    /// <summary>材质名 → 枚举。认不出的值一律回落到不透明（旧文件、手滑改坏都走这条）。</summary>
    public static MaterialKind ParseMaterial(string? token) => token switch
    {
        "frosted" => MaterialKind.Frosted,
        "glass" => MaterialKind.Glass,
        _ => MaterialKind.Opaque,
    };

    /// <summary>枚举 → 落盘用的名字（小写、稳定，不随枚举值顺序变）。</summary>
    public static string MaterialToken(MaterialKind kind) => kind switch
    {
        MaterialKind.Frosted => "frosted",
        MaterialKind.Glass => "glass",
        _ => "opaque",
    };

    /// <summary>最近一次材质应用的结果（设置页与诊断读它，不自己猜）。</summary>
    public static MaterialReport LastMaterial { get; private set; } = MaterialHost.None;

    /// <summary>把当前材质重新落到窗口上（设置页切换材质后立即调用）。</summary>
    public static MaterialReport ApplyMaterial(Form form)
    {
        MaterialReport report = MaterialHost.Apply(form, Material);
        LastMaterial = report;
        Compositor.KernelLog.Write($"ui-material {report.Describe()}");
        return report;
    }

    /// <summary>画刷（自带 Dispose 责任，调用方用 using）。</summary>
    public static SolidBrush Brush(Color opaque) => new(Paint(opaque));

    /// <summary>画笔（线条也随材质变淡，否则磨砂窗上会出现硬边）。</summary>
    public static Pen Pen(Color opaque, float width = 1f) => new(Paint(opaque), width);
}