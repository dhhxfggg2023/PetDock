// 主题化列表（原规格 18 §2.6）：把默认 ListView 的灰白表格改成深色行、自绘表头与选中条。
//
// 为什么仍用 ListView 而不是全新控件：判据与插件页/宠物页/设置页都按 Name 找它、按
// Columns/Items 读内容；换控件等于把所有页面重写一遍。自绘只替换「怎么画」，不改「有什么」。
//
// 行高与列宽都要按字体量出来：默认 ListView 的行高只有 17px 左右，中文字形在 150% 缩放
// 下会被裁掉下半截——用户点名不许出现这种问题（m03850）。行高用 SmallImageList 撑开
// （ListView 只认这个办法），列宽按表头与内容的最大测量宽度自适应。
//
// 勾选框（2026-10-27）：原生 ListView 的 CheckBoxes 在 OwnerDraw 下画的是系统方框（浅色、
// 与深色行打架），而且勾选状态存在控件内部、页面拿不到「为什么不可勾」。所以这里自己画：
// 状态与可勾性都从页面给的两个委托读，点击落在左侧留白里才算勾选，其余的点击照旧选行。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

/// <summary>问某一行的勾选状态（D-03b：自声明委托，不用系统泛型委托）。</summary>
internal delegate bool RowCheckState(ListViewItem row);

/// <summary>某一行被点在勾选框上（页面负责翻转状态并落账）。</summary>
internal delegate void RowCheckToggle(ListViewItem row);

/// <summary>主题化明细列表。列宽、行高、配色全部由绘制时按当前 DPI 决定。</summary>
internal sealed partial class ThemedList : ListView
{
    private readonly ImageList _rowSpacer;
    private bool _rowChecks;

    /// <summary>构造：撑开行高的占位图列表 + 双缓冲。</summary>
    public ThemedList()
    {
        View = View.Details;
        FullRowSelect = true;
        GridLines = false;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        OwnerDraw = true;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.FontFor(this, TypeRole.Body);
        DoubleBuffered = true;

        _rowSpacer = new ImageList
        {
            ImageSize = new Size(1, RowHeightDip()),
            ColorDepth = ColorDepth.Depth32Bit,
        };
        SmallImageList = _rowSpacer;
    }

    // 空白区的自绘（WM_ERASEBKGND / WM_PAINT 与表头高度读数）在 ThemedList.EmptyArea.cs。

    /// <summary>
    /// 每行左侧画一个勾选框（插件页「下次启动不再装载」= 原规格 18 §2.2 的那只复选框）。
    /// 打开后第一列的文字整体右移一个留白，点击留白交给 <see cref="RowCheckToggled"/>。
    /// </summary>
    public bool RowChecks
    {
        get => _rowChecks;
        set
        {
            if (_rowChecks == value)
            {
                return;
            }

            _rowChecks = value;
            FitColumns();
            Invalidate();
        }
    }

    /// <summary>某一行当前是否勾着（页面给，列表不自己存状态）。</summary>
    public RowCheckState? IsRowChecked { get; set; }

    /// <summary>某一行当前是否可勾（被禁用的插件不可勾：勾了也不会生效）。</summary>
    public RowCheckState? IsRowCheckEnabled { get; set; }

    /// <summary>用户在勾选框上点了一下（页面负责翻转状态并落账）。</summary>
    public RowCheckToggle? RowCheckToggled { get; set; }

    /// <summary>勾选框留白宽度（设备像素）。列宽与点击判定共用这一个数。</summary>
    public int CheckGutter => Theme.Dip(this, Theme.SpaceXl + Theme.SpaceXs);

    /// <summary>行高（设备像素）：字体行高 + 上下各一份内边距，保证中文字形不出界。</summary>
    private int RowHeightDip() => Theme.FontFor(this, TypeRole.Body).Height
        + Theme.Dip(this, Theme.SpaceSm) * 2;

    /// <summary>
    /// 列宽自适应：每列取「表头宽度」与「所有单元格宽度」的最大值，再整体按可用宽度收放。
    /// 最小宽度保证表头不被截断；多余空间按比例分给各列（而不是留一条大空白）。
    /// </summary>
    public void FitColumns()
    {
        if (Columns.Count == 0)
        {
            return;
        }

        int pad = Theme.Dip(this, Theme.SpaceMd) * 2;
        Font headerFont = Theme.FontFor(this, TypeRole.BodyStrong);

        var needed = new int[Columns.Count];
        for (int c = 0; c < Columns.Count; c++)
        {
            needed[c] = Ink.TextWidth(Columns[c].Text, headerFont) + pad;
            foreach (ListViewItem item in Items)
            {
                if (c < item.SubItems.Count)
                {
                    needed[c] = Math.Max(needed[c], Ink.TextWidth(item.SubItems[c].Text, Theme.FontFor(this, TypeRole.Body)) + pad);
                }
            }
        }

        // 第一列要额外让出勾选框的留白，否则文字会压到框上。
        if (_rowChecks)
        {
            needed[0] += CheckGutter;
        }

        int total = 0;
        foreach (int n in needed)
        {
            total += n;
        }

        // 列宽铺满整个客户区（不再留两侧 2*SpaceSm 的外缩）：ListView 表头只画「列覆盖到的
        // 那一段」，铺不满时右侧会露出表头自己的白底（2026-10-28 截图实测）。
        int available = Math.Max(0, ClientSize.Width);
        if (total > 0 && available > total)
        {
            // 有富余：按比例分配，避免右侧留一大块空白；除不尽的几像素给最后一列，
            // 否则表头右侧会留下一条系统底色的白缝（2026-10-28 截图实测）。
            int used = 0;
            for (int c = 0; c < needed.Length; c++)
            {
                int width = needed[c] + ((available - total) * needed[c] / total);
                Columns[c].Width = width;
                used += width;
            }

            Columns[needed.Length - 1].Width += Math.Max(0, available - used);
        }
        else
        {
            for (int c = 0; c < needed.Length; c++)
            {
                Columns[c].Width = needed[c];
            }
        }
    }

