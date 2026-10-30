// 托盘图标的加载（2026-10-30 从 TrayHost.cs 拆出：菜单那半边加上「某一只的菜单」之后
// 已经贴着 D-03c 的 400 行上限）。本片只做一件事：从仓库里找 icon-32.png，找不到就用系统默认图标。
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PetDock.Core.Ui;

internal sealed partial class TrayHost
{
    /// <summary>
    /// 托盘图标：原规格 18 §1 指定 assets/icons/icon-32.png。找不到就用系统默认图标——
    /// 缺图标不该阻止程序启动，但也不能静默换成自造图标（那会让「发行前替换占位图」这条消失）。
    /// </summary>
    internal static Icon LoadIcon()
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
        catch (Exception ex)
        {
            Compositor.KernelLog.Write($"tray-icon-load-failed {ex.GetType().Name}: {ex.Message}");
        }

        return SystemIcons.Application;
    }

    /// <summary>从可执行目录向上找仓库根下的相对路径（bin 树深 5~6 层）。</summary>
    internal static string? FindRepoFile(string relative)
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
                // 仓库根已到但文件不存在：不再往上，避免命中仓库外的同名文件。
                return File.Exists(candidate) ? candidate : null;
            }

            dir = dir.Parent;
        }

        return null;
    }

}
