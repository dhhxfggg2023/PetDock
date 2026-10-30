// 右键**某一只**宠物时那份菜单（2026-10-30 从 TrayHost.cs 拆出：D-03c 单文件 400 行上限）。
//
// 为什么是独立的一份菜单，而不是复用托盘那份：托盘那份是「桌上所有宠物各一条子菜单 + 全局退出」，
// 用户在某一隻身上按右键，要的是「这一只的移动/暂停/移除」。菜单项的归属必须由**被点的那只**决定，
// 而不是让用户先进子菜单再自己找一遍刚才点的是谁。
// 文案键与托盘共用同一批（ui.tray.pet.*），所以两份菜单不会漂移成两套语义。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui;

/// <summary>
/// 在**某一只**宠物身上按右键时那份菜单的内容。
/// 与 PetMenuItem 的区别：那份是托盘里的一行（三件事，谁点谁的那一行），
/// 这份是「针对这一只」的完整菜单 —— 多出了面板入口与全局退出，
/// 后两者必须与「只动这一只」的三件事**视觉上分开**（中间一条分隔线），
/// 否则用户会把「退出」读成「关掉这一只」。
/// </summary>
internal sealed record PetScope(
    string Title,
    bool Paused,
    UiCommand Move,
    UiCommand Pause,
    UiCommand Remove,
    UiCommand Panel,
    UiCommand Exit);

internal sealed partial class TrayHost
{
    /// <summary>
    /// 在屏幕坐标处弹出**只针对这一只**的菜单（2026-10-30）。
    /// 为什么另建一份而不是复用托盘那份：托盘那份是「桌上所有宠物各一条子菜单 + 全局退出」，
    /// 用户在某一只身上按右键，想要的是「这一只的移动/暂停/移除」——
    /// 菜单项的归属必须由**被点的那只**决定，而不是让用户在两秒钟后在子菜单里再找一遍自己点的是谁。
    /// 文案键与托盘共用同一批（ui.tray.pet.*），所以两份菜单不会漂移成两套语义；
    /// 「只动这一只」的三件事与「面板/退出」之间有一条分隔线，退出永远是全局的那一个。
    /// 只在 UI 线程上调用（ContextMenuStrip.Show 要求）。菜单是临时的：关掉就释放。
    /// </summary>
    public void ShowPetMenuAt(Point screen, PetScope scope)
    {
        if (_disposed)
        {
            return;
        }

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem(I18n.Get("ui.tray.pet.caption", scope.Title)) { Enabled = false },
            new ToolStripMenuItem(I18n.Get("ui.tray.pet.move"), null, (_, _) => scope.Move()),
            new ToolStripMenuItem(I18n.Get(scope.Paused ? "ui.tray.pet.resume" : "ui.tray.pet.pause"), null,
                (_, _) => scope.Pause()),
            new ToolStripMenuItem(I18n.Get("ui.tray.pet.remove"), null, (_, _) => scope.Remove()),
            new ToolStripSeparator(),
            new ToolStripMenuItem(I18n.Get("ui.tray.panel"), null, (_, _) => scope.Panel()),
            new ToolStripMenuItem(I18n.Get("ui.tray.exit"), null, (_, _) => scope.Exit()),
        });
        Design.ThemedMenuRenderer.Apply(menu);

        _openMenu = menu;
        menu.Closed += (_, _) =>
        {
            _menuWatch.Stop();
            if (ReferenceEquals(_openMenu, menu))
            {
                _openMenu = null;
            }

            menu.Dispose();
        };
        menu.Show(screen);
        _menuArmed = false;
        _menuWatch.Start();
    }

}
