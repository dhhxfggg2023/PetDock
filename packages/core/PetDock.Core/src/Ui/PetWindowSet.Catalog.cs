// 摆位清单的纯函数半边（2026-10-30 从 PetWindowSet.cs 拆出：D-03c 单文件 400 行上限）。
//
// 为什么单独放：「清单里该摆哪几只」是**不需要窗口**就能判的，本仓的判据直接喂纯函数，
// 与「怎么建窗口/怎么起泵」是两件事。收起状态加进来之后这一点更明显了。
using System;
using System.Collections.Generic;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class PetWindowSet
{
    /// <summary>
    /// 纯函数：清单里除了「已经显示的那只」以外，还要摆哪几只。
    /// 单独拿出来是因为它是本片唯一有分支的判断，判决不必造窗口就能验。
    /// </summary>
    public static IReadOnlyList<PetDataStore.ActivePack> Plan(
        string? shownPackId,
        IReadOnlyList<PetDataStore.ActivePack> catalog,
        IReadOnlyCollection<string>? hidden = null)
    {
        var rest = new List<PetDataStore.ActivePack>();
        foreach (PetDataStore.ActivePack pack in catalog)
        {
            if (string.Equals(pack.InstanceId, shownPackId, StringComparison.Ordinal))
            {
                continue; // 这一只就在主窗里
            }

            // 用户收起的（还在清单里，只是不在桌面上）不摆出来。
            // 「从桌面移除」与「删除」分开之后，清单条目会留在宠物页 —— 摆位必须认这个状态，
            // 否则重启之后被收起的那几只又自己回到桌面上。
            if (hidden is not null && hidden.Contains(pack.InstanceId))
            {
                continue;
            }

            rest.Add(pack);
        }

        return rest;
    }
}
