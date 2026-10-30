// 窗口位置的恢复与落盘（RFC-22 §3 的时机 + RFC-24 §3.9 的落点）。
//
// 为什么单独一片：主文件只留「字段/构造/滴答/释放」，而位置有两个**时机**（首滴答恢复一次、
// 拖拽落地落盘一次）和一条纯策略（可见性，见 Compositor/ScreenPlacement.cs），
// 塞进滴答正文只会让那条注释比代码长。
//
// RFC-24 之后事实源换了：位置的**主人**是宠物状态层（`<数据区>/state/<实例>.json` 的保留键
// `position`），`settings.json` 的 `petPosition` 只在两种场合出现 ——
//   ① 升级前的旧文件（读到就一次性搬进状态层，成功即删旧键）；
//   ② 没有状态层的形态（自检、或名册没给数据区）：那时退回旧路径，行为与 RFC-22 完全一致。
// 两份数据同一事实必然漂移（RFC-18 §3 的纪律），所以②是过渡形态而不是第二主人。
//
// 本片不做的事（RFC-22 §7）：显示器拓扑换算、每宠物独立位置、运行中 DPI 变化的重新摆放。
using System;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>上次落盘的坐标。null = 本次运行还没落过盘（或读出来是空）。
    /// 用途只有一个：同一个位置被重复上报时不重复写盘。</summary>
    private Point? _lastSavedPosition;

    /// <summary>当前宠物有没有接上落盘层（没有 = 退回 `settings.json`，见文件头②）。</summary>
    private bool HasStateLayer => _pet?.StateLayer is PersistentPetState;

    /// <summary>
    /// 首滴答里窗口刚显示出来（`_loop.Start()`）之后调用一次（RFC-22 §3.3）：
    /// 保存的位置还有面积落在某个工作区里就摆回去，否则保留窗口层已经摆好的默认位（右下角）。
    /// 判定失败时**不**改写文件：多屏插回来以后旧值仍然是对的（§3.4）。
    ///
    /// internal 而不是 private：自检要能在不 `Show()` 窗口的前提下驱动一次恢复（§4 的
    /// `ui-position-restore-applies`），否则那个判据只能证明策略函数对，证明不了接线对。
    /// </summary>
    internal void TryRestorePosition()
    {
        Point? saved = ReadStatePosition();
        if (saved is null)
        {
            saved = MigrateLegacyPosition();
        }

        if (saved is not Point target)
        {
            return;
        }

        Size size = _window.WindowSize;
        Rectangle[] areas = Screen.AllScreens.Select(s => s.WorkingArea).ToArray();
        if (!ScreenPlacement.IsVisible(target, size, areas))
        {
            KernelLog.Write($"pet-position-discarded reason=offscreen x={target.X} y={target.Y} size={size.Width}x{size.Height}");
            return;
        }

        _window.SetWindowPosition(target.X, target.Y);
        _lastSavedPosition = target;
        KernelLog.Write($"pet-position-restored x={target.X} y={target.Y}");
    }

    /// <summary>
    /// 窗口事件里只关心一条：拖拽落地（`core.window.moved` 的原生生产点，
    /// `LayeredFrameWindow.cs` 的 `OnMouseUp`）。载荷坏掉就记一行、不落盘 ——
    /// 宁可这次位置没存上，也不要写进一个猜出来的坐标。
    /// </summary>
    private void OnWindowEventForPosition(string type, JsonObject? payload)
    {
        if (!string.Equals(type, EventCatalog.WindowMoved, StringComparison.Ordinal) || payload is null)
        {
            return;
        }

        if (payload["position"] is not JsonArray position || position.Count < 2)
        {
            KernelLog.Write("pet-position-payload-invalid reason=no-position");
            return;
        }

        try
        {
            SavePetPosition(position[0]!.GetValue<int>(), position[1]!.GetValue<int>());
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-position-payload-invalid {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// 落盘一次。有状态层 → 写保留键 `position` 并**当场提交**（RFC-24 §3.8 的提交点 1：
    /// 用户停手那一下，不在 tick 里）；没有状态层 → 走旧路径：**读-改-写**整份设置、
    /// 只换 `PetPosition` 一个字段再写回（RFC-16 §3.2 的纪律；凭记忆造一份 UserSettings
    /// 写回去会抹掉别的字段，例如引导标记）。
    /// 坐标没变就不写；写失败只记日志 —— 位置存不下来不该打断用户拖动。
    /// </summary>
    internal void SavePetPosition(int x, int y)
    {
        var next = new Point(x, y);
        if (_lastSavedPosition == next)
        {
            return;
        }

        if (HasStateLayer)
        {
            if (WriteStatePosition(next))
            {
                _lastSavedPosition = next;
                KernelLog.Write($"pet-position-saved x={x} y={y} target=state");
            }
            else
            {
                KernelLog.Write($"pet-position-save-failed target=state x={x} y={y}");
            }

            return;
        }

        if (_settings is null)
        {
            return;
        }

        try
        {
            _settings.Update(current => current with { PetPosition = next });
            _lastSavedPosition = next;
            KernelLog.Write($"pet-position-saved x={x} y={y} target=settings");
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-position-save-failed {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>读状态层的保留键（形状 `{ "x": int, "y": int }`）。没有状态层 / 没这个键 / 形状坏 → null。</summary>
    private Point? ReadStatePosition()
    {
        JsonNode? node = _pet?.ReadState(PersistentPetState.PositionKey);
        if (node is not JsonObject position)
        {
            return null;
        }

        if (position["x"] is not JsonValue x || position["y"] is not JsonValue y)
        {
            KernelLog.Write("pet-position-state-invalid reason=shape");
            return null;
        }

        try
        {
            return new Point(x.GetValue<int>(), y.GetValue<int>());
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-position-state-invalid {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    /// <summary>写状态层 + 提交。false = 没有状态层，或提交被拒（原因已经由状态层记日志）。</summary>
    private bool WriteStatePosition(Point point)
    {
        if (_pet is null)
        {
            return false;
        }

        var payload = new JsonObject { ["x"] = point.X, ["y"] = point.Y };
        return _pet.WriteHostState(PersistentPetState.PositionKey, payload) && _pet.CommitState();
    }

    /// <summary>
    /// RFC-24 §3.9 的一次性迁移：状态层没有 `position`、而 `settings.json` 有 `petPosition`
    /// 时搬过去并删旧键。搬成功才删 —— 状态写不进去就保留旧键（下次启动还能再迁一次，
    /// 代价是两份数据暂时并存，日志里有一行 migration-deferred）。
    /// 返回值是「这次要用的坐标」：迁移失败时仍然用旧值恢复，不让用户看到宠物跳回右下角。
    /// </summary>
    private Point? MigrateLegacyPosition()
    {
        if (_settings is null)
        {
            return null;
        }

        UserSettings settings;
        try
        {
            settings = _settings.Load();
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-position-load-failed {error.GetType().Name}: {error.Message}");
            return null;
        }

        if (settings.PetPosition is not Point legacy)
        {
            return null;
        }

        if (!HasStateLayer)
        {
            // 没有状态层就没有迁移的目的地：旧路径照旧（自检与无数据区形态）。
            return legacy;
        }

        if (!WriteStatePosition(legacy))
        {
            KernelLog.Write($"pet-position-migration-deferred x={legacy.X} y={legacy.Y}");
            return legacy;
        }

        try
        {
            // `Save` 对 null 字段不写键 ⇒ 旧键真的从文件里消失（RFC-16 §3.2 的读-改-写）。
            _settings.Update(current => current with { PetPosition = null });
            KernelLog.Write($"pet-position-migrated x={legacy.X} y={legacy.Y}");
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-position-migration-deferred step=clear-legacy {error.GetType().Name}: {error.Message}");
        }

        return legacy;
    }
}
