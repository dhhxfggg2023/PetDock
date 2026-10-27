// 列表末列的开关（2026-10-28）：插件页要「最右边一枚小开关」，
// 而不是再放一个「禁用选中插件」按钮 —— 按钮要先选中、再点，两步表达一个开关状态；
// 开关则是「这一行现在开不开」的直接答案，点一下就行。
//
// 为什么画在列表里而不是放 CheckBox 控件：ListView 不支持在子项里放控件；
// 用 CheckBoxes 则是系统方框，与深色行、与自绘的勾选框都不一致（见 ThemedList.cs 顶部注释）。
// 所以这里沿用同一套自绘：状态由页面给，点击由列表翻译成一次回调。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

/// <summary>问某一行的开关状态（D-03b：自声明委托）。</summary>
internal delegate bool RowSwitchState(ListViewItem row);

/// <summary>某一行被点在开关上（页面负责落账并刷新行）。</summary>
internal delegate void RowSwitchToggle(ListViewItem row);

internal sealed partial class ThemedList
{
    private bool _rowSwitches;

    /// <summary>是否把末列当成开关列（打开后末列不再画文字）。</summary>
    public bool RowSwitches
    {
        get => _rowSwitches;
        set
        {
            if (_rowSwitches == value)
            {
                return;
            }

            _rowSwitches = value;
            FitColumns();
            Invalidate();
        }
    }

    /// <summary>某一行的开关当前是否打开（页面给，列表不自己存状态）。</summary>
    public RowSwitchState? IsRowSwitchedOn { get; set; }

    /// <summary>某一行的开关是否可点（不可点仍按当前状态画，只是不响应）。</summary>
    public RowSwitchState? IsRowSwitchEnabled { get; set; }

    /// <summary>用户在开关上点了一下（页面负责翻转状态并落账）。</summary>
    public RowSwitchToggle? RowSwitchToggled { get; set; }

    /// <summary>末列需要的最小宽度（设备像素）：胶囊 + 两侧留白。</summary>
    private int SwitchSlotWidth() => Theme.Dip(this, 44) + (Theme.Dip(this, Theme.SpaceMd) * 2);

    /// <summary>胶囊轨道的尺寸（设备像素，按 DPI 取）。</summary>
    private int SwitchTrackWidth(int dpi) => Theme.Dip(dpi, 40);

    private int SwitchTrackHeight(int dpi) => Theme.Dip(dpi, 22);

    /// <summary>开关在某个单元格里的位置：水平靠右、垂直居中。</summary>
    private Rectangle SwitchBounds(Rectangle cell, int dpi)
    {
        int width = SwitchTrackWidth(dpi);
        int height = SwitchTrackHeight(dpi);
        int pad = Theme.Dip(dpi, Theme.SpaceMd);
        return new Rectangle(
            cell.Right - pad - width,
            cell.Y + ((cell.Height - height) / 2),
            width,
            height);
    }

    /// <summary>画末列的开关；不是开关列返回 false（调用方继续按普通单元格画）。</summary>
    private bool DrawRowSwitch(Graphics g, DrawListViewSubItemEventArgs e, int dpi)
    {
        if (!_rowSwitches || Columns.Count == 0 || e.ColumnIndex != Columns.Count - 1)
        {
            return false;
        }

        bool on = e.Item is ListViewItem row && (IsRowSwitchedOn?.Invoke(row) ?? false);
        bool usable = e.Item is not ListViewItem probe || (IsRowSwitchEnabled?.Invoke(probe) ?? true);
        Rectangle track = SwitchBounds(e.Bounds, dpi);

        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            Color fill = !usable ? Theme.Line : (on ? Theme.Accent : Theme.TextDim);
            using (var brush = Theme.Brush(fill))
            {
                int r = track.Height / 2;
                g.FillEllipse(brush, track.X, track.Y, r * 2, r * 2);
                g.FillEllipse(brush, track.Right - (r * 2), track.Y, r * 2, r * 2);
                g.FillRectangle(brush, track.X + r, track.Y, track.Width - (r * 2), track.Height);
            }

            // 圆钮：开 → 贴右；关 → 贴左。留 2dip 内缩，胶囊才看得出是「槽里滑动的钮」。
            int padIn = Theme.Dip(dpi, 2);
            int knob = track.Height - (padIn * 2);
            int knobX = on ? track.Right - padIn - knob : track.X + padIn;
            using var knobBrush = Theme.Brush(usable ? Color.White : Theme.Bg);
            g.FillEllipse(knobBrush, knobX, track.Y + padIn, knob, knob);
        }
        finally
        {
            g.SmoothingMode = previous;
        }

        return true;
    }

    /// <summary>点是否落在某一行末列的开关上。</summary>
    private bool HitRowSwitch(int x, int y, out ListViewItem? item)
    {
        item = null;
        if (!_rowSwitches || Columns.Count == 0)
        {
            return false;
        }

        ListViewItem? hit = GetItemAt(x, y);
        if (hit is null)
        {
            return false;
        }

        // ListView 没有公开的 GetSubItemRect：列宽就是本控件自己量的（FitColumns），
        // 所以按行矩形 + 前面各列宽度推出末列的单元格即可；横向滚动时行矩形已含偏移。
        Rectangle row = hit.GetBounds(ItemBoundsPortion.Entire);
        int cellX = row.X;
        for (int c = 0; c < Columns.Count - 1; c++)
        {
            cellX += Columns[c].Width;
        }

        var cell = new Rectangle(cellX, row.Y, Columns[^1].Width, row.Height);
        if (!SwitchBounds(cell, Theme.DpiOf(this)).Contains(x, y))
        {
            return false;
        }

        item = hit;
        return true;
    }

    /// <summary>
    /// 翻转某一行的开关（鼠标路径与自检共用；不可点时返回 false 且不回调）。
    /// 自检直接调它，不依赖窗口句柄与鼠标坐标 —— 判据证明的是「行 → 回调」这段映射。
    /// </summary>
    internal bool TryToggleRowSwitch(ListViewItem? item)
    {
        if (!_rowSwitches || item is null || RowSwitchToggled is null)
        {
            return false;
        }

        if (IsRowSwitchEnabled?.Invoke(item) == false)
        {
            return false;
        }

        RowSwitchToggled(item);
        Invalidate();
        return true;
    }
}
