// 托盘的**判据面**（2026-10-31 从 TrayHost.cs 拆出：D-03c 单文件 400 行上限）。
//
// 这里全是「读一个读数」「点一下某一项」这类给 --uitest 用的入口，没有生产逻辑。
// 拆出去的理由与 TrayHost.PetMenu.cs 一样：菜单本体已经够长，把判据面混在里头，
// 读托盘行为的人会以为这些公开方法是给生产用的。
//
// 一条纪律：**按 id 断言，不按显示文字**。文字跟着语言走，id 是契约（RFC-42 §3.4 第 2 条）。
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace PetDock.Core.Ui;

internal sealed partial class TrayHost
{
    /// <summary>固定菜单项当前的显示文字（判据断言换语言后它真的变了）。</summary>
    public string FixedItemText(string id) => id switch
    {
        "panel" => _panelItem.Text ?? string.Empty,
        "restart" => _restartItem.Text ?? string.Empty,
        "exit" => _exitItem.Text ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>点一下某个固定项（自检用：走真实 Click 链，验的是「这条命令指向谁」）。</summary>
    public bool ClickFixedItem(string id)
    {
        ToolStripMenuItem? item = id switch
        {
            "panel" => _panelItem,
            "restart" => _restartItem,
            "exit" => _exitItem,
            _ => null,
        };

        if (item is null || !item.Available)
        {
            return false;
        }

        item.PerformClick();
        return true;
    }

    private static void Noop()
    {
    }

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

        // 已释放就别碰它：读 Visible/Bounds 会抛 ObjectDisposedException，
        // 而这里跑在计时器线程上 —— 抛出去就是一个「未处理的异常」对话框（2026-10-30 的事故）。
        if (open is null || open.IsDisposed || !open.IsHandleCreated)
        {
            _openMenu = null;
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

        try
        {
            if (!open.Bounds.Contains(Cursor.Position))
            {
                open.Close();
                _menuWatch.Stop();
            }
        }
        catch (ObjectDisposedException)
        {
            // 菜单在这一瞬间被关掉了：什么都不用做（它已经不在了）。
            _openMenu = null;
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
}
