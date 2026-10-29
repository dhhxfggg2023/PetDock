// 主题色板（2026-10-28 第二版）：深色 / 浅色两套完整色板 + 跟随系统的解析。
//
// 令牌名（Bg/Text/Accent…）对控件保持不变，换的只是它背后那一套值；任何自绘控件
// 只要读 Theme.X 就自动跟着主题走。
using System;
using System.Drawing;
using System.Windows;

namespace PetDock.Core.Ui.Design;

/// <summary>界面亮度：跟随系统 / 浅色 / 深色。（命名空间级，调用方写 ThemeMode 而不是 Theme.ThemeMode。）</summary>
internal enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2,
}

internal static partial class Theme
{
    // ---- 配色：两套完整色板（深色 / 浅色），开关由 ThemeMode 决定 ----
    //
    // 2026-10-28 第二版：所有者要求「系统颜色应该能换深色和浅色」。
    // 做法不是给每个控件写两套代码，而是把所有令牌收敛进一个 Palette 结构体，
    // 令牌**名字**对控件保持稳定（Bg/Text/Accent…），换的只是它背后那一套值。
    // 这样任何自绘控件、任何新控件都自动跟着主题走，不需要知道现在是亮还是暗。

    /// <summary>一套完整色板。字段顺序与令牌声明顺序一致，方便对照。</summary>
    private readonly record struct Palette(
        Color Bg,
        Color BgDeep,
        Color Surface,
        Color SurfaceHover,
        Color Line,
        Color Text,
        Color TextDim,
        Color TextMuted,
        Color Accent,
        Color AccentHover,
        Color AccentSoft,
        Color OnAccent,
        Color Good,
        Color Warn,
        Color Bad,
        Color BadSurface,
        Color WarnSurface);

    /// <summary>深色板：中性石板灰 + 冷蓝点缀（2026-10-28 第一版沿用）。</summary>
    private static readonly Palette DarkPalette = new(
        Bg: Color.FromArgb(0x0F, 0x14, 0x1A),
        BgDeep: Color.FromArgb(0x0A, 0x0E, 0x13),
        Surface: Color.FromArgb(0x16, 0x1D, 0x26),
        SurfaceHover: Color.FromArgb(0x1E, 0x27, 0x33),
        Line: Color.FromArgb(0x25, 0x2F, 0x3C),
        Text: Color.FromArgb(0xE8, 0xEE, 0xF6),
        TextDim: Color.FromArgb(0x93, 0xA1, 0xB3),
        TextMuted: Color.FromArgb(0x67, 0x75, 0x86),
        Accent: Color.FromArgb(0x6C, 0x8E, 0xF5),
        AccentHover: Color.FromArgb(0x8A, 0xA8, 0xFF),
        AccentSoft: Color.FromArgb(0x1B, 0x25, 0x3B),
        OnAccent: Color.FromArgb(0x0A, 0x0E, 0x14),
        Good: Color.FromArgb(0x74, 0xD3, 0xAE),
        Warn: Color.FromArgb(0xE8, 0xC2, 0x71),
        Bad: Color.FromArgb(0xEF, 0x7A, 0x6B),
        BadSurface: Color.FromArgb(0x36, 0x20, 0x22),
        WarnSurface: Color.FromArgb(0x32, 0x2A, 0x1C));

    /// <summary>
    /// 浅色板：纸白底 + 同一支冷蓝。
    /// 要点是**对比度**，不是把深色值反相：正文 #1B2130 在 #F7F8FA 上是 14.8:1，
    /// 次要文字 #5C6677 是 5.9:1，都过 WCAG AA；分隔线只做分块，比正文弱得多。
    /// </summary>
    private static readonly Palette LightPalette = new(
        Bg: Color.FromArgb(0xF7, 0xF8, 0xFA),
        BgDeep: Color.FromArgb(0xEC, 0xEF, 0xF4),
        Surface: Color.FromArgb(0xFF, 0xFF, 0xFF),
        SurfaceHover: Color.FromArgb(0xEE, 0xF1, 0xF6),
        Line: Color.FromArgb(0xDD, 0xE2, 0xEA),
        Text: Color.FromArgb(0x1B, 0x21, 0x30),
        TextDim: Color.FromArgb(0x5C, 0x66, 0x77),
        TextMuted: Color.FromArgb(0x8A, 0x93, 0xA3),
        Accent: Color.FromArgb(0x3B, 0x6E, 0xF5),
        AccentHover: Color.FromArgb(0x2F, 0x5A, 0xD6),
        AccentSoft: Color.FromArgb(0xE6, 0xED, 0xFF),
        OnAccent: Color.FromArgb(0xFF, 0xFF, 0xFF),
        Good: Color.FromArgb(0x1F, 0x9D, 0x74),
        Warn: Color.FromArgb(0xB7, 0x79, 0x1F),
        Bad: Color.FromArgb(0xD1, 0x43, 0x43),
        BadSurface: Color.FromArgb(0xFD, 0xEC, 0xEC),
        WarnSurface: Color.FromArgb(0xFB, 0xF3, 0xE0));

