// 宠物页的陪伴层（2026-10-29，P4 插件线第一块）：宿主给行，页面只摆按钮、把点击交回去。
//
// 为什么单独一个文件：PetsPage.xaml.cs 已经贴着 400 行上限（D-03c），
// 而这一块与「列表 / 多开 / 删除」没有共享状态，只是同一张页面的另一个区。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using PetDock.Core.Ui;
using Button = System.Windows.Controls.Button;

namespace PetDock.Ui.Default;

public partial class PetsPage
{
    private readonly ObservableCollection<CareRow> _careRows = new();

    /// <summary>构造期接上陪伴层那一块（文案 + 数据源）。</summary>
    private void InitCare()
    {
        CareTitle.Text = _context.T("cp.pets.care.title");
        CareRows.ItemsSource = _careRows;
    }

    /// <summary>
    /// 重读陪伴行。它在两次 Changed 之间也会变（时间在走），所以每次刷新页面都重读一遍，
    /// 不跟 Snapshot 一起缓存。
    /// </summary>
    private void RefreshCare()
    {
        _careRows.Clear();
        foreach (CompanionLine line in _context.Companions)
        {
            _careRows.Add(new CareRow(line));
        }

        CarePanel.Visibility = _careRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 按了陪伴层的一枚按钮：交给宿主转回插件。失败走页面错误条（与列表操作同一个出口），
    /// 成功后重读行 —— 数值变了，不能等下一次 Changed。
    /// </summary>
    private void OnCareCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CareButton button })
        {
            return;
        }

        string? problem = _context.InvokeCompanionCommand(button.PetId, button.CompanionId, button.CommandId);
        if (problem is not null)
        {
            ShowError(problem);
            return;
        }

        HideError();
        RefreshCare();
    }

    /// <summary>一行陪伴层（文案全部来自插件，页面不改写）。</summary>
    public sealed class CareRow
    {
        public CareRow(CompanionLine line)
        {
            PetName = line.PetName;
            Title = line.Title;
            Status = line.Status;
            Detail = line.Detail;
            var buttons = new List<CareButton>(line.Commands.Count);
            foreach (CompanionCommandLine command in line.Commands)
            {
                buttons.Add(new CareButton(line.PetId, line.CompanionId, command.CommandId, command.Label));
            }

            Commands = buttons;
        }

        public string PetName { get; }

        public string Title { get; }

        public string Status { get; }

        public string Detail { get; }

        public IReadOnlyList<CareButton> Commands { get; }
    }

    /// <summary>一枚按钮：把 (宠物, 陪伴层, 命令) 三个 id 一起带到点击处理里。</summary>
    public sealed class CareButton
    {
        public CareButton(string petId, string companionId, string commandId, string label)
        {
            PetId = petId;
            CompanionId = companionId;
            CommandId = commandId;
            Label = label;
        }

        public string PetId { get; }

        public string CompanionId { get; }

        public string CommandId { get; }

        public string Label { get; }
    }
}
