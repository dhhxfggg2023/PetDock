// 左导航栏（原规格 18 §2.5「左导航右内容」）：自绘的竖向导航，替代默认 Button 列。
//
// 为什么自绘：默认 Button 在深色底上是浅灰方块、直角、带系统边框，选中态只能靠 Font.Bold
// 表达（原实现就是这样），在视觉上分不出「当前在哪一页」。这里用「左侧点缀条 + 底色 + 字重」
// 三种信号同时表达选中态，且尺寸全部按字体高度量出来，150% 缩放下不会把标题挤掉。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

/// <summary>导航项被点中时的回调。D-03b：自声明委托，不用系统泛型委托。</summary>
internal delegate void NavSelected(string id);

/// <summary>左侧导航栏。项按加入顺序竖排，只有一项处于选中态。</summary>
internal sealed class NavRail : Panel
{
    private readonly List<(string Id, string Title, Rectangle Box)> _items = new();
    private readonly Dictionary<string, string> _titleOf = new(StringComparer.Ordinal);
    private string? _selected;
    private string? _hover;

    public NavRail()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.BgDeep;
        TabStop = false;
    }

    /// <summary>选中的页 id（无选中时为 null）。</summary>
    public string? Selected => _selected;

    /// <summary>用户点中某项。宿主接它来切页；自检也可以直接调 <see cref="Select"/>。</summary>
    public NavSelected? Picked { get; set; }

    /// <summary>加一项。重复 id 会覆盖标题而不新增（判据按 id 断言，不看顺序外的文字）。</summary>
    public void AddItem(string id, string title)
    {
        if (_titleOf.ContainsKey(id))
        {
            _titleOf[id] = title;
            LayoutItems();
            return;
        }

        _titleOf[id] = title;
        _items.Add((id, title, Rectangle.Empty));
        LayoutItems();
    }

    /// <summary>选中某项（不触发 <see cref="Picked"/>，避免宿主自己点自己时递归切页）。</summary>
    public void Select(string id)
    {
        if (!_titleOf.ContainsKey(id) || string.Equals(_selected, id, StringComparison.Ordinal))
        {
            _selected = id;
            Invalidate();
            return;
        }

        _selected = id;
        Invalidate();
    }

    /// <summary>项的当前矩形（自检与真机探针用来算点击位置）。</summary>
    public Rectangle BoxOf(string id)
    {
        foreach ((string itemId, _, Rectangle box) in _items)
        {
            if (string.Equals(itemId, id, StringComparison.Ordinal))
            {
                return box;
            }
        }

        return Rectangle.Empty;
    }

    /// <summary>项的数量（自检断言导航与页一一对应）。</summary>
    public int ItemCount => _items.Count;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutItems();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string? hit = HitTest(e.Location)?.Id;
        if (!string.Equals(hit, _hover, StringComparison.Ordinal))
        {
            _hover = hit;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is not null)
        {
            _hover = null;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        string? id = HitTest(e.Location)?.Id;
        if (id is null)
        {
            return;
        }

        Select(id);
        Picked?.Invoke(id);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Theme.Paint(Theme.BgDeep));

        int dpi = Theme.DpiOf(this);
        int padX = Theme.Dip(dpi, Theme.SpaceSm);
        int textPad = Theme.Dip(dpi, Theme.SpaceMd);
        int dotSize = Theme.Dip(dpi, 6);
        int radius = Theme.Dip(dpi, Theme.RadiusSm);

        // 品牌行：让导航栏上沿不是一片空，也把「这是左侧栏」的层级说清楚。
        var brandBox = new Rectangle(
            padX + textPad,
            0,
            Math.Max(0, Width - (padX + textPad) * 2),
            BrandHeight());
        Ink.TextOneLine(
            g, Brand, Theme.FontFor(this, TypeRole.BodyStrong), Theme.TextDim, brandBox, TextFormatFlags.Left);

        foreach ((string id, string title, Rectangle box) in _items)
        {
            if (box.IsEmpty)
            {
                continue;
            }

            bool isSelected = string.Equals(id, _selected, StringComparison.Ordinal);
            bool isHover = string.Equals(id, _hover, StringComparison.Ordinal);

            if (isSelected)
            {
                // 选中 = 一条低饱和点缀底 + 左侧一个小圆点；不再用整条竖线（那读起来像标签页）。
                Ink.Fill(g, box, radius, Theme.AccentSoft);
                int cy = box.Y + (box.Height / 2);
                Ink.Fill(
                    g,
                    new Rectangle(box.X + textPad - (dotSize / 2), cy - (dotSize / 2), dotSize, dotSize),
                    dotSize / 2,
                    Theme.Accent);
            }
            else if (isHover)
            {
                Ink.Fill(g, box, radius, Theme.Surface);
            }

            TypeRole role = isSelected ? TypeRole.BodyStrong : TypeRole.Body;
            Color color = isSelected ? Theme.Text : Theme.TextDim;
            var textBox = new Rectangle(
                box.X + textPad + dotSize, box.Y, Math.Max(0, box.Width - textPad * 2 - dotSize), box.Height);
            Ink.TextOneLine(g, title, Theme.FontFor(this, role), color, textBox, TextFormatFlags.Left);
        }

        // 右缘一条细线：内容区与导航栏同色系，没有它两块会连成一片。
        Ink.Line(g, Width - 1, 0, Width - 1, Height, Theme.Line);
    }

    /// <summary>导航栏品牌行文字。</summary>
    private const string Brand = "PETDOCK";

    /// <summary>品牌行高度（逻辑像素，随 DPI 放大）。</summary>
    private int BrandHeight() => Theme.Dip(this, Theme.SpaceXxl + Theme.SpaceLg);

    private void LayoutItems()
    {
        if (_items.Count == 0)
        {
            return;
        }

        int dpi = Theme.DpiOf(this);
        int gap = Theme.Dip(dpi, Theme.SpaceXs);
        int itemHeight = Math.Max(
            Theme.Dip(dpi, 40),
            Ink.WrapHeight("Ag", Theme.FontFor(this, TypeRole.Body), Math.Max(1, Width)) + Theme.Dip(dpi, Theme.SpaceLg) * 2);
        int padX = Theme.Dip(dpi, Theme.SpaceSm);
        int y = BrandHeight() + Theme.Dip(dpi, Theme.SpaceSm);

        for (int i = 0; i < _items.Count; i++)
        {
            (string id, string title, _) = _items[i];
            var box = new Rectangle(padX, y, Math.Max(0, Width - padX * 2), itemHeight);
            _items[i] = (id, title, box);
            y += itemHeight + gap;
        }

        Invalidate();
    }

    private (string Id, string Title, Rectangle Box)? HitTest(Point p)
    {
        foreach ((string id, string title, Rectangle box) in _items)
        {
            if (box.Contains(p))
            {
                return (id, title, box);
            }
        }

        return null;
    }
}
