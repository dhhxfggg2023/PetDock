// RFC-37 §7 第 4 条（多宠物欠账）：**位置按宠物**，不按窗口索引。
//
// 片 1b 的摆位是「主窗位置向左第 N 个 140」—— 那只记住的是「排第几」，不是「我把这一只拖到哪儿」。
// 用户把第二只拖到副屏、重启之后它回到主窗左边第 2 格，看起来就是「位置丢了」。
//
// 为什么键是**清单 id**（`user.<folder>`）而不是实例 id：同一批槽位的实例 id 都来自同一个前缀
// （`resident-set.<pluginId>`），两只不同的宠物会算出同一个 key，第一只的位置会把第二只覆盖掉。
// 清单 id 是用户看得见的那只宠物（导入时一次生成、重启不变），位置跟着它走才对。
//
// 落点仍是 `settings.json` 的逐宠物条目（不是宠物状态层）：主窗那只的位置归状态层的保留键
// `position`（RFC-24 §3.9 已定），多出来的几只不是实例的生命周期所有者 —— 它们的窗口归本类，
// 位置同样归本类。两份数据各有各的主人，不重复存同一个事实。
//
// 判据面（--uitest 的 `ui-pet-position-persist-*`）：写入后重读一致 / 缺条目回落 / 未知 id 被忽略。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class PetWindowSet
{
    /// <summary>没有设置存储时的空表（自检与单窗装配）。</summary>
    private static readonly IReadOnlyDictionary<string, Point> NoPositions =
        new Dictionary<string, Point>(StringComparer.Ordinal);

    /// <summary>
    /// 逐宠物位置的落盘层（可空：没有数据区就没有持久化，行为与片 1b 完全一致）。
    /// 由装配路径（<see cref="FromCatalog"/>）给出，之后只读。
    /// </summary>
    internal UserSettingsStore? Settings { get; set; }

    /// <summary>
    /// 纯函数：这一只该摆在哪儿。
    /// 有存过、且那个位置还有面积落在某个工作区里 ⇒ 就用它；否则退回片 1b 的布局
    /// （主窗实时位置向左第 <paramref name="index"/> 个 140）。
    /// 屏外的旧值**丢位置但不改写文件**：多屏插回来以后旧坐标仍然是对的（与 RFC-22 §3.4 同一口径）。
    /// </summary>
    public static (int X, int Y) ResolveStart(
        Point? saved,
        (int X, int Y) primary,
        int index,
        Size size,
        IEnumerable<Rectangle> workAreas)
    {
        if (saved is Point target && ScreenPlacement.IsVisible(target, size, workAreas))
        {
            return (target.X, target.Y);
        }

        return (primary.X - (SpacingX * index), primary.Y);
    }

    /// <summary>这一只的清单 id（位置按它存）。槽位自己带的就是它；没带就去清单里按路径反查。</summary>
    private string KeyOf(Slot slot) => slot.PetId ?? CatalogIdOf(slot.PackPath) ?? IdOf(slot);

    /// <summary>按素材根反查清单 id（追加进桌面的那几只走这条：清单是 id 的唯一来源）。</summary>
    private string? CatalogIdOf(string packPath)
    {
        if (_store is null || string.IsNullOrEmpty(packPath))
        {
            return null;
        }

        foreach (PetDataStore.ActivePack pack in _store.ReadPets())
        {
            if (string.Equals(pack.Path, packPath, StringComparison.OrdinalIgnoreCase))
            {
                return pack.InstanceId;
            }
        }

        return null;
    }

    /// <summary>读逐宠物位置表。读不出来只记一行、按「都没存过」处理（位置存不下来不该拦住启动）。</summary>
    private IReadOnlyDictionary<string, Point> SavedPositions()
    {
        if (Settings is null)
        {
            return NoPositions;
        }

        try
        {
            return Settings.Load().PetPositions ?? NoPositions;
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-set-position-load-failed {error.GetType().Name}: {error.Message}");
            return NoPositions;
        }
    }

    /// <summary>各屏的工作区（可见性判定的边界，口径与 RFC-22 一致：不含任务栏）。</summary>
    private static Rectangle[] WorkAreas()
    {
        Screen[] screens = Screen.AllScreens;
        var areas = new Rectangle[screens.Length];
        for (int i = 0; i < screens.Length; i++)
        {
            areas[i] = screens[i].WorkingArea;
        }

        return areas;
    }

    /// <summary>把一只摆到它的起点（存过的位置优先，否则按索引间距）。</summary>
    private void PlaceAtStart(Slot slot, int index)
    {
        Point? saved = SavedPositions().TryGetValue(KeyOf(slot), out Point hit) ? hit : null;
        (int x, int y) = ResolveStart(saved, _primary.CurrentPosition, index, slot.Window.WindowSize, WorkAreas());
        slot.Window.MoveTo(x, y);
    }

    /// <summary>槽位窗口的拖拽落地 → 按宠物记下位置（读-改-写整份设置，只换自己那一条）。</summary>
    private void OnSlotMoved(Slot slot, string type, JsonObject? payload)
    {
        if (!string.Equals(type, EventCatalog.WindowMoved, StringComparison.Ordinal))
        {
            return;
        }

        if (payload?["position"] is not JsonArray position || position.Count < 2)
        {
            KernelLog.Write("pet-set-position-payload-invalid reason=no-position");
            return;
        }

        try
        {
            var point = new Point(position[0]!.GetValue<int>(), position[1]!.GetValue<int>());
            SavePosition(KeyOf(slot), point);
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-set-position-payload-invalid {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// 记下一只宠物的位置。读-改-写（RFC-16 §3.2 的纪律）：凭记忆造一份设置写回去会抹掉别的字段
    /// （引导标记、逐宠物的其它条目都会没）。坐标没变就不写，写失败只记日志 ——
    /// 位置存不下来不该打断用户拖动。返回是否**已是最新**（判据读它）。
    /// </summary>
    internal bool SavePosition(string id, Point point)
    {
        if (Settings is null || string.IsNullOrEmpty(id))
        {
            return false;
        }

        try
        {
            UserSettings current = Settings.Load();
            var map = new Dictionary<string, Point>(StringComparer.Ordinal);
            if (current.PetPositions is { } existing)
            {
                foreach (KeyValuePair<string, Point> pair in existing)
                {
                    map[pair.Key] = pair.Value;
                }
            }

            if (map.TryGetValue(id, out Point had) && had == point)
            {
                return true;
            }

            map[id] = point;

            // 写的时候再读一遍再改：这几毫秒里别的入口（切语言、插件启停）可能已写过盘。
            Settings.Update(actual =>
            {
                var next = actual.PetPositions is null
                    ? new Dictionary<string, Point>(StringComparer.Ordinal)
                    : new Dictionary<string, Point>(actual.PetPositions, StringComparer.Ordinal);
                next[id] = point;
                return actual with { PetPositions = next };
            });
            KernelLog.Write($"pet-set-position-saved id={id} x={point.X} y={point.Y}");
            return true;
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-set-position-save-failed id={id} {error.GetType().Name}: {error.Message}");
            return false;
        }
    }
}
