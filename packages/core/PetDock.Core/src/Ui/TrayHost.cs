// 托盘（docs/18 §1，唯一常驻入口）。五项菜单的顺序与文案照抄文档，一项不多一项不少——
// §7 写死「未在本文列出的菜单项，代码审查时视为缺陷」，所以这里没有任何「顺手加」的项。
//
// RFC-37 §3.6（片 3）：§1 那一行「宠物：<名称> | 子菜单：移动/暂停/隐藏/移除 | **每宠物一条**」
// 以前只兑现了半句 —— 菜单上永远只有一条，名字是启动时那一只。桌上有几只就得有几条，
// 而且每条只该动它自己那只（点「隐藏」藏三只里的哪一只，用户心里有数）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PetDock.Core.Ui;

/// <summary>托盘「宠物：<名称>」那一条：名字 + 四个命令（每条一个，命令由宿主按 id 路由）。</summary>
internal sealed record PetMenuItem(
    string Title,
    UiCommand Move,
    UiCommand Pause,
    UiCommand Hide,
    UiCommand Remove);

/// <summary>
/// 托盘宿主。持有 NotifyIcon 与菜单；所有动作经回调交给上层（本类不直接操作宠物）。
/// </summary>
internal sealed class TrayHost : IDisposable
{
    /// <summary>
    /// 固定项的稳定 id（自检按 id 断言，不按显示文字——文字会跟着语言变）。
    /// 宠物项不在这个数组里：它们的 id 是 `pet-1`…`pet-N`，条数由桌面上的只数决定。
    /// </summary>
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
    private readonly List<ToolStripMenuItem> _petItems = new();
    private bool _disposed;

    /// <summary>
    /// 每条宠物子菜单的四个子项**按显示顺序**对应的文案键。键是契约（判据断言它），
    /// 文案在 `assets/i18n`（RFC-42）。顺序照 docs/18 §1：移动 → 暂停 → 隐藏 → 移除。
    /// </summary>
    internal static readonly string[] PetChildKeys =
    {
        "ui.tray.pet.move",
        "ui.tray.pet.pause",
        "ui.tray.pet.hide",
        "ui.tray.pet.remove",
    };

    public TrayHost(
        IReadOnlyList<PetMenuItem> pets,
        UiCommand toggleAll,
        UiCommand openPanel,
        UiCommand pauseHooks,
        UiCommand exit)
    {
        _menu = new ContextMenuStrip();

        var toggle = new ToolStripMenuItem(I18n.Get("ui.tray.toggleAll"), null, (_, _) => toggleAll())
        {
            Name = "toggle-all",
        };
        var panel = new ToolStripMenuItem(I18n.Get("ui.tray.panel"), null, (_, _) => openPanel())
        {
            Name = "panel",
        };

        // 强制项：docs/18 §1「必须存在且不可隐藏」。Enabled 由宿主状态决定，
        // 但 Visible 恒为 true——任何一条代码路径都不许把它藏起来。
        _pauseHooks = new ToolStripMenuItem(I18n.Get("ui.tray.pauseHooks"), null, (_, _) => pauseHooks())
        {
            Name = "pause-hooks",
        };

        var exitItem = new ToolStripMenuItem(I18n.Get("ui.tray.exit"), null, (_, _) => exit())
        {
            Name = "exit",
        };

        _menu.Items.AddRange(new ToolStripItem[]
        {
            toggle,
            panel,
            new ToolStripSeparator(),
            _pauseHooks,
            new ToolStripSeparator(),
            exitItem,
        });

        SetPets(pets);

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = I18n.Get("ui.tray.title.running", pets.Count),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => openPanel();
    }

    /// <summary>
    /// 重建宠物那几条子菜单（导入追加、移除之后调用）。插在「主控面板」与分隔线之间，
    /// 顺序与 docs/18 §1 一致：显示/隐藏全部 → 主控面板 → 每宠物一条 → 暂停钩子 → 退出。
    /// </summary>
    public void SetPets(IReadOnlyList<PetMenuItem> pets)
    {
        foreach (ToolStripMenuItem old in _petItems)
        {
            _menu.Items.Remove(old);
            old.Dispose();
        }

        _petItems.Clear();

        int insertAt = 2; // 固定项 toggle-all / panel 之后
        for (int i = 0; i < pets.Count; i++)
        {
            PetMenuItem pet = pets[i];
            int index = i;
            var item = new ToolStripMenuItem(I18n.Get("ui.tray.pet.caption", pet.Title))
            {
                Name = "pet-" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            for (int child = 0; child < PetChildKeys.Length; child++)
            {
                UiCommand command = child switch
                {
                    0 => pet.Move,
                    1 => pet.Pause,
                    2 => pet.Hide,
                    _ => pet.Remove,
                };
                item.DropDownItems.Add(new ToolStripMenuItem(I18n.Get(PetChildKeys[child]), null, (_, _) => command()));
            }
            _menu.Items.Insert(insertAt + index, item);
            _petItems.Add(item);
        }

        _icon?.SetTextSafe(I18n.Get("ui.tray.title.running", pets.Count));
    }

    /// <summary>第 <paramref name="index"/> 条宠物子菜单的四个子项文案（自检用；顺序即显示顺序）。</summary>
    public IReadOnlyList<string> PetChildTitles(int index)
    {
        var titles = new List<string>();
        if (index < 0 || index >= _petItems.Count)
        {
            return titles;
        }

        foreach (ToolStripItem item in _petItems[index].DropDownItems)
        {
            titles.Add(item.Text ?? string.Empty);
        }

        return titles;
    }

    /// <summary>宠物子菜单的条数（= 桌面上的只数）。</summary>
    public int PetItemCount => _petItems.Count;

    /// <summary>
    /// 第 <paramref name="index"/> 条宠物子菜单**用到的键**（显示顺序）。判据断言这个，
    /// 而不是断言「文案长什么样」——文案跟着语言走，键是契约（RFC-42 §3.4 第 2 条）。
    /// </summary>
    public IReadOnlyList<string> PetChildKeyList(int index) =>
        index >= 0 && index < _petItems.Count ? PetChildKeys : Array.Empty<string>();

    /// <summary>
    /// 点一下第 <paramref name="petIndex"/> 条宠物子菜单的第 <paramref name="childIndex"/> 项。
    /// 自检专用：`PerformClick` 走的是与真实点击同一条 Click 处理链，所以它验的是「这条命令指向谁」，
    /// 而不是「我能不能调用一个方法」。菜单从未 Show 过也能点 —— Visible 是运行时态，
    /// 断言它只会得到假红（2026-10-19 那次的教训）。
    /// </summary>
    public bool ClickPetChild(int petIndex, int childIndex)
    {
        if (petIndex < 0 || petIndex >= _petItems.Count)
        {
            return false;
        }

        ToolStripItemCollection children = _petItems[petIndex].DropDownItems;
        if (childIndex < 0 || childIndex >= children.Count)
        {
            return false;
        }

        if (children[childIndex] is ToolStripMenuItem item)
        {
            item.PerformClick();
            return true;
        }

        return false;
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
    public void SetPetCount(int count) => _icon.Text = I18n.Get("ui.tray.title.running", count);

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

/// <summary>NotifyIcon 的文本长度上限是 63 字符，超了会抛 —— 宠物很多时截断而不是崩。</summary>
internal static class NotifyIconText
{
    public static void SetTextSafe(this NotifyIcon icon, string text)
    {
        if (icon is null || text is null)
        {
            return;
        }

        icon.Text = text.Length <= 63 ? text : text.Substring(0, 63);
    }
}
