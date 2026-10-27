// 页面里的两条常用积木（2026-10-28 重构第二批）：底部动作栏与行内说明。
//
// 之前这两样写在 ControlPanel.Layout.cs 的私有方法里，只有设置页与插件页在用，
// 而性能页/宠物页又各自写了一遍 padding 与字号。收进 FormGrid 之后，
// 「一页怎么摆」只有这一处知识：表单是 FormGrid.Row，动作栏是 FormGrid.Bar。
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui.Design;

internal static partial class FormGrid
{
    /// <summary>页面底部的动作栏：Dock=Bottom，按钮按自身尺寸从右往左排。</summary>
    public static FlowLayoutPanel Bar(Control owner, int heightDip = 56)
    {
        int dpi = Theme.DpiOf(owner);
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Dip(dpi, heightDip),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Bg,
            Padding = new Padding(0, Theme.Dip(dpi, Theme.SpaceMd), Theme.Dip(dpi, Theme.SpaceXl), 0),
        };
    }

    /// <summary>行内说明文字（Caption 级，灰色，不占主视线）。</summary>
    public static Label Hint(Control owner, string text, TypeRole role = TypeRole.Caption)
    {
        int dpi = Theme.DpiOf(owner);
        return new Label
        {
            AutoSize = true,
            Text = text,
            Font = Theme.FontFor(owner, role),
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, Theme.Dip(dpi, Theme.SpaceMd), 0),
        };
    }
}