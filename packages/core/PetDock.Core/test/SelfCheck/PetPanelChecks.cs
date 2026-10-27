// 宠物页的多只与移除（2026-10-28）。
//
// 多宠物的实例层/窗口层判据在 PetSetChecks / PetWindowChecks；本文件只看**面板**：
//   ① 快照里主窗之外的那几只真的进了行（不再只显示一只）；
//   ② 行可以被选中，移除意图带着正确的宠物 id 交给宿主。
// 不弹确认框（RemoveSelectedPet(confirm: false)）：无人值守环境里没有人会点「是」。
using System;
using System.Collections.Generic;
using System.Linq;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class PetPanelChecks
{
    internal delegate void Check(string name, bool ok, string detail);

    public static void Run(Check check)
    {
        check = check ?? throw new ArgumentNullException(nameof(check));

        var extras = new List<PetLine>
        {
            new("run.second", "second-pack", I18n.Get("cp.pets.source.imported"), "运行中", "12.0 MB"),
        };

        UiSnapshot snapshot = UiRoster.Build(null, null, null, null, extras);
        check(
            "ui-pet-roster-multi",
            snapshot.Pets.Count == 1 && string.Equals(snapshot.Pets[0].Id, "run.second", StringComparison.Ordinal),
            $"pets={snapshot.Pets.Count} first={snapshot.Pets.FirstOrDefault()?.Id ?? "(none)"}");

        string? removed = null;
        using var panel = new ControlPanel(
            () => snapshot,
            showWindow: false,
            removePet: id =>
            {
                removed = id;
                return null;
            });

        bool selected = panel.SelectPet("run.second");
        check(
            "ui-pet-panel-rows",
            panel.PetRowCount == 1 && selected && string.Equals(panel.SelectedPetId, "run.second", StringComparison.Ordinal),
            $"rows={panel.PetRowCount} selected={panel.SelectedPetId ?? "(none)"} ok={selected}");

        check(
            "ui-pet-panel-buttons",
            panel.PetButtonNames.Contains("remove-pet") && panel.PetButtonNames.Contains("import-pack"),
            $"buttons={string.Join(",", panel.PetButtonNames)}");

        panel.RemoveSelectedPet(confirm: false);
        check(
            "ui-pet-panel-remove-callback",
            string.Equals(removed, "run.second", StringComparison.Ordinal),
            $"removed={removed ?? "(none)"}");
    }
}
