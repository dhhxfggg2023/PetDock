// 表单页的排版母体（2026-10-28 重构第一批）。
//
// 为什么要有它：在此之前四个页面各写各的 Dock + Padding + 字号，结果是
// 「每页看起来都用了同一套颜色，但没有一页是按同一个网格排的」——设置页尤其明显
// （勾选框、说明、数字框、按钮挤在同一条底栏里，材质另起一条底栏，状态另占一行）。
// 这里把「一页 = 若干段 + 每段若干行（标签列 + 控件列）」定成唯一形状，
// 页面只声明内容，不再算坐标。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

/// <summary>一段的标题 + 行列表；行内左列是标签，右列是控件。</summary>
internal static class FormGrid
{
    /// <summary>标签列宽（逻辑像素）：一屏四个字以内都能放下，超过就换行到控件上方。</summary>
    public const int LabelColumnDip = 132;

    /// <summary>一行的最小高度（逻辑像素），保证控件与标签都垂直居中。</summary>
    public const int RowHeightDip = 44;

    /// <summary>建一个纵向滚动的表单容器（Dock=Fill）。</summary>
    public static Panel Stack(Control owner)
    {
        int dpi = Theme.DpiOf(owner);
        return new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.Bg,
            Padding = new Padding(
                Theme.Dip(dpi, Theme.SpaceXl),
                0,
                Theme.Dip(dpi, Theme.SpaceXl),
                0),
            Name = "form-stack",
        };
    }

    /// <summary>段标题（全大写感的分组名，不是第二个页面标题）。</summary>
    public static Label Section(Control owner, string text)
    {
        int dpi = Theme.DpiOf(owner);
        var label = new Label
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = Theme.Dip(dpi, 26),
            Font = Theme.FontFor(owner, TypeRole.Caption),
            ForeColor = Theme.TextDim,
            BackColor = Theme.Bg,
            Padding = new Padding(0, Theme.Dip(dpi, Theme.SpaceSm), 0, 0),
        };
        return label;
    }

    /// <summary>一行：左标签 + 右控件（控件自适应宽度）。hint 为空则不占说明位。</summary>
    public static Panel Row(Control owner, string label, Control control, string? hint = null)
    {
        int dpi = Theme.DpiOf(owner);
        int labelWidth = Theme.Dip(dpi, LabelColumnDip);
        int captionHeight = Theme.Dip(dpi, 22);
        int controlHeight = Math.Max(Theme.Dip(dpi, 34), control.Height);
        int hintHeight = hint is null ? 0 : Theme.Dip(dpi, 14);

        var row = new Panel
        {
            Dock = DockStyle.Top,
            Height = controlHeight + hintHeight,
            BackColor = Theme.Bg,
        };

        // 显式坐标而不是 Dock：Dock=Left 的排布顺序取决于控件加入顺序与 z-order，
        // 2026-10-28 实测过一次「标签跑到控件右边」——表单这类固定网格不该靠这种隐式规则。
        var caption = new Label
        {
            Text = label,
            AutoSize = false,
            Bounds = new Rectangle(0, Math.Max(0, (controlHeight - captionHeight) / 2), labelWidth, captionHeight),
            Font = Theme.FontFor(owner, TypeRole.Body),
            ForeColor = Theme.Text,
            BackColor = Theme.Bg,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        row.Controls.Add(caption);

        control.Dock = DockStyle.None;
        control.Location = new Point(labelWidth, Math.Max(0, (controlHeight - control.Height) / 2));
        control.Margin = Padding.Empty;
        row.Controls.Add(control);

        if (hint is not null)
        {
            var note = new Label
            {
                Text = hint,
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Bottom,
                Height = hintHeight,
                Font = Theme.FontFor(owner, TypeRole.Caption),
                ForeColor = Theme.TextMuted,
                BackColor = Theme.Bg,
                Padding = new Padding(labelWidth, 0, 0, 0),
            };
            row.Controls.Add(note);
        }

        return row;
    }

    /// <summary>
    /// 段与段之间留白：用一个空面板吃掉固定高度，而不是让调用方去算 Margin
    /// （WinForms 的 Margin 在 Dock 布局里经常不生效，这一点踩过不止一次）。
    /// </summary>
    public static Panel Gap(Control owner, int dip = Theme.SpaceMd)
    {
        int dpi = Theme.DpiOf(owner);
        return new Panel { Dock = DockStyle.Top, Height = Theme.Dip(dpi, dip), BackColor = Theme.Bg };
    }

    /// <summary>把若干控件自下而上装进容器（Dock=Top 的堆叠顺序）。</summary>
    public static void Fill(Panel stack, IEnumerable<Control> children)
    {
        foreach (Control child in children)
        {
            stack.Controls.Add(child);
            stack.Controls.SetChildIndex(child, 0);
        }
    }
}