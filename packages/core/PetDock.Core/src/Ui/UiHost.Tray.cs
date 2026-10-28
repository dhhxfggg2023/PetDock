// 托盘命令的实现（主控面板 / 每宠物一条子菜单 / 退出）。
// 为什么单独一个文件：这些命令是「用户的一次点击」到「内核的一次动作」的映射，
// 与授权链路无关，混在一起会让人读授权代码时读到托盘逻辑。
//
// RFC-37 §3.6（片 3）：每条子菜单带着一只宠物的 id 下来，命令按 id 路由 ——
// 不能再用「当前宠物」这种隐式指代，否则桌上有三只时命令永远只动主窗那只。
using System.Collections.Generic;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>
    /// 右键宠物 → 在光标处弹出托盘那份菜单（2026-10-26 用户要求「右键宠物可以弹菜单」）。
    /// 复用托盘那一份而不是另建：原规格 18 §0 「未写进清单的界面元素不许出现」——
    /// 另建一套就得先在文档里新增一张清单，而用户要的是「能点到那些功能」，不是第二份菜单。
    /// 手势归属：右键从此归宿主，插件层原有的右键手势因此让位（用户 2026-10-26 明确同意）。
    /// </summary>
    private void ShowPetMenu(System.Drawing.Point screen)
    {
        TrayHost? tray = _tray;
        if (tray is null)
        {
            KernelLog.Write("pet-menu-skip reason=no-tray");
            return;
        }

        tray.ShowMenuAt(screen);
        KernelLog.Write($"pet-menu-shown x={screen.X} y={screen.Y}");
    }

    /// <summary>主窗那只的菜单 id。它可能没有实例（内核纯色兜底），所以要一个不会撞车的名字。</summary>
    private const string PrimaryPetId = "primary";

    /// <summary>
    /// 托盘上现在该有哪几条「宠物：<名称>」：主窗那只 + 集合里的每一只。
    /// 顺序与桌面上从左到右的顺序一致（集合的槽位顺序就是摆位顺序），用户点哪条指哪只。
    /// </summary>
    private IReadOnlyList<PetMenuItem> BuildPetMenu()
    {
        var items = new List<PetMenuItem>();

        if (!_primaryRemoved)
        {
            string primaryId = _pet?.Id ?? PrimaryPetId;
            items.Add(new PetMenuItem(
                _pet is null ? I18n.Get("ui.tray.kernelFallback") : primaryId,
                () => MovePetById(primaryId),
                () => PausePetById(primaryId),
                () => RemovePetById(primaryId)));
        }

        if (_petSet is not null)
        {
            foreach (PetWindowSet.Entry entry in _petSet.Entries())
            {
                string id = entry.Id;
                items.Add(new PetMenuItem(
                    entry.Title,
                    () => MovePetById(id),
                    () => PausePetById(id),
                    () => RemovePetById(id)));
            }
        }

        return items;
    }

    /// <summary>桌面上还剩几只（主窗那只被移除后不算）。</summary>
    private int LivePetCount() => (_primaryRemoved ? 0 : 1) + (_petSet?.Slots.Count ?? 0);

    /// <summary>
    /// 主窗之外那几只的只读行（面板的宠物页用）。来源写「已导入」而不是插件 id：
    /// 用户认的是「我从哪装进来的」，不是内部实例标识。
    /// </summary>
    private IReadOnlyList<PetLine> BuildExtraPetLines()
    {
        var lines = new List<PetLine>();
        if (_petSet is null)
        {
            return lines;
        }

        foreach (PetWindowSet.Entry entry in _petSet.Entries())
        {
            PetInstance? instance = entry.Slot.Instance;
            string state = instance is null
                ? I18n.Get("cp.plugins.state.off")
                : PluginHost.DescribeInstanceState(instance.State)
                    + (instance.BrainDegraded ? I18n.Get("ui.roster.state.degraded") : string.Empty);
            lines.Add(new PetLine(
                Id: entry.Id,
                Name: entry.Title,
                Source: I18n.Get("cp.pets.source.imported"),
                State: state,
                Memory: UiRoster.ProcessMemoryMb()));
        }

        return lines;
    }

    /// <summary>
    /// 面板「移除选中宠物」的宿主侧执行体（UI 线程）。
    /// 只把这一只从桌上与清单里拿掉；素材文件留在数据目录里，用户还能再导入。
    /// </summary>
    internal string? RemovePetFromPanel(string petId)
    {
        if (IsPrimary(petId) || string.Equals(petId, PrimaryPetId, System.StringComparison.Ordinal))
        {
            RemovePetById(petId);
            return null;
        }

        if (_petSet?.Find(petId) is null)
        {
            return I18n.Get("cp.pets.remove.missing");
        }

        RemovePetById(petId);
        return null;
    }

    /// <summary>把这一只挪回屏幕正中。</summary>
    private void MovePetById(string id)
    {
        if (IsPrimary(id))
        {
            _window.MoveToCenter();
            return;
        }

        _petSet?.MoveToCenter(id);
    }

    /// <summary>暂停/恢复这一只（主窗那只走 UiHost 持有的实例）。</summary>
    private void PausePetById(string id)
    {
        if (!IsPrimary(id))
        {
            _petSet?.TogglePause(id);
            return;
        }

        if (_pet is null)
        {
            return;
        }

        if (_pet.State == InstanceState.Running)
        {
            _pet.Suspend("tray-pause");
            return;
        }

        if (_pet.State == InstanceState.Suspended)
        {
            _pet.Resume("tray-resume");
        }
    }

    /// <summary>
    /// 把这一只从桌上和清单里移除。
    /// 主窗那只的**实例**归 Program / CAS（RFC-15 §3.2），本片不在这里释放它 ——
    /// 只把它从清单与托盘里拿掉并藏起窗口，真正的卸载在重启时发生
    /// （`pet-remove-primary deferred=restart` 这行日志是它的取证）。
    /// </summary>
    private void RemovePetById(string id)
    {
        if (IsPrimary(id))
        {
            _primaryRemoved = true;
            _loop.Hide();
            _store?.RemoveFromCatalog(_pet?.Id ?? string.Empty);
            KernelLog.Write($"pet-remove-primary id={_pet?.Id ?? "(none)"} deferred=restart");
            RefreshTrayPets();
            return;
        }

        if (_petSet?.Remove(id, out string packPath) == true)
        {
            _store?.RemoveFromCatalog(id);
            KernelLog.Write($"pet-remove-extra id={id} pack={packPath}");
        }

        RefreshTrayPets();
    }

    /// <summary>托盘与提示文字同步到「现在桌上有几只」。</summary>
    private void RefreshTrayPets()
    {
        _tray.SetPets(BuildPetMenu());
        _tray.SetPetCount(LivePetCount());
    }

    private bool IsPrimary(string id) =>
        !_primaryRemoved && string.Equals(id, _pet?.Id ?? PrimaryPetId, System.StringComparison.Ordinal);

    /// <summary>
    /// 退出确认（原规格 18 §1：退出是固定项，但要有一次确认）。
    /// 走 ConfirmDialog 而不是 MessageBox：后者不进置顶档，前台有最大化应用时会被压在后面，
    /// 用户以为关掉了、进程还在（真机取证见 PetDockDialog.cs）。
    /// </summary>
    private void ExitApp()
    {
        if (ConfirmDialog.Ask(I18n.Get("ui.tray.exitConfirm"), I18n.Get("ui.tray.exitConfirmTitle")))
        {
            ExitThread();
        }
    }
}