    /// <summary>清空并重建列（列标题由页面给，宽度这里统一量）。</summary>
    public void SetColumns(params (string Title, int MinWidthDip)[] columns)
    {
        Items.Clear();
        Columns.Clear();
        foreach ((string title, int _) in columns)
        {
            Columns.Add(title);
        }

        FitColumns();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FitColumns();
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        int dpi = Theme.DpiOf(this);
        using (var brush = Theme.Brush(Theme.Surface))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);

            // 末列的填充一直画到客户区右缘：列宽之和若因整除余数差几像素，
            // 右边会露出表头自己的白底。
            if (e.ColumnIndex == Columns.Count - 1 && e.Bounds.Right < ClientSize.Width)
            {
                e.Graphics.FillRectangle(
                    brush,
                    new Rectangle(e.Bounds.Right, e.Bounds.Y, ClientSize.Width - e.Bounds.Right, e.Bounds.Height));
            }
        }

        // 表头文字：小一号、暗一档，让内容成为视觉主体。
        var textBox = new Rectangle(
            e.Bounds.X + Theme.Dip(dpi, Theme.SpaceMd),
            e.Bounds.Y,
            Math.Max(0, e.Bounds.Width - (Theme.Dip(dpi, Theme.SpaceMd) * 2)),
            e.Bounds.Height);
        Ink.TextOneLine(
            e.Graphics, e.Header?.Text ?? string.Empty, Theme.FontFor(this, TypeRole.BodyStrong),
            Theme.TextDim, textBox, TextFormatFlags.Left);

        Ink.Line(
            e.Graphics, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1, Theme.Line);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        // 行底色：选中 → 低饱和点缀底 + 左侧点缀条；悬停 → 悬停底；其余 → 窗口底色。
        // 不再画斑马条纹：条纹让列表读起来像电子表格；分组靠留白与行分隔线。
        int dpi = Theme.DpiOf(this);
        bool isSelected = e.Item?.Selected == true;
        bool isHover = e.ItemIndex == _hoverIndex && !isSelected;
        Color back = isSelected
            ? Theme.AccentSoft
            : (isHover ? Theme.Surface : Theme.Bg);

        using (var brush = Theme.Brush(back))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }

        if (isSelected)
        {
            int bar = Theme.Dip(dpi, Theme.SpaceXs);
            using var accent = Theme.Brush(Theme.Accent);
            e.Graphics.FillRectangle(accent, new Rectangle(e.Bounds.X, e.Bounds.Y, bar, e.Bounds.Height));
        }

        // 行分隔线：低对比，只出现在行与行之间。
        Ink.Line(e.Graphics, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1, Theme.Line);
    }

    private int _hoverIndex = -1;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = GetItemAt(e.X, e.Y)?.Index ?? -1;
        if (index == _hoverIndex)
        {
            return;
        }

        _hoverIndex = index;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            Invalidate();
        }
    }

    /// <summary>
    /// 表头与滚动条由系统按浅色主题画：在深色母体里会跳出两块白。
    /// SetWindowTheme("DarkMode_Explorer") 让系统用深色那套视觉样式（Win10 1809+ 有效）。
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            SetWindowTheme(Handle, "DarkMode_Explorer", null);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // 老系统没有 uxtheme 的深色主题：保持系统外观，不影响功能。
        }
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        int dpi = Theme.DpiOf(this);
        int pad = Theme.Dip(dpi, Theme.SpaceMd);
        int left = e.Bounds.X + pad;

        Color color = e.Item?.Selected == true ? Theme.Text : Theme.TextDim;
        if (e.ColumnIndex == 0)
        {
            color = Theme.Text;
        }

        // 第一列：先画勾选框，再把文字右移一个留白。
        if (_rowChecks && e.ColumnIndex == 0 && e.Item is ListViewItem row)
        {
            bool isChecked = IsRowChecked?.Invoke(row) ?? false;
            bool isEnabled = IsRowCheckEnabled?.Invoke(row) ?? true;
            int size = Theme.Dip(dpi, Theme.SpaceLg);
            var box = new Rectangle(
                left,
                e.Bounds.Y + ((e.Bounds.Height - size) / 2),
                size,
                size);
            Ink.CheckBox(e.Graphics, box, isChecked, isEnabled, Theme.Dip(dpi, Theme.SpaceXs));
            left = box.Right + pad;

            if (!isEnabled)
            {
                color = Theme.TextDim;
            }
        }

        var textBox = new Rectangle(
            left,
            e.Bounds.Y,
            Math.Max(0, e.Bounds.Right - left - pad),
            e.Bounds.Height);

        Ink.TextOneLine(e.Graphics, e.SubItem?.Text ?? string.Empty, Theme.FontFor(this, TypeRole.Body), color, textBox, TextFormatFlags.Left);
    }

    /// <summary>点击落在第一列的勾选框留白里 = 一次勾选（点别处照旧选行）。</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_rowChecks || e.Button != MouseButtons.Left || e.X > CheckGutter + Theme.Dip(this, Theme.SpaceMd))
        {
            return;
        }

        ListViewItem? hit = GetItemAt(e.X, e.Y);
        if (hit is null || RowCheckToggled is null)
        {
            return;
        }

        if (IsRowCheckEnabled?.Invoke(hit) == false)
        {
            return;
        }

        RowCheckToggled(hit);
        Invalidate();
    }
}