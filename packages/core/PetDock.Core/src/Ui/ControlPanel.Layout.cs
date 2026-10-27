// 主控面板的排版积木（2026-10-28 从 ControlPanel.cs 拆出）：页底操作栏、说明文字、
// 页头两行文字的定位、页容器本身。都是纯布局，不含业务逻辑。
//
// 为什么要拆：ControlPanel.cs 逼近 D-03c 的单文件 400 行上限，而页头副标题与材质选择器
// 让这一片又长了一截；拆成 partial 比继续挤在同一个文件里更容易读。
using System;
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;

namespace PetDock.Core.Ui;

internal sealed partial class ControlPanel
{
    /// <summary>
    /// 页底操作栏：<see cref="FlowLayoutPanel"/> 默认是系统灰底、不留内边距，直接放进深色页面
    /// 就是一块突兀的浅色横条。这里统一给底色、内边距与高度（都按页面当前 DPI 取）。
    /// </summary>
    private static FlowLayoutPanel ThemedBar(Control owner)
    {
        int dpi = Theme.DpiOf(owner);
        int padX = Theme.Dip(dpi, Theme.SpaceMd);
        int padY = Theme.Dip(dpi, Theme.SpaceSm);
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Dip(dpi, 52),
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(padX, padY, padX, padY),
            WrapContents = false,
            BackColor = Theme.Bg,
        };
    }

    /// <summary>说明文字：自动尺寸、次要色、透明底（Label 默认用系统色，深底上读不清）。</summary>
    private static Label ThemedHint(Control owner, string text, TypeRole role = TypeRole.Caption)
    {
        int dpi = Theme.DpiOf(owner);
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            Margin = new Padding(Theme.Dip(dpi, Theme.SpaceSm), Theme.Dip(dpi, Theme.SpaceSm), 0, 0),
        };
        label.Font = Theme.FontFor(owner, role);
        return label;
    }

    /// <summary>页头两行文字：标题在上、一句说明在下（显式排版，按字体高度累加）。</summary>
    private static void PlaceHeaderText(Control host, Label title, Label subtitle)
    {
        int dpi = Theme.DpiOf(host);
        int left = Theme.Dip(dpi, Theme.SpaceXl);
        int top = Theme.Dip(dpi, Theme.SpaceLg);
        int width = Math.Max(0, host.Width - (left * 2));
        title.Bounds = new Rectangle(left, top, width, title.Font.Height + Theme.Dip(dpi, Theme.SpaceXs));
        subtitle.Bounds = new Rectangle(
            left, title.Bounds.Bottom, width, subtitle.Font.Height + Theme.Dip(dpi, Theme.SpaceXs));
    }

    /// <summary>一页的容器：统一内边距，页与页互不叠放（只有当前页 Visible）。</summary>
    private Panel MakePage(string id) => new()
    {
        Name = "tab-" + id,
        Dock = DockStyle.Fill,
        BackColor = Theme.Bg,
        Padding = new Padding(
            Theme.Dip(this, Theme.SpaceXl),
            Theme.Dip(this, Theme.SpaceMd),
            Theme.Dip(this, Theme.SpaceXl),
            Theme.Dip(this, Theme.SpaceSm)),
        Visible = false,
    };
}