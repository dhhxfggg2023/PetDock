// 页头两行文字的定位、页容器本身。页底动作栏与说明文字已收进 FormGrid（Bar/Hint），
// 这里只留页头与页容器——一页怎么摆只有一处知识。
using System;
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;

namespace PetDock.Core.Ui;

internal sealed partial class ControlPanel
{
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