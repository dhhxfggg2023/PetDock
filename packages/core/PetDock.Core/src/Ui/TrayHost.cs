// 托盘（docs/18 §1，唯一常驻入口）。五项菜单的顺序与文案照抄文档，一项不多一项不少——
// §7 写死「未在本文列出的菜单项，代码审查时视为缺陷」，所以这里没有任何「顺手加」的项。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PetDock.Core.Ui;

/// <summary>
/// 托盘宿主。持有 NotifyIcon 与菜单；所有动作经回调交给上层（本类不直接操作宠物）。
/// </summary>
internal sealed class TrayHost : IDisposable
{
    /// <summary>菜单项的稳定 id（自检按 id 断言，不按显示文字——文字会跟着语言变）。</summary>
    internal static readonly string[] ItemIds =
    {
        "toggle-all",
        "panel",
        "pet",
        "pause-hooks",
        "exit",
    };

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _pauseHooks;
    private bool _disposed;

    public TrayHost(
        string petName,
        UiCommand toggleAll,
        UiCommand openPanel,
        UiCommand movePet,
        UiCommand pausePet,
        UiCommand hidePet,
        UiCommand removePet,
        UiCommand pauseHooks,
        UiCommand exit)
    {
        _menu = new ContextMenuStrip();

        var toggle = new ToolStripMenuItem("显示/隐藏全部宠物", null, (_, _) => toggleAll())
        {
            Name = "toggle-all",
        };
        var panel = new ToolStripMenuItem("主控面板", null, (_, _) => openPanel())
        {
            Name = "panel",
        };

        var petItem = new ToolStripMenuItem("宠物：" + petName)
        {
            Name = "pet",
        };
        petItem.DropDownItems.Add(new ToolStripMenuItem("移动", null, (_, _) => movePet()));
        petItem.DropDownItems.Add(new ToolStripMenuItem("暂停", null, (_, _) => pausePet()));
        petItem.DropDownItems.Add(new ToolStripMenuItem("隐藏", null, (_, _) => hidePet()));
        petItem.DropDownItems.Add(new ToolStripMenuItem("移除", null, (_, _) => removePet()));

        // 强制项：docs/18 §1「必须存在且不可隐藏」。Enabled 由宿主状态决定，
        // 但 Visible 恒为 true——任何一条代码路径都不许把它藏起来。
        _pauseHooks = new ToolStripMenuItem("暂停全局输入钩子", null, (_, _) => pauseHooks())
        {
            Name = "pause-hooks",
        };

        var exitItem = new ToolStripMenuItem("退出", null, (_, _) => exit())
        {
            Name = "exit",
        };

        _menu.Items.AddRange(new ToolStripItem[]
        {
            toggle,
            panel,
            petItem,
            new ToolStripSeparator(),
            _pauseHooks,
            new ToolStripSeparator(),
            exitItem,
        });

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = $"PetDock — 1 只宠物运行中",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => openPanel();
    }

    /// <summary>菜单项 id 的实际顺序（自检用）。</summary>
    public IReadOnlyList<string> ItemOrder
    {
        get
        {
            var ids = new List<string>();
            foreach (ToolStripItem item in _menu.Items)
            {
                if (!string.IsNullOrEmpty(item.Name))
                {
                    ids.Add(item.Name);
                }
            }

            return ids;
        }
    }

    /// <summary>
    /// 强制项是否处于「可显示」状态——自检断言它永远为 true。
    /// 用 Available 而不是 Visible：Visible 是运行时状态，宿主菜单从未 Show 过时恒为 false，
    /// 断言它等于断言「菜单打开过」；Available 才是控制该条目能否出现的开关
    /// （docs/18 §1：暂停全局输入钩子是强制项，不得随宠物数或状态消失）。
    /// </summary>
    public bool HookItemVisible => _pauseHooks.Available;

    /// <summary>托盘提示文字随宠物数变化（docs/18 §1）。</summary>
    public void SetPetCount(int count) => _icon.Text = $"PetDock — {count} 只宠物运行中";

    /// <summary>
    /// 托盘图标：docs/18 §1 指定 assets/icons/icon-32.png。找不到就用系统默认图标——
    /// 缺图标不该阻止程序启动，但也不能静默换成自造图标（那会让「发行前替换占位图」这条消失）。
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
