// LayeredFrameWindow 的消息处理部分（C-03c：主文件超 400 行，WndProc 拆到这里作 partial 的另一半；
// 鼠标拖拽（OnMouseDown/Move/Up）留主文件——它们只操作实例字段，不值得为搬行数改写 this 绑定）。
// partial 而不是静态助手：WndProc 要调 DefWndProc / ApplyFrameToWindowSize，两者都是实例成员，
// 静态助手够不着（也不需要为此把它们放开成 internal —— 那会让「谁能改窗口」多出一个入口）。
// 拆分只是行数搬运：每条消息的语义注释随代码走，本文件不留第二份解释。
using System;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using PetDock.Core.PetRuntime;

namespace PetDock.Core.Compositor;

internal sealed partial class LayeredFrameWindow
{
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_DPICHANGED)
        {
            HandleDpiChanged(ref m);
        }
        else if (m.Msg == Win32.WM_ACTIVATEAPP)
        {
            // core.window.focus：wParam≠0 = 有焦点。NOACTIVATE 窗口只在程序切换前台时收到它。
            RaiseWindowEvent(EventCatalog.WindowFocus, new JsonObject
            {
                ["focused"] = m.WParam != IntPtr.Zero,
            });
        }

        base.WndProc(ref m);
    }

    private void HandleDpiChanged(ref Message m)
    {
        if (m.LParam == IntPtr.Zero)
        {
            // 纵深防御：WM_DPICHANGED 的 lParam 必须是 RECT*，为 0 时按不处理返回，
            // 绝不对空指针做 PtrToStructure（那是访问冲突，进程直接死）。
            KernelLog.Write("dpi-changed 收到空 lParam，已忽略");
            m.Result = IntPtr.Zero;
            return;
        }

        uint dpi = unchecked((uint)m.WParam.ToInt64() & 0xFFFF);
        RECT suggested = Marshal.PtrToStructure<RECT>(m.LParam);
        KernelLog.Write($"dpi-changed dpi={dpi} suggested={suggested.Width}x{suggested.Height}");

        Win32.SetWindowPos(Handle, IntPtr.Zero, suggested.Left, suggested.Top,
            suggested.Width, suggested.Height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);

        // DPI 变了 ⇒ 目标物理尺寸也变了。帧缓冲要跟着窗口的实测矩形重建并重出这一帧，
        // 否则 DIB 与窗口尺寸不一致，画面会在换屏之后静默消失。
        ApplyFrameToWindowSize();
        RenderFrame();

        System.Drawing.Rectangle screen = Win32.ScreenRect(Handle);
        RaiseWindowEvent(EventCatalog.WindowDpiChanged, new JsonObject
        {
            ["scale"] = dpi / 96.0,
            ["rect"] = new JsonArray(screen.Left, screen.Top, screen.Width, screen.Height),
        });

        m.Result = IntPtr.Zero;
    }
}
