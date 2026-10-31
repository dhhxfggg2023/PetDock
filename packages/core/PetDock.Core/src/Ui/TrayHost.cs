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
        "restart",
        "exit",
    };

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _menuWatch;
    private bool _menuArmed;

    /// <summary>当前弹着的那份菜单（托盘那份或某一只那份）。「点空白处关闭」只认它。</summary>
    private ContextMenuStrip? _openMenu;

    /// <summary>
    /// 右键某一只时那份菜单。**只建一次**：见 ShowPetMenuAt 的说明 ——
    /// 每次右键 new 一个、关掉就释放，会让「这一次点击」与「已释放」撞在同一次交互里。
    /// </summary>
    private ContextMenuStrip? _scopeMenu;

    /// <summary>固定项的两个动作：右键菜单里要复用（不重造一份，见 ShowPetMenuAt）。</summary>
    private readonly UiCommand _openPanel;
    private readonly UiCommand _restart;
    private readonly UiCommand _exit;
    private readonly ToolStripMenuItem _panelItem;
    private readonly ToolStripMenuItem _restartItem;
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
        UiCommand exit,
        UiCommand? restart = null)
    {
        _openPanel = openPanel ?? throw new ArgumentNullException(nameof(openPanel));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));

        // 重启是**可选**项（2026-10-31 用户要求「加上常驻的重启功能」）：判据会造不带它的托盘，
        // 而没有入口时菜单里就不该出现一条点了没反应的「重启」。
        _restart = restart ?? Noop;
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

        _restartItem = new ToolStripMenuItem(I18n.Get("ui.tray.restart"), null, (_, _) => _restart())
        {
            Name = "restart",
        };

        _exitItem = new ToolStripMenuItem(I18n.Get("ui.tray.exit"), null, (_, _) => _exit())
        {
            Name = "exit",
        };

        if (restart is not null)
        {
            _menu.Items.AddRange(new ToolStripItem[]
            {
                _panelItem,
                _restartItem,
                new ToolStripSeparator(),
                _exitItem,
            });
        }
        else
        {
            _menu.Items.AddRange(new ToolStripItem[]
            {
                _panelItem,
                new ToolStripSeparator(),
                _exitItem,
            });
        }

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
        _restartItem.Text = I18n.Get("ui.tray.restart");
        _exitItem.Text = I18n.Get("ui.tray.exit");
        SetPets(_pets);
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
        _menuWatch.Dispose();
        _menu.Dispose();

        // 右键某一只那份菜单也要收：它是长生命周期对象，不在 Closed 里释放（见 ShowPetMenuAt），
        // 所以只有这里释放它。
        _scopeMenu?.Dispose();
        _scopeMenu = null;
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
