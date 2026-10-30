// 窗口材质（原规格 18 §2.7）：磨砂 / 玻璃。
//
// 能做到什么、做不到什么，先说清楚：
//   · 真·壁纸模糊靠系统合成。Win11 22H2+ 有 DWMWA_SYSTEMBACKDROP_TYPE（Mica/亚克力），
//     更早的系统只有未公开的 SetWindowCompositionAttribute（AERO 玻璃/亚克力的老路）。
//     两条路这里都试，谁先成谁算数。
//   · WinForms 的客户区永远是「画上去的」，DWM 背景层透不穿 —— 所以真正让背景可见的是
//     Form.Opacity（等价于 WS_EX_LAYERED + LWA_ALPHA），整窗按比例透到系统合成的那层模糊上。
//   · 系统两条路都给不了模糊时，只剩透明度：仍然比不透明好看，但要如实写进诊断行，
//     设置页据此告诉用户「这台机器只能给到透明，给不了模糊」。
//
// 判据口径：这里只证「接口通了、参数落下了、失败有回退」，画面好不好看只能人眼验收。
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

/// <summary>一次材质应用的结果（诊断与设置页共用）。</summary>
internal readonly record struct MaterialReport(
    MaterialKind Requested,
    MaterialKind Applied,
    bool Blurred,
    bool RoundedCorners,
    bool Layered,
    string Note)
{
    /// <summary>
    /// 一行诊断：谁要求的、实际给了什么、有没有模糊、窗口是不是分层态。
    /// layered 必须出现在这里：成品的磨砂/玻璃**不是**「一层透明度」，而整窗透明度
    /// （WS_EX_LAYERED + LWA_ALPHA）正是「内容一起被淡掉」的那个做法。
    /// 把这一个事实写进每一行，才有一条能一眼看出「它又退回成简单透明了」的读数。
    /// </summary>
    public string Describe() =>
        $"requested={Requested} applied={Applied} blurred={Blurred} corners={RoundedCorners}"
        + $" layered={Layered} note={Note}";
}

