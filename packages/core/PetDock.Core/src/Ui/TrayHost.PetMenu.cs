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
    UiCommand Restart,
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

        ContextMenuStrip menu = RebuildPetMenu(scope);
        _openMenu = menu;
        menu.Show(screen);
        _menuArmed = false;
        _menuWatch.Start();
    }

    /// <summary>
    /// 判据入口（2026-10-31）：只走到「重建条目」为止，不 Show。
    /// 无人值守的自检桌面上弹一个真菜单没有意义（而且菜单会挡住后续判据），
    /// 而缺陷恰恰在重建那一半 —— 因此把这一半单独开放，判据连调两次就能钉住它。
    /// </summary>
    internal void RebuildPetMenuForCheck(PetScope scope) => RebuildPetMenu(scope);

    /// <summary>把这一只的菜单条目重建成 <paramref name="scope"/> 描述的那一份。</summary>
    private ContextMenuStrip RebuildPetMenu(PetScope scope)
    {
        ContextMenuStrip menu = _scopeMenu ??= new ContextMenuStrip();

        // 上一批条目在这里释放（2026-10-31 修）：`foreach` 直接遍历 `menu.Items`，
        // 而 `old.Dispose()` 会把它自己从那个集合里摘掉 —— 边枚举边改集合，
        // ToolStripItemCollection 的枚举器立刻抛
        // 「InvalidOperationException: Collection was modified; enumeration operation may not execute」。
        // 后果不是「菜单画得难看」：异常从 ShowPetMenu 抛回 WndProc 的 try/catch，
        // 记一行 context-menu-failed 就结束 —— 用户右键宠物**一次菜单都看不到**。
        // 用户的真机日志（2026-10-31 00:35）里这行出现了 6 次，正是他报的「右键宠物的移动·暂停·移除没用」。
        // 所以先复制成数组再释放：枚举的是快照，改的是原集合。
        ToolStripItem[] stale = new ToolStripItem[menu.Items.Count];
        menu.Items.CopyTo(stale, 0);
        foreach (ToolStripItem old in stale)
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
            PetCommand(I18n.Get("ui.tray.restart"), menu, scope.Restart),
            PetCommand(I18n.Get("ui.tray.exit"), menu, scope.Exit),
        });
        Design.ThemedMenuRenderer.Apply(menu);
        return menu;
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
