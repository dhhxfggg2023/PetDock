// 陪伴层的面板半边（2026-10-29，P4 插件线）：把每只宠物每个陪伴层的 Report 摆成一行。
//
// 页面不认宠物也不认插件：它只拿 IUiPageContext.Companions 的行，把按钮点击原样交回
// InvokeCompanionCommand(petId, companionId, commandId)。判定、说话、落盘都在宿主/插件侧。
// 与 Snapshot 不同，这份读数不缓存：它在两次 Changed 之间也会变（时间在走），页面刷新时自己再读。
using System;
using System.Collections.Generic;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    IReadOnlyList<CompanionLine> IUiPageContext.Companions => BuildCompanionLines();

    string? IUiPageContext.InvokeCompanionCommand(string petId, string companionId, string commandId) =>
        InvokeCompanionFromPanel(petId, companionId, commandId);

    /// <summary>
    /// 桌面上的每一只（主窗 + 额外槽位）。id 与宠物页的行一致：主窗用清单 id，槽位用槽位 id，
    /// 否则页面点按钮时按行 id 回查会找不到实例。
    /// </summary>
    private IEnumerable<(string Id, string Name, PetInstance Pet)> DesktopPets()
    {
        if (!_primaryRemoved && _pet is not null)
        {
            string id = _store?.ReadActive()?.InstanceId ?? _pet.Id;
            string name = _pet.Imported ? _pet.DisplayName : I18n.Get("cp.pets.name.builtin");
            yield return (id, name, _pet);
        }

        if (_petSet is null)
        {
            yield break;
        }

        foreach (PetWindowSet.Entry entry in _petSet.Entries())
        {
            if (entry.Slot.Instance is { } pet)
            {
                yield return (entry.Id, entry.Title, pet);
            }
        }
    }

    /// <summary>
    /// 一行一只宠物的一层陪伴。Report 由插件给快照（见 <see cref="IPetCompanion"/> 的注释），
    /// 宿主只转述；坏插件抛异常就跳过这一行并记 fault，不连累其他角色。
    /// </summary>
    private IReadOnlyList<CompanionLine> BuildCompanionLines()
    {
        var lines = new List<CompanionLine>();
        foreach ((string id, string name, PetInstance pet) in DesktopPets())
        {
            foreach (IPetCompanion companion in pet.Companions)
            {
                CompanionReport report;
                try
                {
                    report = companion.Report;
                }
                catch (Exception error)
                {
                    KernelLog.Write($"companion-report-failed instance={pet.Id} id={companion.Meta.Id}"
                        + $" {error.GetType().Name}: {error.Message}");
                    continue;
                }

                var commands = new List<CompanionCommandLine>(report.Commands.Count);
                foreach (CompanionCommand command in report.Commands)
                {
                    commands.Add(new CompanionCommandLine(command.Id, command.Label));
                }

                lines.Add(new CompanionLine(
                    id, name, companion.Meta.Id, report.Title, report.Status, report.Detail, commands));
            }
        }

        return lines;
    }

    /// <summary>
    /// 面板按下的那枚按钮：找到宠物与陪伴层就转回插件。返回 null = 成功；
    /// 非空是给用户看的原因（找不到宠物 / 找不到角色 / 插件抛异常，最后一种由实例给文案）。
    /// </summary>
    private string? InvokeCompanionFromPanel(string petId, string companionId, string commandId)
    {
        PetInstance? target = null;
        foreach ((string id, string _, PetInstance pet) in DesktopPets())
        {
            if (string.Equals(id, petId, StringComparison.Ordinal))
            {
                target = pet;
                break;
            }
        }

        if (target is null)
        {
            return I18n.Get("ui.companion.missing");
        }

        string? failure = target.InvokeCompanion(companionId, commandId);
        if (failure is null)
        {
            RefreshUi();
        }

        return failure;
    }
}
