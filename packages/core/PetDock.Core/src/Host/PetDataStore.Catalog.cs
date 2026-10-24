// 清单（pets.json）的**删除**一侧（RFC-37 §3.6 片 3）。
//
// 为什么单独一个文件：D-03c 的单文件 400 行上限。PetDataStore 主文件已经装着
// 「读 / 写 / 登记 / 选中 / 落盘」，删除是第 5 件事，再塞进去就把主文件顶过上限了
// （实测 421 行）—— 拆出来的代价只是多一个 partial 文件头，收益是不必为了 21 行去抬 D-03c。
using System;
using System.Globalization;
using System.Text.Json.Nodes;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal sealed partial class PetDataStore
{
    /// <summary>
    /// 把一条登记从清单里**删掉**（RFC-37 §3.6 片 3：托盘的「移除」）。
    /// 两件事一件都不能少：条目本身移除；被移除的那只如果正是 active，就把 active 交给
    /// 剩下的第一只（一只不剩就置空）。否则重启之后 active 指向一只已经不在清单里的宠物，
    /// 主窗会退回默认那只 —— 用户看到的是「我移除了 A，结果 B 也变成了默认宠物」。
    /// 素材目录不删：移除是「不再摆它」，不是「删用户文件」。
    /// </summary>
    public bool RemoveFromCatalog(string instanceId)
    {
        JsonObject catalog = ReadCatalog();
        if (catalog["pets"] is not JsonArray pets)
        {
            return false;
        }

        var kept = new JsonArray();
        string? firstKept = null;
        bool removed = false;
        foreach (JsonNode? item in pets)
        {
            if (item is JsonObject entry && string.Equals((string?)entry["id"], instanceId, StringComparison.Ordinal))
            {
                removed = true;
                continue;
            }

            if (item is not null)
            {
                firstKept ??= (item is JsonObject keptEntry ? (string?)keptEntry["id"] : null);
                kept.Add(item.DeepClone());
            }
        }

        if (!removed)
        {
            return false;
        }

        catalog["pets"] = kept;
        if (string.Equals((string?)catalog["active"], instanceId, StringComparison.Ordinal))
        {
            catalog["active"] = firstKept ?? string.Empty;
        }

        catalog["agent_id"] = "host";
        catalog["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        SaveCatalog(catalog);
        KernelLog.Write($"data-store-remove-catalog id={instanceId} left={kept.Count} active={(string?)catalog["active"] ?? "(none)"}");
        return true;
    }
}
