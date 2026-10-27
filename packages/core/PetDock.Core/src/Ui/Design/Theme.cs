// 设计令牌（docs/18 §2.6「外观」）：全产品唯一的配色、字号阶梯、间距刻度与圆角来源。
//
// 为什么要一个集中文件：用户对界面的判词是「难看、复古」（2026-10-27），根因不是某几个颜色不对，
// 而是每个窗口各自用 SystemColors 和默认控件外观，彼此不成体系。所以颜色、字号、间距、圆角
// 一律从这里取；界面代码里不允许再出现颜色字面量（判据 ui-theme-no-raw-colors）。
//
// 配色与桌宠、托盘图标同源：深蓝底 + 奶油白 + 粉色点缀（assets/icons 的像素猫用的就是这三色）。
//
// 缩放口径（2026-10-27 重做，用户要求「不要有缩放问题」）：
//   · 一律**显式**换算：尺寸走 Dip(逻辑像素 × DPI/96)，字号走 FontFor(角色, DPI) 的像素字号。
//   · **不用** WinForms 的 AutoScaleMode.Dpi：它会在已经乘过 DPI 的尺寸上再乘一次
//     （150% 下变成 2.25 倍），而本文件的每个尺寸都已经是设备像素。两套缩放同时开，
//     结果一定在某一档上把文字挤出去。所以 AutoScaleMode 恒为 None，缩放只有这一个出处。
//   · 判据因此可以在进程内模拟 100%/125%/150%/200%：SetSimulatedDpi 让 Dip 与字号同时放大，
//     布局代码一行都不用改（AppearanceChecks 的 ui-text-fits-* 就是这么量的）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

/// <summary>字号角色。取 Font 时按角色而不是散落各处的 new Font(...)。</summary>
internal enum TypeRole
{
    /// <summary>辅助说明、状态栏、提示。</summary>
    Caption,

    /// <summary>正文、列表行、按钮。</summary>
    Body,

    /// <summary>正文加粗（选中项、关键数值）。</summary>
    BodyStrong,

    /// <summary>小标题（分区标题、卡片标题）。</summary>
    Heading,

    /// <summary>页面标题。</summary>
    Title,
}

/// <summary>
/// 设计令牌。颜色用 0xRRGGBB 常量，避免每处各写一遍。
/// 所有尺寸单位是「逻辑像素」（96 DPI 下的像素），经 <see cref="Dip"/> 换成设备像素。
/// 材质的枚举与绘制透明度在 Theme.Material.cs（同一个 partial 类）。
/// </summary>
internal static partial class Theme
{
    // ---- 配色（2026-10-28 现代化重排：中性石板灰 + 冷蓝点缀，替代原先的深蓝+粉） ----
    //
    // 为什么换：用户 2026-10-28 的原话是「好丑啊，能不能做成 dsh 那种现代化的界面」。
    // 旧配色的深蓝底 + 奶油白 + 粉点缀读起来是「复古像素风」，与母体其余部分（自绘控件、
    // 圆角、细分隔）不成一套。新色板把色彩压到近乎中性，只留一个冷蓝点缀给出可点的地方。

    /// <summary>窗口底色：近黑石板灰，全产品最底层。</summary>
    public static readonly Color Bg = Color.FromArgb(0x0F, 0x14, 0x1A);

    /// <summary>左侧导航栏底：比内容区再深一档，用明度而不是分割线分开。</summary>
    public static readonly Color BgDeep = Color.FromArgb(0x0A, 0x0E, 0x13);

    /// <summary>卡片／列表行底：比底色亮一档，用来分块。</summary>
    public static readonly Color Surface = Color.FromArgb(0x16, 0x1D, 0x26);

    /// <summary>悬停／选中底：再亮一档。</summary>
    public static readonly Color SurfaceHover = Color.FromArgb(0x1E, 0x27, 0x33);

    /// <summary>描边、分隔线：低对比，只做「分块」不做「边框感」。</summary>
    public static readonly Color Line = Color.FromArgb(0x25, 0x2F, 0x3C);

    /// <summary>主文字。</summary>
    public static readonly Color Text = Color.FromArgb(0xE8, 0xEE, 0xF6);

    /// <summary>次要文字：标题下的说明、列名。</summary>
    public static readonly Color TextDim = Color.FromArgb(0x93, 0xA1, 0xB3);

    /// <summary>更弱的文字：占位、空态说明。</summary>
    public static readonly Color TextMuted = Color.FromArgb(0x67, 0x75, 0x86);

    /// <summary>点缀色：选中态、主按钮、强调数值。</summary>
    public static readonly Color Accent = Color.FromArgb(0x6C, 0x8E, 0xF5);

