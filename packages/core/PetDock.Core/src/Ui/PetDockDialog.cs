// PetDock 自有模态窗的基类：与宠物窗同档（置顶）。
//
// 账（2026-10-26 真机取证，pid 6444）：宠物窗是 WS_EX_TOPMOST 的 —— 桌宠必须浮在所有窗口
// 上面，否则被任何最大化的前台应用盖住就等于没运行（原规格 12 的置顶要求）。而本产品自己的
// 模态窗默认落在普通档，于是用户前台有最大化应用时，实测 z-order 是
//     宠物窗(#3) > 前台应用(#4) > 授权窗(#5)
// 宠物浮在前台应用上面可见，弹窗却被压在它后面：用户一个弹窗都看不到。
// 再叠加 WinForms 的 ShowDialog：它把同线程其他顶层窗置 WS_DISABLED（宠物窗 style 实测
// 0x1E010000），宠物于是「可见但点不动」；而唯一的退出入口在托盘里、退出确认框同样被压住
// ⇒ 用户眼里的「点不了，也关不掉」。
//
// 规则：本产品每一个模态窗都进置顶档（与宠物同档，且因后开而压在上面）。
// 为什么不靠「弹窗抢焦点」：前台锁让后台进程的 SetForegroundWindow 失败，抢焦点不可靠；
// 置顶可靠，且不改变用户前台应用本身。
//
// 为什么不能只写 `TopMost = true` 就完事：实测（--uitest 的 ui-modal-topmost-*，2026-10-26）
// 只设属性时窗口显示后的原生 exstyle 是 0x10101 —— TOPMOST 位不在。宠物窗那边早有同一条账
// （LayeredFrameWindow.BringToFrontOfTopmost：「一次实测里 exstyle 里的 TOPMOST 位是会被系统
// 抹掉的，所以不能假设它一直在」）。所以这里在句柄建好与显示两个时点都主动把位设回去，
// 判据读的是位本身，不是属性。
using System;
using System.Windows.Forms;
using PetDock.Core.Compositor;

namespace PetDock.Core.Ui;

/// <summary>PetDock 自有模态窗的基类。新增的模态窗都继承它，不要直接继承 <see cref="Form"/>。</summary>
internal class PetDockDialog : Form
{
    protected PetDockDialog()
    {
        TopMost = true;
        // 模态窗不进任务栏：ShowInTaskbar 为真的窗在显示时会被 WinForms 用 HWND_TOP 提到普通档
        // 最前，那一步把 TOPMOST 位抹掉。实测（2026-10-26，--uitest）：授权弹窗 exstyle
        // 0x50101（带 APPWINDOW、无 TOPMOST），引导窗与退出确认 0x10109（带 TOPMOST）。
        ShowInTaskbar = false;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnterTopmostBand();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        EnterTopmostBand();
    }

    /// <summary>
    /// 把「在置顶档」这件事做到可以读回来：位设上，再用 HWND_TOPMOST 把 Z 序提到这一档最前
    /// （TOPMOST 是一整档，档内先后同样决定用户看到谁）。
    /// </summary>
    private void EnterTopmostBand()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        long exStyle = Win32.GetWindowLongPtr(Handle, Win32.GWL_EXSTYLE).ToInt64();
        if ((exStyle & Win32.WS_EX_TOPMOST) == 0)
        {
            Win32.SetWindowLongPtr(Handle, Win32.GWL_EXSTYLE, new IntPtr(exStyle | Win32.WS_EX_TOPMOST));
        }

        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }
}