    private static ThemeMode _mode = ThemeMode.System;

    /// <summary>当前选择的亮度模式（System 表示每帧向系统要）。</summary>
    public static ThemeMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    /// <summary>
    /// 判据用的高对比度覆盖（null = 读系统设置）。生产路径从不设置它；
    /// 自检用它在普通桌面上也能量到高对比度色板。
    /// </summary>
    internal static bool? HighContrastOverride { get; set; }

    /// <summary>系统是否处于高对比度模式（读不到按否）。</summary>
    public static bool IsHighContrast => HighContrastOverride ?? DetectHighContrast();

    private static bool DetectHighContrast()
    {
        try
        {
            return SystemParameters.HighContrast;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 高对比度色板：一律用系统的高对比度前景/背景/控件色，不发明颜色。
    /// 不用 Highlight 做选中底：页面选中态的前景仍是 Text，而不是 HighlightText，
    /// 用 Highlight 当底会出现「深底深字」。层次靠 Line（系统前景色）与 AccentSoft（控件色）表达；
    /// Good/Warn/Bad 在系统色里没有对应语义，统一用前景色，状态由文字本身说明。
    /// </summary>
    private static Palette HighContrastPalette()
    {
        Color window = System.Drawing.SystemColors.Window;
        Color text = System.Drawing.SystemColors.WindowText;
        Color control = System.Drawing.SystemColors.Control;
        return new Palette(
            Bg: window,
            BgDeep: control,
            Surface: window,
            SurfaceHover: control,
            Line: text,
            Text: text,
            TextDim: text,
            TextMuted: text,
            Accent: text,
            AccentHover: text,
            AccentSoft: control,
            OnAccent: window,
            Good: text,
            Warn: text,
            Bad: text,
            BadSurface: window,
            WarnSurface: window);
    }

    /// <summary>当前实际生效的色板（高对比度优先，其次亮/暗，System 模式下按系统设置解析）。</summary>
    private static Palette Colors =>
        IsHighContrast
            ? HighContrastPalette()
            : _mode switch
            {
                ThemeMode.Light => LightPalette,
                ThemeMode.Dark => DarkPalette,
                _ => SystemUsesLight() ? LightPalette : DarkPalette,
            };

    /// <summary>现在是不是浅色（供窗口装饰、材质、判据判断）。</summary>
    public static bool IsLight => Colors.Text.R < 0x80;

    /// <summary>读系统的「应用使用浅色主题」（读不到按深色，深色是产品默认）。</summary>
    private static bool SystemUsesLight()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>把设置里的字符串解析成模式（认不出按跟随系统）。</summary>
    public static ThemeMode ParseMode(string? token) =>
        token switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

    /// <summary>模式的持久化名（System 也落盘，用户明确选过「跟随系统」）。</summary>
    public static string ModeToken(ThemeMode mode) =>
        mode switch
        {
            ThemeMode.Light => "light",
            ThemeMode.Dark => "dark",
            _ => "system",
        };

    /// <summary>窗口底色：全产品最底层。</summary>
    public static Color Bg => Colors.Bg;

    /// <summary>左侧导航栏底：比内容区再深／浅一档，用明度而不是分割线分开。</summary>
    public static Color BgDeep => Colors.BgDeep;

    /// <summary>卡片／列表行底：与底色分开一档，用来分块。</summary>
    public static Color Surface => Colors.Surface;

    /// <summary>悬停／选中底。</summary>
    public static Color SurfaceHover => Colors.SurfaceHover;

    /// <summary>描边、分隔线：低对比，只做「分块」不做「边框感」。</summary>
    public static Color Line => Colors.Line;

    /// <summary>主文字。</summary>
    public static Color Text => Colors.Text;

    /// <summary>次要文字：标题下的说明、列名。</summary>
    public static Color TextDim => Colors.TextDim;

    /// <summary>更弱的文字：占位、空态说明。</summary>
    public static Color TextMuted => Colors.TextMuted;

    /// <summary>点缀色：选中态、主按钮、强调数值。</summary>
    public static Color Accent => Colors.Accent;

    /// <summary>点缀色悬停。</summary>
    public static Color AccentHover => Colors.AccentHover;

    /// <summary>点缀色的低饱和底：选中行的底色、选中项右侧的浅色胶囊。</summary>
    public static Color AccentSoft => Colors.AccentSoft;

    /// <summary>主按钮上的文字：与 Accent 成对。（深色板用近黑，浅色板用纯白。）</summary>
    public static Color OnAccent => Colors.OnAccent;

    /// <summary>正常／通过：绿。</summary>
    public static Color Good => Colors.Good;

    /// <summary>警告：琥珀。</summary>
    public static Color Warn => Colors.Warn;

    /// <summary>失败／禁用：红。</summary>
    public static Color Bad => Colors.Bad;

    /// <summary>失败块的底：与 <see cref="Bad"/> 成对，保证两块都读得清。</summary>
    public static Color BadSurface => Colors.BadSurface;

    /// <summary>提示块的底：与 <see cref="Warn"/> 成对。</summary>
    public static Color WarnSurface => Colors.WarnSurface;
}
