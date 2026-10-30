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
    /// 这份菜单**只建一次**，之后每次右键只换里面的条目（2026-10-30 事故后改）。
    ///
    /// 旧写法是每次右键 new 一个、在 Closed 里 Dispose：用户在菜单项上点一下，
    /// WinForms 先关菜单（Closed 触发 → 我们把菜单释放掉），再走完这次点击，
    /// 而 50ms 的「点空白处就关」计时器也可能已经排队了 —— 于是任何一次点击都可能
    /// 撞在已释放的 ContextMenuStrip 上，弹「Cannot access a disposed object」。
    /// 现在：菜单活到托盘释放为止，条目每次重建（重建时释放上一批条目，那时已经隔了一整次交互，安全）。
    /// </summary>
    public void ShowPetMenuAt(Point screen, PetScope scope)
    {
        if (_disposed)
        {
            return;
        }

        ContextMenuStrip menu = _scopeMenu ??= new ContextMenuStrip();

        // 上一批条目在这里释放：此刻距离「上一次点击」已经隔了完整的打开→点击→关闭，
        // 没有任何调用栈还引用它们（与旧写法在 Closed 里释放菜单的区别就在这里）。
        foreach (ToolStripItem old in menu.Items)
        {
            old.Dispose();
        }

        menu.Items.Clear();
        menu.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem(I18n.Get("ui.tray.pet.caption", scope.Title)) { Enabled = false },
            PetCommand(I18n.Get("ui.tray.pet.move"), menu, scope.Move),
            PetCommand(I18n.Get(scope.Paused ? "ui.tray.pet.resume" : "ui.tray.pet.pause"), menu, scope.Pause),
            PetCommand(I18n.Get("ui.tray.pet.remove"), menu, scope.Remove),
            new ToolStripSeparator(),
            PetCommand(I18n.Get("ui.tray.panel"), menu, scope.Panel),
            PetCommand(I18n.Get("ui.tray.exit"), menu, scope.Exit),
        });
        Design.ThemedMenuRenderer.Apply(menu);

        _openMenu = menu;
        menu.Show(screen);
        _menuArmed = false;
        _menuWatch.Start();
    }

    /// <summary>
    /// 一条「点了以后做某件事」的菜单项。动作**投递回消息循环**再执行（2026-10-30）。
    /// 为什么不在 Click 里直接跑：菜单项的动作里可能有模态确认框（「从桌面移除」就会问一句），
    /// 在菜单自己的点击链里开模态窗，等于让菜单的关闭流程与新窗的消息泵互相嵌套 ——
    /// 用户看到的是「点了没反应」或者异常框。投递之后点击链先走完，再执行动作。
    /// </summary>
    private static ToolStripMenuItem PetCommand(string text, ContextMenuStrip owner, UiCommand command) =>
        new(text, null, (_, _) => PostCommand(owner, command));

    private static void PostCommand(ContextMenuStrip owner, UiCommand command)
    {
        try
        {
            if (owner.IsHandleCreated && !owner.IsDisposed)
            {
                owner.BeginInvoke((MethodInvoker)(() => command()));
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            // 菜单正好没了：退到直接执行（动作本身仍然要做）。
        }
        catch (InvalidOperationException)
        {
            // 句柄还没建出来 / 已在销毁：同上。
        }

        command();
    }

}
