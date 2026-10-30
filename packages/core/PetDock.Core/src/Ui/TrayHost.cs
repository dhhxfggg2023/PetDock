// 托盘（唯一常驻入口）。菜单只留「主控面板 → 每只宠物（移动/暂停/移除）→ 退出」。
//
// 桌上有几只就列几条；每条只动它自己那只。右键宠物弹出的菜单走 ShowMenuAt，
// 宠物窗是 NOACTIVATE 分层窗，菜单拿不到激活，所以由 _menuWatch 负责点空白处关闭。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using PetDock.Core.Compositor;

namespace PetDock.Core.Ui;

/// <summary>托盘「宠物：<名称>」那一条：名字 + 三个命令（移动/暂停/移除，由宿主按 id 路由）。</summary>
internal sealed record PetMenuItem(
    string Title,
    UiCommand Move,
    UiCommand Pause,
    UiCommand Remove);

/// <summary>
/// 托盘宿主。持有 NotifyIcon 与菜单；所有动作经回调交给上层（本类不直接操作宠物）。
/// </summary>
internal sealed partial class TrayHost : IDisposable
{
    /// <summary>
    /// 固定项的稳定 id（自检按 id 断言，不按显示文字——文字会跟着语言变）。
    /// 宠物项不在这个数组里：它们的 id 是 `pet-1`…`pet-N`，条数由桌面上的只数决定。
    /// </summary>
    internal static readonly string[] ItemIds =
    {
        "panel",
        "exit",
    };

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _menuWatch;
    private bool _menuArmed;

    /// <summary>当前弹着的那份菜单（托盘那份或某一只那份）。「点空白处关闭」只认它。</summary>
    private ContextMenuStrip? _openMenu;

    /// <summary>固定项的两个动作：右键菜单里要复用（不重造一份，见 ShowPetMenuAt）。</summary>
    private readonly UiCommand _openPanel;
    private readonly UiCommand _exit;
    private readonly ToolStripMenuItem _panelItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly List<ToolStripMenuItem> _petItems = new();
    private IReadOnlyList<PetMenuItem> _pets = Array.Empty<PetMenuItem>();
    private bool _registered;
    private bool _disposed;

    /// <summary>
    /// 每条宠物子菜单的三个子项**按显示顺序**对应的文案键。键是契约（判据断言它），
    /// 文案在 `assets/i18n`（RFC-42）。顺序：移动 → 暂停 → 移除。
    /// </summary>
    internal static readonly string[] PetChildKeys =
    {
        "ui.tray.pet.move",
        "ui.tray.pet.pause",
        "ui.tray.pet.remove",
    };