    /// <summary>点缀色悬停。</summary>
    public static readonly Color AccentHover = Color.FromArgb(0x8A, 0xA8, 0xFF);

    /// <summary>点缀色的低饱和底：选中行的底色、选中项右侧的浅色胶囊。</summary>
    public static readonly Color AccentSoft = Color.FromArgb(0x1B, 0x25, 0x3B);

    /// <summary>主按钮上的文字：冷蓝底上要用近黑才读得清（白字在 0x6C8EF5 上对比度不足）。</summary>
    public static readonly Color OnAccent = Color.FromArgb(0x0A, 0x0E, 0x14);

    /// <summary>正常／通过：薄荷绿。</summary>
    public static readonly Color Good = Color.FromArgb(0x74, 0xD3, 0xAE);

    /// <summary>警告：琥珀。</summary>
    public static readonly Color Warn = Color.FromArgb(0xE8, 0xC2, 0x71);

    /// <summary>失败／禁用：赤陶。</summary>
    public static readonly Color Bad = Color.FromArgb(0xEF, 0x7A, 0x6B);

    /// <summary>失败块的底：赤陶压暗，保证上面的 <see cref="Bad"/> 与 <see cref="Text"/> 都读得清。</summary>
    public static readonly Color BadSurface = Color.FromArgb(0x36, 0x20, 0x22);

    /// <summary>提示块的底：琥珀压暗（「重启生效」这类需要用户注意但不失败的话）。</summary>
    public static readonly Color WarnSurface = Color.FromArgb(0x32, 0x2A, 0x1C);

    // ---- 间距刻度（逻辑像素，4 的倍数） ----

    public const int SpaceXs = 4;
    public const int SpaceSm = 8;
    public const int SpaceMd = 12;
    public const int SpaceLg = 16;
    public const int SpaceXl = 24;
    public const int SpaceXxl = 32;

    // ---- 圆角与线宽 ----

    public const int RadiusSm = 6;
    public const int RadiusMd = 10;
    public const int RadiusLg = 14;

    // ---- 材质（docs/18 §2.7）：实现与令牌在 Theme.Material.cs（本文件只管配色/字号/间距） ----

    // ---- 字号阶梯（点） ----

    private static readonly Dictionary<TypeRole, float> PointSize = new()
    {
        [TypeRole.Caption] = 9f,
        [TypeRole.Body] = 10f,
        [TypeRole.BodyStrong] = 10f,
        [TypeRole.Heading] = 13f,
        [TypeRole.Title] = 18f,
    };

    /// <summary>
    /// UI 字体族。中英文混排优先用雅黑（中文字形完整、行高稳定），
    /// 没有则退回 Segoe UI（英文环境的标准界面字）。
    /// </summary>
    private static readonly string Family = PickFamily();

    private static readonly Dictionary<(TypeRole, int), Font> Cache = new();

    private static int _simulatedDpi;
    private static int _systemDpi;

    /// <summary>
    /// 判据用的模拟 DPI（0 = 关闭）。开启后 <see cref="Dip"/>、<see cref="DpiOf"/>、
    /// <see cref="Font(TypeRole)"/> 一律按它算，于是同一次运行里能量出 100%/125%/150%/200%
    /// 四档布局。**只给判据用**：生产路径从不设置它。
    /// </summary>
    public static void SetSimulatedDpi(int dpi) => _simulatedDpi = dpi > 0 ? dpi : 0;

    /// <summary>当前模拟 DPI（0 = 没有模拟）。</summary>
    public static int SimulatedDpi => _simulatedDpi;

    /// <summary>系统 DPI（进程启动时的主屏 DPI）。取不到就按 96 算。</summary>
    public static int SystemDpi
    {
        get
        {
            if (_systemDpi > 0)
            {
                return _systemDpi;
            }

            try
            {
                using Graphics g = Graphics.FromHwnd(IntPtr.Zero);
                _systemDpi = g.DpiX > 0 ? (int)Math.Round(g.DpiX) : 96;
            }
            catch (Exception)
            {
                _systemDpi = 96;
            }

            return _systemDpi;
        }
    }

    /// <summary>当前该用的 DPI：模拟档优先，否则主屏 DPI。</summary>
    public static int CurrentDpi => _simulatedDpi > 0 ? _simulatedDpi : SystemDpi;

    /// <summary>按角色取字体（当前 DPI）。同角色同 DPI 复用同一实例（Font 是 GDI 句柄，不能每帧新建）。</summary>
    public static Font Font(TypeRole role) => FontFor(role, CurrentDpi);