/// <summary>把材质落到一个窗口上。所有 P/Invoke 都只碰窗口属性，不碰布局。</summary>
internal static class MaterialHost
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowBorderColor = 34;
    private const int DwmWindowCaptionColor = 35;
    private const int DwmWindowTextColor = 36;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmSystemBackdropType = 38;
    private const int BackdropNone = 1;
    private const int BackdropMica = 2;
    private const int BackdropAcrylic = 3;
    private const int CornerRound = 2;

    // SetWindowCompositionAttribute 的 AccentState
    private const int AccentDisabled = 0;
    private const int AccentBlurBehind = 3;
    private const int AccentAcrylicBlurBehind = 4;

    /// <summary>空报告（不透明材质、或没有窗口时）。</summary>
    public static MaterialReport None { get; } = new(MaterialKind.Opaque, MaterialKind.Opaque, false, false, false, "opaque");

    /// <summary>
    /// 把材质套到窗口上。窗口还没建句柄时先记下请求，等 <see cref="Control.HandleCreated"/> 再落。
    /// 返回「实际给了什么」——调用方（设置页/诊断）拿它说话，不假设请求一定兑现。
    /// </summary>
    public static MaterialReport Apply(Form form, MaterialKind kind)
    {
        if (!form.IsHandleCreated)
        {
            // 同一个窗口重复请求（面板每次开都会走一遍 Appearance）只留一个钩子：
            // 旧写法每次都 +=，句柄一到就有 N 个相同的 Apply 连着跑（实机日志里
            // pending-handle 出现过 10 次，也就是句柄创建那一刻会连做 10 遍）。
            Pending entry = PendingHooks.GetOrCreateValue(form);
            if (entry.Handler is not null)
            {
                form.HandleCreated -= entry.Handler;
            }

            EventHandler handler = (_, _) => Apply(form, kind);
            entry.Handler = handler;
            form.HandleCreated += handler;
            return new MaterialReport(kind, MaterialKind.Opaque, false, false, false, "pending-handle");
        }

        IntPtr hwnd = form.Handle;
        TryDarkMode(hwnd);
        TryCaptionColors(hwnd);

        // 圆角是**窗口**的属性，不是材质的属性：
        // 旧实现在不透明档直接 return，于是「切回不透明」会把圆角一起去掉 ——
        // 用户看到的是「选了不透明，窗口突然变成直角」。这一条与材质无关，永远做。
        bool rounded = TryRoundedCorners(hwnd);

        if (kind == MaterialKind.Opaque)
        {
            form.Opacity = 1.0;
            TryClearBackdrop(hwnd);
            TryClearBlur(hwnd);
            return new MaterialReport(kind, kind, false, rounded, false, "opaque");
        }

        // 两条系统路径**互补，不是二选一**（2026-10-30）。旧写法是 `A || B` 短路：
        // Win11 上 DWMWA_SYSTEMBACKDROP_TYPE 会返回成功，于是永远不会去试亚克力那条；
        // 而系统背景类型只作用于**非客户区**（窗口客户区是自己画的，DWM 那层透不穿），
        // 结果就是「要到了背景却没有一处看得见」—— 用户看到的效果只剩下面的整窗透明度。
        bool backdrop = TrySystemBackdrop(hwnd, kind);
        bool accent = TryAccentBlur(hwnd, kind);
        bool readBack = ReadBackdrop(hwnd, kind);

        // 整窗透明度只在**要不到系统合成**时用。
        // 它是「整窗按比例淡下去」，文字跟着一起糊 —— 那正是用户说的「只是简单的透明度变化」。
        // 要到了模糊就不再淡：让系统那层去做模糊，内容保持清晰。
        // 两者不能叠加：叠加等于把透明度算两遍（绘制 alpha × 窗口 alpha），越选越糊。
        form.Opacity = backdrop || accent ? 1.0 : Theme.PaintAlpha(kind) / 255.0;
        bool layered = IsLayered(form);

        string note = accent
            ? (kind == MaterialKind.Glass ? "acrylic-accent" : "blur-accent")
            : backdrop ? "backdrop-nonclient" : "translucent-only";
        if (readBack)
        {
            note += "+backdrop-readback";
        }

        // Applied 说实话：两条系统路径都没成时，用户实际拿到的**不是**他要的那一档，
        // 而是「一层透明度」。旧实现这里报 Glass（连请求的是 Frosted 都报 Glass），
        // 设置页据此告诉用户「已经是玻璃了」——那是撒谎。
        MaterialKind applied = accent || backdrop ? kind : MaterialKind.Opaque;
        return new MaterialReport(kind, applied, accent || backdrop, rounded, layered, note);
    }

    /// <summary>等句柄的钩子（每窗口一个；见 Apply 里的去重说明）。</summary>
    private sealed class Pending
    {
        public EventHandler? Handler { get; set; }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, Pending> PendingHooks = new();

    /// <summary>窗口现在是不是分层态（<c>WS_EX_LAYERED</c>）。判据读它证「没有把内容一起淡掉」。</summary>
    public static bool IsLayered(Form form) =>
        (Compositor.Win32.GetWindowLongPtr(form.Handle, Compositor.Win32.GWL_EXSTYLE).ToInt64()
            & Compositor.Win32.WS_EX_LAYERED) != 0;

    /// <summary>把系统背景类型读回来（证「参数真的落下了」，不是只看调用返回 0）。</summary>
    private static bool ReadBackdrop(IntPtr hwnd, MaterialKind kind)
    {
        try
        {
            int want = kind == MaterialKind.Glass ? BackdropAcrylic : BackdropMica;
            return DwmGetWindowAttribute(hwnd, DwmSystemBackdropType, out int got, sizeof(int)) == 0
                && got == want;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 玻璃比磨砂更透：给设置页/诊断看的一处口径。
    /// 数值来自 <see cref="Theme.PaintAlpha"/>（同一张表），不再各自写一份 ——
    /// 两份定义迟早会漂移成「绘制说不透明、诊断说半透明」。
    /// </summary>
    public static double OpacityOf(MaterialKind kind) => Theme.PaintAlpha(kind) / 255.0;

    /// <summary>
    /// 把系统标题栏刷成面板底色（2026-10-28 用户截图：标题栏是 Windows 强调色亮黄，与界面完全脱节）。
    /// DWM 35/36/34 是 Win11 才有的；老系统返回非 0，保持默认外观即可。
    /// </summary>
    private static void TryCaptionColors(IntPtr hwnd)
    {
        try
        {
            int caption = ColorRef(Theme.Bg);
            int text = ColorRef(Theme.Text);
            int border = ColorRef(Theme.Line);
            DwmSetWindowAttribute(hwnd, DwmWindowCaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmWindowTextColor, ref text, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmWindowBorderColor, ref border, sizeof(int));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // 老系统没有标题栏取色属性：默认外观即可。
        }
    }

    /// <summary>COLORREF 是 0x00BBGGRR，不是 ARGB。</summary>
    private static int ColorRef(System.Drawing.Color color) => color.R | (color.G << 8) | (color.B << 16);

    private static bool TrySystemBackdrop(IntPtr hwnd, MaterialKind kind)
    {
        try
        {
            int type = kind == MaterialKind.Glass ? BackdropAcrylic : BackdropMica;
            int hr = DwmSetWindowAttribute(hwnd, DwmSystemBackdropType, ref type, sizeof(int));
            return hr == 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void TryClearBackdrop(IntPtr hwnd)
    {
        try
        {
            int none = BackdropNone;
            DwmSetWindowAttribute(hwnd, DwmSystemBackdropType, ref none, sizeof(int));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // 老系统上没有这个属性：本来就没有背景层，不用清。
        }
    }

    private static bool TryAccentBlur(IntPtr hwnd, MaterialKind kind)
    {
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = kind == MaterialKind.Glass ? AccentAcrylicBlurBehind : AccentBlurBehind,
                AccentFlags = 2,

                // 0xAABBGGRR：ABGR 顺序（不是 ARGB），alpha 越低越透。
                GradientColor = kind == MaterialKind.Glass ? 0x33101A24 : 0x66101A24,
            };

            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, buffer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = 19, // WCA_ACCENT_POLICY
                    Data = buffer,
                    SizeOfData = size,
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void TryClearBlur(IntPtr hwnd)
    {
        try
        {
            var accent = new AccentPolicy { AccentState = AccentDisabled, AccentFlags = 0, GradientColor = 0 };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, buffer, false);
                var data = new WindowCompositionAttributeData { Attribute = 19, Data = buffer, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // 没有这条未公开接口的系统上也不会有残留。
        }
    }

    private static void TryDarkMode(IntPtr hwnd)
    {
        try
        {
            int on = Theme.IsLight ? 0 : 1;

            // 20 = Win10 2004+ 的属性号；19 是 1809~1909 用过的旧号。
            // 两个都试：失败的那次只是返回非 0，不影响窗口本身。
            // 浅色主题要显式写 0（「不要深色标题栏」），否则系统会沿用上一档的深色装饰。
            if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // Win10 1809 之前没有这个属性；窗口照样能变深色，只是标题栏不跟着变。
        }
    }

    private static bool TryRoundedCorners(IntPtr hwnd)
    {
        try
        {
            int round = CornerRound;
            return DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref round, sizeof(int)) == 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>读回窗口属性（证参数真的落下了；只看 Set 的返回值会漏掉「返回 0 但没生效」）。</summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}