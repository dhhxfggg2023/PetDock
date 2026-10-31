// 宠物页「这一只的大小」（2026-10-31；用户：「调整大小应该是每个宠物单独调整，
// 你这样我怎么调整」）。
//
// 为什么单独一个文件：PetsPage.xaml.cs 已经贴着 D-03c 的 400 行上限。
// 这一块与「列表 / 多开 / 删除」不共享状态，只读写选中那一行的 SizePx ——
// 与 PetsPage.Care.cs 拆出去的理由完全相同。
using System;
using System.Windows;
using PetDock.Core.Ui;

namespace PetDock.Ui.Default;

public partial class PetsPage
{
    /// <summary>拖动中只更新读数，松手才落盘/改窗口（每次 ValueChanged 都写盘会让拖拽变卡）。</summary>
    private bool _sizeDragging;

    /// <summary>正在把快照写进滑块：此时 ValueChanged 是回填，不是用户操作。</summary>
    private bool _bindingSize;

    /// <summary>
    /// 把这一行的当前边长放上滑块（2026-10-31）。回填期间 _bindingSize 拦住 ValueChanged，
    /// 否则「选中一行」会被当成「用户拖了一下」而触发一次落盘。
    /// </summary>
    private void BindDetailSize(PetRow row)
    {
        _bindingSize = true;
        try
        {
            int clamped = Math.Clamp(row.SizePx, (int)DetailSizeSlider.Minimum, (int)DetailSizeSlider.Maximum);
            DetailSizeSlider.Value = clamped;
            DetailSizeValue.Text = _context.T("cp.pets.detail.sizeValue", clamped);
            System.Windows.Automation.AutomationProperties.SetName(
                DetailSizeSlider, _context.T("cp.pets.detail.size") + " " + row.Name);
        }
        finally
        {
            _bindingSize = false;
        }
    }

    /// <summary>拖动中只更新读数；松手才真正生效（见 OnDetailSizeDragCompleted）。</summary>
    private void OnDetailSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_bindingSize)
        {
            return;
        }

        int size = (int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero);
        DetailSizeValue.Text = _context.T("cp.pets.detail.sizeValue", size);
        if (!_sizeDragging)
        {
            ApplyDetailSize(size);
        }
    }

    private void OnDetailSizeDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _sizeDragging = false;
        if (!_bindingSize)
        {
            ApplyDetailSize((int)Math.Round(DetailSizeSlider.Value, MidpointRounding.AwayFromZero));
        }
    }

    /// <summary>
    /// 改**选中那一只**的边长。没选中就什么都不做（宠物页没有「默认大小」这个概念 ——
    /// 那是设置页那条滑块的事）。失败走与列表操作同一个错误条。
    /// </summary>
    private void ApplyDetailSize(int size)
    {
        string? id = SelectedPetId;
        if (id is null)
        {
            return;
        }

        string? problem = _context.SetPetSize(id, size);
        if (problem is not null)
        {
            ShowError(problem);
            return;
        }

        HideError();
        _context.Refresh();
    }
}
