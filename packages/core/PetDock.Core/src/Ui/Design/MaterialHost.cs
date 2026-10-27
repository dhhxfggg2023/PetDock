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
    string Note)
{
    /// <summary>一行诊断：谁要求的、实际给了什么、有没有模糊。</summary>
    public string Describe() =>
        $"requested={Requested} applied={Applied} blurred={Blurred} corners={RoundedCorners} note={Note}";
}

/// <summary>把材质落到一个窗口上。所有 P/Invoke 都只碰窗口属性，不碰布局。</summary>
internal static class MaterialHost
{
    private const int DwmUseImmersiveDarkMode = 20;
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
    public static MaterialReport None { get; } = new(MaterialKind.Opaque, MaterialKind.Opaque, false, false, "opaque");

    /// <summary>
    /// 把材质套到窗口上。窗口还没建句柄时先记下请求，等 <see cref="Control.HandleCreated"/> 再落。
    /// 返回「实际给了什么」——调用方（设置页/诊断）拿它说话，不假设请求一定兑现。
    /// </summary>
    public static MaterialReport Apply(Form form, MaterialKind kind)
    {
        if (!form.IsHandleCreated)
        {
            MaterialReport pending = new(kind, kind, false, false, "pending-handle");
            form.HandleCreated += (_, _) => Apply(form, kind);
            return pending;
        }

        IntPtr hwnd = form.Handle;
        TryDarkMode(hwnd);

        if (kind == MaterialKind.Opaque)
        {
            form.Opacity = 1.0;
            TryClearBackdrop(hwnd);
            TryClearBlur(hwnd);
            return new MaterialReport(kind, kind, false, false, "opaque");
        }

        // 先要到模糊，再调透明度：反过来会让第一帧闪成纯透明。
        bool blurred = TrySystemBackdrop(hwnd, kind) || TryAccentBlur(hwnd, kind);
        form.Opacity = kind == MaterialKind.Glass ? 0.80 : 0.92;

        bool rounded = TryRoundedCorners(hwnd);
        string note = blurred
            ? (kind == MaterialKind.Glass ? "acrylic" : "mica-or-blur")
            : "translucent-only";
        return new MaterialReport(kind, blurred ? kind : MaterialKind.Glass, blurred, rounded, note);
    }

    /// <summary>玻璃比磨砂更透：给设置页/诊断看的一处口径。</summary>
    public static double OpacityOf(MaterialKind kind) => kind switch
    {
        MaterialKind.Glass => 0.80,
        MaterialKind.Frosted => 0.92,
        _ => 1.0,
    };

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