// 设计令牌（R2a）。**本工程唯一的颜色事实源**：别的文件不许写十六进制色值，
// 也不许写魔法间距 —— 判据 ui-design-tokens-only 扫全工程（白名单只有本文件）。
//
// 为什么是两套调色板而不是「主题色 + 变量」：新层只支持明暗两种（旧层的材质/亮度是另一回事，
// 那属于窗口材质，不进界面层）。两套写死的好处是**对比度可以被判据算出来**：
// ui-design-contrast 对两套都算 WCAG 比值，正文 ≥ 4.5、正文弱化 ≥ 3.0。
//
// 颜色用 System.Windows.Media.Color；画笔一律冻结（Freeze），否则每次布局都会新建一份
// 可变的 Brush，滚动时白拿抖动。
using System.Collections.Generic;
using System.Windows.Media;

namespace PetDock.Ui.Design;

/// <summary>明/暗两种模式。</summary>
public enum ThemeMode
{
    Dark = 0,
    Light = 1,
}

/// <summary>
/// 一套调色板。字段全是**角色**而不是颜色名（Surface 而不是 Gray800）：
/// 换一套配色时只改这里的值，控件那边一个字都不用动。
/// </summary>
public sealed record Palette(
    Color Background,
    Color Surface,
    Color SurfaceAlt,
    Color Border,
    Color TextPrimary,
    Color TextSecondary,
    Color Accent,
    Color AccentText,
    Color Danger,
    Color Warning,
    Color Success)
{
    /// <summary>深色：默认。底色压到 #1E1F22，卡片亮一档，靠边框而不是阴影分层。</summary>
    public static Palette Dark { get; } = new(
        Background: Hex("#1E1F22"),
        Surface: Hex("#26272B"),
        SurfaceAlt: Hex("#2E3035"),
        Border: Hex("#3A3C42"),
        TextPrimary: Hex("#F2F3F5"),
        TextSecondary: Hex("#B8BCC4"),
        Accent: Hex("#4C8DFF"),
        AccentText: Hex("#0B1220"),
        Danger: Hex("#FF6B6B"),
        Warning: Hex("#F2C14E"),
        Success: Hex("#5FD08A"));

    /// <summary>浅色：纸白底，同样靠边框分层。</summary>
    public static Palette Light { get; } = new(
        Background: Hex("#F6F7F9"),
        Surface: Hex("#FFFFFF"),
        SurfaceAlt: Hex("#EEF0F4"),
        Border: Hex("#D5D9E0"),
        TextPrimary: Hex("#1B1D22"),
        TextSecondary: Hex("#4A505B"),
        Accent: Hex("#1F6FEB"),
        AccentText: Hex("#FFFFFF"),
        Danger: Hex("#C62828"),
        Warning: Hex("#8A6100"),
        Success: Hex("#1B7A43"));

    /// <summary>取一套已冻结的画笔（同一颜色只建一次）。</summary>
    public SolidColorBrush Brush(Color color)
    {
        if (!_brushes.TryGetValue(color, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(color);
            brush.Freeze();
            _brushes[color] = brush;
        }

        return brush;
    }

    private readonly Dictionary<Color, SolidColorBrush> _brushes = new();

    /// <summary>#RRGGBB → Color（解码只在这里发生一次，别处不许再写十六进制）。</summary>
    private static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);
}

/// <summary>间距、圆角、字号、动效的刻度。界面里只许用这些档位，不许就地写数字。</summary>
public static class Tokens
{
    /// <summary>4 的倍数。间距一律取这几个档，避免每个控件自己拍一个数。</summary>
    public static class Space
    {
        public const double Xs = 4;
        public const double Sm = 8;
        public const double Md = 12;
        public const double Lg = 16;
        public const double Xl = 24;
        public const double Xxl = 32;
    }

    public static class Radius
    {
        public const double Sm = 4;
        public const double Md = 8;
        public const double Lg = 12;
    }

    public static class Text
    {
        /// <summary>界面字体：跟随系统 UI 字体（中文在 Windows 上会落到微软雅黑）。</summary>
        public const string FontFamily = "Segoe UI";

        public const double Caption = 12;
        public const double Body = 13;
        public const double Emphasized = 15;
        public const double Title = 18;
        public const double Display = 22;
    }

    public static class Line
    {
        public const double Hairline = 1;
        public const double Emphasis = 2;
    }

    public static class Motion
    {
        /// <summary>悬停/按下这类即时反馈。</summary>
        public const int FastMs = 120;

        /// <summary>展开/收起这类布局变化。</summary>
        public const int NormalMs = 200;
    }
}
