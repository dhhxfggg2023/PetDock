// 列表空白区的绘制（ThemedList 的另一半，拆分只为单文件行数上限 D-03c）。
//
// 为什么需要它：系统把 OwnerDraw 列表的空白区按列画成一竖一竖的分隔线（2026-10-28 截图里
// 宠物页、性能页的空表都出现了等距竖线，读起来像「表格没画完」）。做法是两段：
//   ① WM_ERASEBKGND 自己擦整块客户区（跳过系统的主题擦除）；
//   ② WM_PAINT 之后把「表头以下、最后一行以上之外」的剩余区域再铺一遍底色。
// 表头高度必须问那个独立子窗口要（LVM_GETHEADER + GetWindowRect），按字号估会盖掉表头。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

internal sealed partial class ThemedList
{
    private const int WmEraseBackground = 0x0014;
    private const int WmPaint = 0x000F;

    /// <summary>先让基类照常画，再把最后一行以下的整块铺成底色。</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmEraseBackground)
        {
            using Graphics graphics = Graphics.FromHwnd(Handle);
            using var brush = new SolidBrush(Theme.Paint(Theme.Bg));
            graphics.FillRectangle(brush, ClientRectangle);
            m.Result = (IntPtr)1;
            return;
        }

        base.WndProc(ref m);

        if (m.Msg == WmPaint && IsHandleCreated)
        {
            ClearBelowLastRow();
        }
    }

    /// <summary>最后一行以下的区域（含空表时的整个内容区）铺成底色。</summary>
    private void ClearBelowLastRow()
    {
        int top = HeaderHeight();
        if (Items.Count > 0)
        {
            top = Math.Max(top, Items[Items.Count - 1].Bounds.Bottom);
        }

        int height = ClientSize.Height - top;
        if (height <= 0)
        {
            return;
        }

        using Graphics graphics = Graphics.FromHwnd(Handle);
        using var brush = new SolidBrush(Theme.Paint(Theme.Bg));
        graphics.FillRectangle(brush, new Rectangle(0, top, ClientSize.Width, height));
    }

    /// <summary>表头高度（设备像素）：表头是独立的子窗口，必须问它要真实高度，不能按字号估。</summary>
    private int HeaderHeight()
    {
        if (!IsHandleCreated)
        {
            return 0;
        }

        IntPtr header = SendMessage(Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
        if (header == IntPtr.Zero || !GetWindowRect(header, out NativeRect headerRect))
        {
            return 0;
        }

        return GetWindowRect(Handle, out NativeRect listRect) ? Math.Max(0, headerRect.Bottom - listRect.Top) : 0;
    }

    private const int LvmGetHeader = 0x101F;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}