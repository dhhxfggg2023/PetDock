// 新层的托盘适配器（R1e）。**本工程里唯一允许出现 System.Windows.Forms 的文件。**
//
// 为什么还是 WinForms：R0 spike 实测托盘通知的那条 Win32 调用在本机返回 FALSE 且
// GetLastError=0（cbSize=976，hwnd/icon 都有效，explorer 在 session 1）—— 继续钻这个洞不划算。
// 于是「产品零 WinForms」这个承诺作废，换成一个可检验的边界：WinForms 只许出现在这个文件里。
// 判据：test/SelfCheck/NewUiChecks.cs 的 ui-next-winforms-isolated 扫新层全部源码。
//
// 托盘只在两件事上说话：打开壳窗口、退出进程。它不碰窗口状态、不认识页面，
// 也不做「关掉以后进程还在」的判断 —— 那是壳的事（R3 重新定义常驻语义）。
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PetDock.Ui;

/// <summary>托盘：一个图标 + 两项菜单。</summary>
internal sealed class NotifyIconTray : IDisposable
{
    /// <summary>NotifyIcon.Text 的上限是 63 个字符，超了会抛。</summary>
    private const int MaxTipLength = 63;

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private bool _disposed;

    public NotifyIconTray(string tip, string openText, string exitText)
    {
        _menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem(openText);
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(open);

        var exit = new ToolStripMenuItem(exitText);
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = tip.Length > MaxTipLength ? tip[..MaxTipLength] : tip,
            ContextMenuStrip = _menu,
            Visible = true,
        };

        // 双击图标 = 打开（与旧层一致：用户对托盘的默认期待）。
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    /// <summary>菜单项数（判据用：两项之外多出来的项说明有人在托盘里塞功能）。</summary>
    internal int MenuItemCount => _menu.Items.Count;

    /// <summary>图标是否真的交给了系统（NotifyIcon 不检查返回值，这里读它自己记的那一位）。</summary>
    internal bool IsVisible => _icon.Visible;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 顺序要紧：先撤图标再 Dispose，否则托盘里会留一个「幽灵图标」，要等用户划过它才消失。
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    /// <summary>
    /// 图标：assets/icons/icon-32.png（原规格 18 §1），找不到就退回系统默认图标。
    /// 找不到不该阻止启动，但也不能静默换成自造图 —— 那会让「发行前替换占位图」这条消失。
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            string? png = FindRepoFile(Path.Combine("assets", "icons", "icon-32.png"));
            if (png is not null)
            {
                using Bitmap bitmap = new(png);
                return Icon.FromHandle(bitmap.GetHicon());
            }
        }
        catch (Exception)
        {
            // 图标读不出来不改变结论（退回系统图标）；真正的错误由旧层那条日志记录。
        }

        return SystemIcons.Application;
    }

    /// <summary>从可执行目录向上找仓库根下的相对路径（bin 树深 5~6 层）。</summary>
    private static string? FindRepoFile(string relative)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (File.Exists(Path.Combine(dir.FullName, "PetDock.sln")))
            {
                return null;
            }

            dir = dir.Parent;
        }

        return null;
    }
}