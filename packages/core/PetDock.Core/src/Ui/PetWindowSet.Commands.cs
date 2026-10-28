// RFC-37 §3.6（片 3）：托盘要能**逐只**下命令。
//
// 片 1b 的托盘只有一条「宠物」子菜单，点下去动的永远是主窗那只 —— 桌上摆了三只之后，
// 用户点「隐藏」只藏了一只、点「移除」连语义都没有（移除的实现只是 _loop.Hide()）。
// 本文件把「第 N 只」变成一个有名字的对象：托盘的每一项、面板将来那一行，都按 id 找槽位。
//
// 主窗那只不在这里：它的生命周期归 UiHost（RFC-14 的可见性↔泵、RFC-22 的位置落盘都挂在它上面），
// 这里只回答「主窗之外的槽位」。
using System;
using System.Collections.Generic;
using System.IO;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class PetWindowSet
{
    /// <summary>一条可以下命令的槽位（托盘的每一项就是它）。</summary>
    internal sealed record Entry(string Id, string Title, Slot Slot);

    /// <summary>槽位的稳定 id：有实例就用实例 id，没有（自检形态）就用素材包目录名。</summary>
    public static string IdOf(Slot slot) =>
        slot.Instance?.Id ?? Path.GetFileName(slot.PackPath);

    /// <summary>托盘上显示的名字：清单里的显示名；旧条目没有名字时才回落目录名。不编造宠物名。</summary>
    public static string TitleOf(Slot slot) =>
        string.IsNullOrWhiteSpace(slot.Name) ? Path.GetFileName(slot.PackPath) : slot.Name;

    /// <summary>当前桌面上主窗之外的那几只，按槽位顺序。</summary>
    public IReadOnlyList<Entry> Entries()
    {
        var list = new List<Entry>(_slots.Count);
        foreach (Slot slot in _slots)
        {
            list.Add(new Entry(IdOf(slot), TitleOf(slot), slot));
        }

        return list;
    }

    /// <summary>按 id 找槽位；找不到（可能刚被移除）返回 null。</summary>
    public Slot? Find(string id)
    {
        foreach (Slot slot in _slots)
        {
            if (string.Equals(IdOf(slot), id, StringComparison.Ordinal))
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>暂停/恢复这一只（与托盘「暂停」同一条实例状态机，RFC-34）。</summary>
    public bool TogglePause(string id)
    {
        PetInstance? instance = Find(id)?.Instance;
        if (instance is null)
        {
            return false;
        }

        if (instance.State == InstanceState.Running)
        {
            instance.Suspend("tray-pause");
        }
        else if (instance.State == InstanceState.Suspended)
        {
            instance.Resume("tray-resume");
        }

        KernelLog.Write($"pet-set-pause id={id} state={instance.State}");
        return true;
    }

    /// <summary>藏起这一只并停掉它的泵（隐藏的宠物不该继续烧 CPU）。</summary>
    public bool Hide(string id)
    {
        Slot? slot = Find(id);
        if (slot is null)
        {
            return false;
        }

        slot.Loop.Hide();
        KernelLog.Write($"pet-set-hide id={id}");
        return true;
    }

    /// <summary>把这一只挪回屏幕正中（托盘的「移动」：单只时是主窗那句 MoveToCenter 的逐只版）。</summary>
    public bool MoveToCenter(string id)
    {
        Slot? slot = Find(id);
        if (slot is null)
        {
            return false;
        }

        slot.Window.MoveToCenter();
        KernelLog.Write($"pet-set-move-center id={id}");
        return true;
    }

    /// <summary>
    /// 把这一只从桌上拿掉：停泵 → 释放窗口 → 释放实例 → 从槽位表移除。
    /// 素材目录不删（移除是「不再摆它」，不是「删用户文件」）；清单那一条由调用方删
    /// （<see cref="PetDataStore.RemoveFromCatalog"/>），本类不碰存储。
    /// </summary>
    public bool Remove(string id, out string packPath)
    {
        packPath = string.Empty;
        Slot? slot = Find(id);
        if (slot is null)
        {
            return false;
        }

        packPath = slot.PackPath;
        DisposeSlot(slot);
        _slots.Remove(slot);
        KernelLog.Write($"pet-set-remove id={id} {Describe()}");
        return true;
    }

    /// <summary>释放一条槽位的那三样东西；任何一步失败都只记日志（移除要继续）。</summary>
    private static void DisposeSlot(Slot slot)
    {
        try
        {
            slot.Loop.Dispose();
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-set-remove-loop-failed {error.GetType().Name}: {error.Message}");
        }

        try
        {
            slot.Window.Dispose();
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-set-remove-window-failed {error.GetType().Name}: {error.Message}");
        }

        try
        {
            slot.Instance?.Dispose();
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-set-remove-instance-failed {error.GetType().Name}: {error.Message}");
        }
    }
}
