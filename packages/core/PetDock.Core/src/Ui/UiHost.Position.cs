// 窗口位置的恢复与落盘（RFC-22 §3）。
//
// 为什么单独一片：主文件只留「字段/构造/滴答/释放」，而位置有两个**时机**（首滴答恢复一次、
// 拖拽落地落盘一次）和一条纯策略（可见性，见 Compositor/ScreenPlacement.cs），
// 塞进滴答正文只会让那条注释比代码长。
//
// 本片不做的事（RFC-22 §7）：显示器拓扑换算、每宠物独立位置、运行中 DPI 变化的重新摆放、
// 把位置迁进 docs/17 的宠物状态层。
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
        if (_settings is null)
        {
            return;
        }

        UserSettings settings;
        try
        {
            settings = _settings.Load();
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-position-load-failed {error.GetType().Name}: {error.Message}");
            return;
        }

        if (settings.PetPosition is not Point saved)
        {
            return;
        }

        Size size = _window.WindowSize;
        Rectangle[] areas = Screen.AllScreens.Select(s => s.WorkingArea).ToArray();
        if (!ScreenPlacement.IsVisible(saved, size, areas))
        {
            KernelLog.Write($"pet-position-discarded reason=offscreen x={saved.X} y={saved.Y} size={size.Width}x{size.Height}");
            return;
        }

        _window.SetWindowPosition(saved.X, saved.Y);
        _lastSavedPosition = saved;
        KernelLog.Write($"pet-position-restored x={saved.X} y={saved.Y}");
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
    /// 落盘一次。**读-改-写**：先读出整份设置、只换 `PetPosition` 一个字段再写回
    /// （RFC-16 §3.2 的纪律；凭记忆造一份 UserSettings 写回去会抹掉别的字段，例如引导标记）。
    /// 坐标没变就不写；写失败只记日志 —— 位置存不下来不该打断用户拖动。
    /// </summary>
    internal void SavePetPosition(int x, int y)
    {
        if (_settings is null)
        {
            return;
        }

        var next = new Point(x, y);
        if (_lastSavedPosition == next)
        {
            return;
        }

        try
        {
            UserSettings current = _settings.Load();
            _settings.Save(current with { PetPosition = next });
            _lastSavedPosition = next;
            KernelLog.Write($"pet-position-saved x={x} y={y}");
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-position-save-failed {error.GetType().Name}: {error.Message}");
        }
    }
}
