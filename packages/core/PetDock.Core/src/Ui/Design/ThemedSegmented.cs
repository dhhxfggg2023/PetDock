// 分段选择器（原规格 18 §2.7 的材质三选一）：一个圆角容器里并排 N 段，选中那段有底色。
//
// 为什么不直接摆三个按钮：材质是「互斥的一档」，摆三个按钮会读成三个独立动作；
// 分段器把「只能选一个」画进形状里。也不用原生 ComboBox —— 它在这个深色母体里
// 是系统白底，且 ui-appearance-control-kinds 明令禁止界面里出现原生下拉。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

/// <summary>选中项变了（键 + 下标一起给，D-03b：自声明委托，不用系统泛型委托）。</summary>
internal delegate void SegmentChosen(string key, int index);

/// <summary>互斥选项的分段选择器。键与文案分离：键落盘，文案给人看。</summary>
internal sealed class ThemedSegmented : Control
{
    private readonly List<(string Key, string Text)> _options = new();
    private int _selected;
    private int _hover = -1;

    /// <summary>构造：自绘、双缓冲、不吃系统主题。</summary>
    public ThemedSegmented()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        TabStop = true;
        DoubleBuffered = true;
        Height = RowHeight();
    }

    /// <summary>选中项变了（键 + 下标一起给，调用方不必自己再读）。</summary>
    public event SegmentChosen? SelectedChanged;

    /// <summary>已选中项的键（空表时是空串）。</summary>
    public string SelectedKey => _options.Count == 0 ? string.Empty : _options[Math.Clamp(_selected, 0, _options.Count - 1)].Key;

    /// <summary>已选中项的下标（空表时是 -1）。</summary>
    public int SelectedIndex => _options.Count == 0 ? -1 : _selected;

    /// <summary>设置选项（键, 文案），保持原选中项不变、不触发事件。</summary>
    public void SetOptions(params (string Key, string Text)[] options)
    {
        _options.Clear();
        _options.AddRange(options);
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _options.Count - 1));
        Height = RowHeight();
        Invalidate();
    }

    /// <summary>按改动来源是否可信来决定要不要发事件（程序设置初始值时不发，用户点时才发）。</summary>
    public void Select(string key, bool raise)
    {
        int index = _options.FindIndex(option => string.Equals(option.Key, key, StringComparison.Ordinal));
        if (index < 0 || index == _selected)
        {
            return;
        }

        _selected = index;
        Invalidate();
        if (raise && _options.Count > 0)
        {
            SelectedChanged?.Invoke(_options[index].Key, index);
        }
    }

    /// <summary>行高：字体行高 + 上下内边距（自绘控件不自动撑高）。</summary>
    private int RowHeight() => Theme.FontFor(this, TypeRole.Body).Height + (Theme.Dip(this, Theme.SpaceSm) * 2);

    /// <summary>每一段的外框：等宽，最后一列吃掉整除余数（否则右侧会差一两个像素）。</summary>
    private Rectangle SegmentBox(int index, int width, int height)
    {
        if (_options.Count == 0)
        {
            return new Rectangle(0, 0, width, height);
        }

        int part = width / _options.Count;
        int left = index * part;
        return index == _options.Count - 1
            ? new Rectangle(left, 0, width - left, height)
            : new Rectangle(left, 0, part, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        int dpi = Theme.DpiOf(this);
        int pad = Theme.Dip(dpi, Theme.SpaceXs);
        int radius = Theme.Dip(dpi, Theme.RadiusSm);

        // 外框：深色底 + 一圈细描边，读起来是「一个控件」而不是三个。
        Ink.Fill(g, new Rectangle(0, 0, Width - 1, Height - 1), radius + 2, Theme.Surface);
        Ink.Stroke(g, new Rectangle(0, 0, Width - 1, Height - 1), radius + 2, Theme.Line);

        for (int i = 0; i < _options.Count; i++)
        {
            Rectangle box = SegmentBox(i, Width - 1, Height - 1);
            box.Inflate(-pad, -pad * 2);
            if (i == _selected)
            {
                Ink.Fill(g, box, radius, Theme.AccentSoft);
                Ink.Stroke(g, box, radius, Theme.Accent);
            }
            else if (i == _hover)
            {
                Ink.Fill(g, box, radius, Theme.SurfaceHover);
            }

            Ink.TextOneLine(
                g,
                _options[i].Text,
                Theme.FontFor(this, TypeRole.Body),
                i == _selected ? Theme.Text : Theme.TextDim,
                box,
                TextFormatFlags.HorizontalCenter);
        }

        if (Focused)
        {
            Ink.Stroke(g, new Rectangle(0, 0, Width - 1, Height - 1), radius + 2, Theme.Accent, 1.5f);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = IndexAt(e.X);
        if (index != _hover)
        {
            _hover = index;
            Invalidate();
        }

        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1)
        {
            _hover = -1;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Choose(IndexAt(e.X));
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left:
                Choose(_selected - 1);
                e.Handled = true;
                break;
            case Keys.Right:
                Choose(_selected + 1);
                e.Handled = true;
                break;
            case Keys.Home:
                Choose(0);
                e.Handled = true;
                break;
            case Keys.End:
                Choose(_options.Count - 1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>键盘在控件间切换（Tab 进来要能看见焦点）。</summary>
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    private void Choose(int index)
    {
        if (index < 0 || index >= _options.Count || index == _selected)
        {
            return;
        }

        _selected = index;
        Invalidate();
        SelectedChanged?.Invoke(_options[index].Key, index);
    }

    private int IndexAt(int x)
    {
        if (_options.Count == 0 || Width <= 0)
        {
            return -1;
        }

        int part = Width / _options.Count;
        if (part <= 0)
        {
            return -1;
        }

        int index = Math.Clamp(x / part, 0, _options.Count - 1);
        return x < 0 || x > Width ? -1 : index;
    }
}