    /// <summary>
    /// 按角色与目标 DPI 取字体。字号用**像素**而不是点：点的渲染由设备 DPI 决定，
    /// 那是第二套缩放（本文件开头的口径），这里要的是「10 点在这个 DPI 下等于多少像素」的显式答案。
    /// </summary>
    public static Font FontFor(TypeRole role, int dpi)
    {
        if (dpi <= 0)
        {
            dpi = CurrentDpi;
        }

        (TypeRole, int) key = (role, dpi);
        if (Cache.TryGetValue(key, out Font? cached))
        {
            return cached;
        }

        FontStyle style = role == TypeRole.BodyStrong ? FontStyle.Bold : FontStyle.Regular;
        float px = PointSize[role] * dpi / 72f;
        var font = new Font(Family, px, style, GraphicsUnit.Pixel);
        Cache[key] = font;
        return font;
    }

    /// <summary>某个控件所在 DPI 下该用的字体（自绘时必须用它，而不是 <see cref="Font(TypeRole)"/>）。</summary>
    public static Font FontFor(Control control, TypeRole role) => FontFor(role, DpiOf(control));

    /// <summary>逻辑像素 → 设备像素（按给定 DPI 取整）。</summary>
    public static int Dip(int logical, int dpi)
    {
        if (dpi <= 0)
        {
            dpi = CurrentDpi;
        }

        return (int)Math.Round(logical * dpi / 96.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>逻辑像素 → 设备像素，用控件当前 DPI。</summary>
    public static int Dip(Control control, int logical) => Dip(logical, DpiOf(control));

    /// <summary>控件当前 DPI：模拟档优先，其次控件自己的 DeviceDpi，最后主屏 DPI。</summary>
    public static int DpiOf(Control control)
    {
        if (_simulatedDpi > 0)
        {
            return _simulatedDpi;
        }

        try
        {
            int dpi = control.DeviceDpi;
            return dpi > 0 ? dpi : SystemDpi;
        }
        catch (Exception)
        {
            return SystemDpi;
        }
    }

    /// <summary>
    /// 把窗口本身套上主题：底色、字体、缩放口径。所有自有窗口都要过这一道。
    /// AutoScaleMode 恒为 None —— 本文件的尺寸已经是设备像素，再让框架乘一次就是两套缩放。
    /// </summary>
    public static void Apply(Form form)
    {
        form.BackColor = Bg;
        form.ForeColor = Text;
        form.Font = Font(TypeRole.Body);
        form.AutoScaleMode = AutoScaleMode.None;

        // 材质（docs/18 §2.7）：不透明时这一步只是把窗口恢复成实心；
        // 磨砂/玻璃在这一步要到系统模糊并把整窗按比例透过去。
        ApplyMaterial(form);

        // DPI 变化（拖到另一台显示器）：显式口径下框架不会动手，这里按比例缩一次并留一行诊断。
        // 只在真的变了的时候做；比例 1 时什么都不碰。
        form.DpiChanged += (_, e) =>
        {
            int before = e.DeviceDpiOld > 0 ? e.DeviceDpiOld : SystemDpi;
            int after = e.DeviceDpiNew;
            if (after <= 0 || before == after)
            {
                return;
            }

            float ratio = after / (float)before;
            form.Font = FontFor(TypeRole.Body, after);
            form.Scale(new SizeF(ratio, ratio));
            form.PerformLayout();
            Compositor.KernelLog.Write($"ui-dpi-changed from={before} to={after} ratio={ratio:0.00}");
        };
    }

    /// <summary>给标签套上角色与颜色（Label 的默认色是系统色，深色底上读不清）。</summary>
    public static void StyleLabel(Label label, TypeRole role, Color? color = null)
    {
        label.Font = Font(role);
        label.ForeColor = color ?? Text;
        label.BackColor = Color.Transparent;
    }

    /// <summary>
    /// 对话框底部的按钮条：深色底 + 按 DPI 取的内边距 + 右对齐（默认键在最右）。
    /// 自绘按钮不自动撑开行高，所以高度也在这里定死一个——免得每个窗各写一遍同样的三个数。
    /// </summary>
    public static FlowLayoutPanel DialogButtonBar(Control owner)
    {
        int dpi = DpiOf(owner);
        int padX = Dip(dpi, SpaceXl);
        int padY = Dip(dpi, SpaceSm);
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = Dip(dpi, SpaceXxl + SpaceLg),
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(padX, padY, padX, padY),
            WrapContents = false,
            BackColor = Bg,
        };
    }

    private static string PickFamily()
    {
        string[] candidates = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" };
        foreach (string name in candidates)
        {
            try
            {
                using var probe = new Font(name, 9f);
                if (string.Equals(probe.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
            catch (ArgumentException)
            {
                // 字体名不合法（极少见）：试下一个候选。
            }
        }

        return Control.DefaultFont.FontFamily.Name;
    }
}