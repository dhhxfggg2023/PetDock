// 清单（pets.json）的**删除**一侧（RFC-37 §3.6 片 3）。
//
// 为什么单独一个文件：D-03c 的单文件 400 行上限。PetDataStore 主文件已经装着
// 「读 / 写 / 登记 / 选中 / 落盘」，删除是第 5 件事，再塞进去就把主文件顶过上限了
// （实测 421 行）—— 拆出来的代价只是多一个 partial 文件头，收益是不必为了 21 行去抬 D-03c。
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

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
    /// <summary>按清单 id 找一只（找不到或目录已消失时 null）。</summary>
    public ActivePack? Find(string instanceId)
    {
        foreach (ActivePack pack in ReadPets())
        {
            if (string.Equals(pack.InstanceId, instanceId, StringComparison.Ordinal))
            {
                return pack;
            }
        }

        return null;
    }

    /// <summary>
    /// 旧清单条目没有 name 的补名（2026-10-29）：从素材包自己的 petdock-sample.json 读 name。
    /// 只补空值，不覆盖用户已经看到的名字；读不到就保持 ReadPets 的「回落到目录名」。
    /// 启动时调一次：补完立即落盘，之后每次启动都是零写入。
    /// </summary>
    public int MigrateMissingNames()
    {
        JsonObject catalog = ReadCatalog();
        if (catalog["pets"] is not JsonArray pets)
        {
            return 0;
        }

        int filled = 0;
        foreach (JsonNode? item in pets)
        {
            if (item is not JsonObject entry
                || !string.IsNullOrWhiteSpace((string?)entry["name"])
                || (string?)entry["folder"] is not string folder
                || string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            string? name = ReadPackDisplayName(Path.Combine(PacksDir, folder));
            if (name is null)
            {
                continue;
            }

            entry["name"] = name;
            filled++;
        }

        if (filled == 0)
        {
            return 0;
        }

        catalog["agent_id"] = "host";
        catalog["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        SaveCatalog(catalog);
        KernelLog.Write($"data-store-name-migration filled={filled}");
        return filled;
    }

    /// <summary>素材包清单里的显示名；文件缺失、坏 JSON 或没有 name 都返回 null（不猜名字）。</summary>
    private static string? ReadPackDisplayName(string packDir)
    {
        string manifest = Path.Combine(packDir, "petdock-sample.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest, Encoding.UTF8));
            if (!doc.RootElement.TryGetProperty("name", out JsonElement value)
                || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? name = value.GetString();
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (Exception error)
        {
            KernelLog.Write($"data-store-name-migration-skip path={packDir} {error.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// 给同一只宠物再开一只（2026-10-28 第七轮「同一个桌宠也能多开」）：
    /// 清单里新增一条**指向同一个素材目录**的条目，id 用 #2/#3 区分。
    /// 不复制素材：多开的每只只是同一个包的另一个实例，删掉一只不影响其余几只。
    /// </summary>
    public ActivePack AddInstance(string baseId, string displayName)
    {
        JsonObject catalog = ReadCatalog();
        if (catalog["pets"] is not JsonArray pets)
        {
            throw new InvalidOperationException(I18n.Get("host.pets.err.no-catalog"));
        }

        string? folder = null;
        string stem = baseId;
        foreach (JsonNode? item in pets)
        {
            if (item is not JsonObject entry)
            {
                continue;
            }

            string? id = (string?)entry["id"];
            if (string.Equals(id, baseId, StringComparison.Ordinal))
            {
                folder = (string?)entry["folder"];
                int hash = baseId.IndexOf('#', StringComparison.Ordinal);
                if (hash > 0)
                {
                    stem = baseId[..hash];
                }
            }
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new InvalidOperationException(I18n.Get("host.pets.err.not-found"));
        }

        int n = 2;
        string nextId;
        while (true)
        {
            nextId = stem + "#" + n.ToString(CultureInfo.InvariantCulture);
            bool exists = false;
            foreach (JsonNode? item in pets)
            {
                if (item is JsonObject entry && string.Equals((string?)entry["id"], nextId, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                break;
            }

            n++;
        }

        pets.Add(new JsonObject
        {
            ["id"] = nextId,
            ["folder"] = folder,
            ["name"] = string.IsNullOrWhiteSpace(displayName) ? nextId : displayName,
        });
        catalog["pets"] = pets;
        catalog["agent_id"] = "host";
        catalog["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        SaveCatalog(catalog);
        KernelLog.Write($"data-store-add-instance id={nextId} folder={folder}");
        return new ActivePack(nextId, Path.Combine(PacksDir, folder), displayName);
    }

    /// <summary>
    /// 删除一只宠物的素材目录，但只在**没有任何清单条目再引用它**时动手。
    /// 多开让「同一个目录」被多条条目引用；删第一只时删目录，其余几只就一起没了。
    /// 只删数据区 packs/ 下的目录：pets/sample-pet 这类随包素材永远不碰。
    /// </summary>
    public bool DeletePackIfUnreferenced(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full = Path.GetFullPath(path);
        string packs = Path.GetFullPath(PacksDir);
        if (!full.StartsWith(packs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            KernelLog.Write($"data-store-delete-refused path={full} reason=outside-packs");
            return false;
        }

        foreach (ActivePack pack in ReadPets())
        {
            if (string.Equals(Path.GetFullPath(pack.Path), full, StringComparison.OrdinalIgnoreCase))
            {
                KernelLog.Write($"data-store-delete-kept path={full} reason=still-referenced ref={pack.InstanceId}");
                return false;
            }
        }

        Discard(full);
        bool gone = !Directory.Exists(full);
        KernelLog.Write($"data-store-delete-pack path={full} gone={gone}");
        return gone;
    }

}
