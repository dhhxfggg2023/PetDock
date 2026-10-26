// RFC-37 §3.2 片 5（2026-10-26 用户要求「右键宠物可以弹菜单」）：右键菜单的接线半边。
// 与「拖拽落地」同一条纪律 —— 在**唯一**的登记点接一次，装配期建出来的和运行期追加进来的
// 都走这里，两个入口各接一次就会漏掉一边。主窗那只不在本集合里，它的接线归 UiHost。
// 本文件是 docs/40 D-03c 的拆分半边。
using PetDock.Core.Compositor;

namespace PetDock.Core.Ui;

internal sealed partial class PetWindowSet
{
    /// <summary>
    /// 右键菜单的接线（2026-10-26）：与「拖拽落地」同一条纪律 —— 在**唯一**的登记点接一次，
    /// 装配期建出来的和运行期追加进来的都走这里，两个入口各接一次就会漏掉一边。
    /// 主窗那只不在本集合里，它的接线归 UiHost（构造期只有主窗）。
    /// </summary>
    internal LayeredFrameWindow.ContextRequestDelegate? ContextMenuHandler { get; set; }

    private void WireContextMenu(LayeredFrameWindow window)
    {
        if (ContextMenuHandler is { } handler)
        {
            window.ContextRequested += handler;
        }
    }

    /// <summary>判据用：桌上有几只真的接上了右键（0 = 菜单是个死键）。</summary>
    internal int ContextMenuWiredCount
    {
        get
        {
            int wired = 0;
            foreach (Slot slot in _slots)
            {
                if (slot.Window.ContextMenuWired)
                {
                    wired++;
                }
            }

            return wired;
        }
    }
}