    public TrayHost(
        IReadOnlyList<PetMenuItem> pets,
        UiCommand openPanel,
        UiCommand exit)
    {
        _openPanel = openPanel ?? throw new ArgumentNullException(nameof(openPanel));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _menu = new ContextMenuStrip();

        // 宠物窗是 NOACTIVATE 分层窗，菜单拿不到激活，点空白处不会自动收起来。
        // 菜单开着时用 50ms 的 UI 计时器看鼠标：按下且在菜单外就自己关掉。
        _menuWatch = new System.Windows.Forms.Timer { Interval = 50 };
        _menuWatch.Tick += (_, _) => CloseMenuIfClickedOutside();
        _menu.Closed += (_, _) => _menuWatch.Stop();

        _panelItem = new ToolStripMenuItem(I18n.Get("ui.tray.panel"), null, (_, _) => _openPanel())
        {
            Name = "panel",
        };

        _exitItem = new ToolStripMenuItem(I18n.Get("ui.tray.exit"), null, (_, _) => _exit())
        {
            Name = "exit",
        };

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _panelItem,
            new ToolStripSeparator(),
            _exitItem,
        });

        SetPets(pets);
        Design.ThemedMenuRenderer.Apply(_menu);

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = I18n.Get("ui.tray.title.running", pets.Count),
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => openPanel();
    }

    /// <summary>
    /// 把图标真正注册进外壳。**首次滴答才调用**（RFC-14 §2.2 的同一条理由）：
    /// 构造期没有消息泵，而托盘通知 API 的注册要等本进程回一次消息才算成功——
    /// 装载一忙（内容插件在装、帧泵在跑），这个握手就超时，图标静默地不进托盘，
    /// 用户看到的是「托盘里根本没有东西」（2026-10-26 实测：构造期注册时外壳无图标，
    /// 同样的字节在别处延迟注册就正常）。返回是否**在这次**调用里改变了注册状态。
    /// </summary>
    public void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        _icon.Visible = true;

        // 托盘通知 API 的失败是静默的（NotifyIcon 既不抛也不报），这一行是唯一的现场记录：
        // 图标句柄为 0 或文本为空，外壳都会拒绝这一条，而 visible=True 只是宿主侧的意愿。
        KernelLog.Write(
            $"tray-icon icon={(_icon.Icon?.Handle ?? IntPtr.Zero).ToInt64()} " +
            $"visible={_icon.Visible} text-chars={(_icon.Text ?? string.Empty).Length}");
    }

    /// <summary>
    /// 重建宠物那几条子菜单（导入追加、移除之后调用）。插在「主控面板」与分隔线之间，
    /// 顺序：主控面板 → 每宠物一条 → 退出。
    /// </summary>
    public void SetPets(IReadOnlyList<PetMenuItem> pets)
    {
        _pets = new List<PetMenuItem>(pets);
        foreach (ToolStripMenuItem old in _petItems)
        {
            _menu.Items.Remove(old);
            old.Dispose();
        }

        _petItems.Clear();

        int insertAt = 1; // 「主控面板」之后
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
                    _ => pet.Remove,
                };
                item.DropDownItems.Add(new ToolStripMenuItem(I18n.Get(PetChildKeys[child]), null, (_, _) => command()));
            }
            _menu.Items.Insert(insertAt + index, item);
            _petItems.Add(item);
        }

        _icon?.SetTextSafe(I18n.Get("ui.tray.title.running", pets.Count));
    }

    /// <summary>
    /// 语言切换后按当前文案表重建所有文字（固定项 + 每宠物一条 + 托盘提示）。
    /// 菜单对象不重建：右键宠物与托盘图标共用这一份，换语言只该换字，不该换入口。
    /// </summary>
    public void ReloadLanguage()
    {
        _panelItem.Text = I18n.Get("ui.tray.panel");
        _exitItem.Text = I18n.Get("ui.tray.exit");
        SetPets(_pets);
    }

    /// <summary>固定菜单项当前的显示文字（判据断言换语言后它真的变了）。</summary>
    public string FixedItemText(string id) => id switch
    {
        "panel" => _panelItem.Text ?? string.Empty,
        "exit" => _exitItem.Text ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>第 <paramref name="index"/> 条宠物子菜单当前显示的名称（判据用）。</summary>
    public string PetItemTitle(int index) =>
        (index >= 0 && index < _petItems.Count ? _petItems[index].Text : string.Empty) ?? string.Empty;

    /// <summary>
    /// 在屏幕坐标处弹出同一份菜单（2026-10-26 用户要求「右键宠物弹菜单」）。
    /// 复用托盘那一份而不是另建一套：菜单是「用户的一次点击 → 内核的一次动作」的映射，
    /// 两套菜单迟早会漂移成两套语义。宠物窗的右键与托盘图标因此是同一个入口。
    /// 只在 UI 线程上调用（ContextMenuStrip.Show 要求）：生产路径是 WndProc/托盘回调，天然是 UI 线程。
    /// </summary>
    public void ShowMenuAt(Point screen)
    {
        if (_disposed)
        {
            return;
        }

        _openMenu = _menu;
        _menu.Show(screen);
        _menuArmed = false;
        _menuWatch.Start();
    }

    /// <summary>菜单外按下鼠标就关闭菜单（托盘图标那条由系统菜单自己处理）。</summary>
    private void CloseMenuIfClickedOutside()
    {
        // 「当前弹着的那份」可能是托盘那份，也可能是某一只那份（2026-10-30）：
        // 宠物窗拿不到激活，两份菜单都得靠这个计时器收，只认 _menu 会让右键菜单点外面不关。
        ContextMenuStrip? open = _openMenu;
        if (open is null || !open.Visible)
        {
            _menuWatch.Stop();
            return;
        }

        if (!_menuArmed)
        {
            // 打开菜单的那次右键还没松开；等它松开之后再开始看「点外面」。
            if (Control.MouseButtons == MouseButtons.None)
            {
                _menuArmed = true;
            }

            return;
        }

        if (Control.MouseButtons == MouseButtons.None)
        {
            return;
        }

        if (!open.Bounds.Contains(Cursor.Position))
        {
            open.Close();
            _menuWatch.Stop();
        }
    }

    /// <summary>第 <paramref name="index"/> 条宠物子菜单的三个子项文案（自检用；顺序即显示顺序）。</summary>
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

    /// <summary>托盘提示文字随宠物数变化（原规格 18 §1）。</summary>
    public void SetPetCount(int count) => _icon.Text = I18n.Get("ui.tray.title.running", count);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menuWatch.Dispose();
        _menu.Dispose();

        // 临时弹着的那份（右键某一只）也要收：它的 Closed 处理器里 Dispose，
        // 但托盘被 Dispose 时它可能还开着（进程正在退出的路径）。
        if (_openMenu is { } open && !ReferenceEquals(open, _menu))
        {
            open.Dispose();
        }

        _openMenu = null;
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
