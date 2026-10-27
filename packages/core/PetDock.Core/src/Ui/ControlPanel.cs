// 主控面板（原规格 18 §2 + RFC-12 §2.5）：单窗口、左导航右内容、四页。
// 面板只读展示（§2.4「面板只读展示，不改任何宠物/插件状态」），写操作只有三处：
//   ① 刷新快照与采样；② 插件页的「授权」「禁用/启用」两个入口（RFC-13 §2.6）。
// 后者只**发出意图**（把选中的 pluginId 交给回调），真正的判定与落账在宿主侧。
// 单实例由 ControlPanelHost 保证（RFC-12 §2.5「再次点托盘只激活已有窗口」）。
//
// 外观（原规格 18 §2.6，2026-10-27）：全部用 Ui/Design 的设计令牌与自绘控件，
// 不再出现默认 Button/ListView 的系统外观。四页不再用 TabControl 装——
// 它只是切页用的，却会带一圈原生边框和默认底色；换成一个 Panel 容器 + 每页一个 Panel，
// 切页就是改 Visible。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;

namespace PetDock.Core.Ui;

/// <summary>插件页操作回调（带被操作的 pluginId）。
/// D-03b：系统自带的同名泛型委托含黑名单子串，所以自声明一个，理由同 UiCommand。</summary>
internal delegate void PluginCommand(string pluginId);

/// <summary>三个页面的稳定 id（自检按 id 断言，不按标题文字）。
/// 2026-10-28 删掉「性能」页：那是给开发者看的逐帧采样台，普通用户没有动作可做，
/// 与「装宠物 / 管插件 / 改设置」三个真实任务无关；帧预算仍在设置页可调，采样命令仍在 --perftest。</summary>
internal static class PanelPages
{
    public const string Pets = "pets";
    public const string Plugins = "plugins";
    public const string Settings = "settings";

    public static readonly string[] All = { Pets, Plugins, Settings };

    public static string Title(string id) => id switch
    {
        Pets => I18n.Get("cp.tab.pets"),
        Plugins => I18n.Get("cp.tab.plugins"),
        Settings => I18n.Get("cp.tab.settings"),
        _ => id,
    };

    /// <summary>页头副标题：一句话说清这一页能做什么（原规格 18 §2.5 的一行说明位）。</summary>
    public static string Note(string id) => id switch
    {
        Pets => I18n.Get("cp.page.pets.hint"),
        Plugins => I18n.Get("cp.page.plugins.hint"),
        Settings => I18n.Get("cp.page.settings.hint"),
        _ => string.Empty,
    };
}

/// <summary>
/// 主控面板窗口。数据来源是一个取快照的委托（不持有宿主引用，便于自检在没有宿主时也能建树）。
/// 插件页的操作入口在另一个 partial 文件里（ControlPanel.Plugins.cs），避免单文件超行数上限。
/// </summary>
internal sealed partial class ControlPanel : Form
{
    private readonly Func<UiSnapshot> _snapshot;
    private readonly ThemedList _list;
    private readonly Label _status;
    private readonly Label _headerTitle;
    private readonly Label _headerSubtitle;
    private readonly NavRail _rail;
    private readonly Panel _content;
    private readonly Dictionary<string, Panel> _pageOf = new(StringComparer.Ordinal);

    /// <summary>插件页「授权」入口的回调（可空：没人接就静默）。</summary>
    private readonly PluginCommand? _authorize;

    /// <summary>插件页「禁用/启用」入口的回调（可空：没人接就静默）。</summary>
    private readonly PluginCommand? _toggleDisabled;

    /// <summary>插件页「安装」入口的回调（source, 是否安装后立即启用；返回 null = 成功，否则是失败原因）。</summary>
    private readonly Func<string, bool, string?>? _installPlugin;

    /// <summary>插件页「删除」入口的回调（pluginId；返回 null = 成功，否则是失败原因）。</summary>
    private readonly Func<string, string?>? _removePlugin;

    private string _current = PanelPages.Pets;

    public ControlPanel(
        Func<UiSnapshot> snapshot,
        bool showWindow,
        PluginCommand? authorize = null,
        PluginCommand? toggleDisabled = null,
        Func<string, bool, string?>? installPlugin = null,
        Func<string, string?>? removePlugin = null)
    {
        _snapshot = snapshot ?? (() => UiSnapshot.Empty);
        _authorize = authorize;
        _toggleDisabled = toggleDisabled;
        _installPlugin = installPlugin;
        _removePlugin = removePlugin;

        Text = I18n.Get("cp.title");
        StartPosition = FormStartPosition.CenterScreen;
        Theme.Apply(this);
        Width = Theme.Dip(this, 980);
        Height = Theme.Dip(this, 620);
        MinimumSize = new Size(Theme.Dip(this, 780), Theme.Dip(this, 480));
        ShowInTaskbar = true;

        // 左导航（§2.5：固定四项，顺序固定）
        _rail = new NavRail
        {
            Dock = DockStyle.Left,
            Width = Theme.Dip(this, 188),
            Name = "nav",
        };
        foreach (string id in PanelPages.All)
        {
            _rail.AddItem(id, PanelPages.Title(id));
        }

        _rail.Picked = ShowPage;

        // 页头：当前页标题 + 一句说明 + 刷新。面板只读展示，刷新是它唯一的写动作（§2.4）。
        // 标题与说明用显式定位（Dock 叠两个 Label 会互相盖住，且页头高度随 DPI 变）。
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Dip(this, 84),
            BackColor = Theme.Bg,
            Name = "panel-header",
            // 底部留 1px 给页头的分隔线：不留的话 Dock=Fill 的子容器会把它盖掉，
            // 只剩刷新按钮那一小段线露在外面（2026-10-28 截图实测）。
            Padding = new Padding(0, 0, Theme.Dip(this, Theme.SpaceXl), 1),
        };
        var titleHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Name = "panel-header-text",
        };
        _headerTitle = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(TypeRole.Title),
            ForeColor = Theme.Text,
            BackColor = Theme.Bg,
            Name = "page-title",
        };
        _headerSubtitle = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(TypeRole.Caption),
            ForeColor = Theme.TextDim,
            BackColor = Theme.Bg,
            Name = "page-subtitle",
        };
        titleHost.Controls.Add(_headerSubtitle);
        titleHost.Controls.Add(_headerTitle);
        titleHost.Resize += (_, _) => PlaceHeaderText(titleHost, _headerTitle, _headerSubtitle);

        // 页头没有「刷新」按钮（2026-10-28 重构）：它刷新的是同一份快照，点了等于没点。
        // 面板每次显示与每次数据变化都会自己重绑（BindCurrent），用户不需要按一个假按钮。
        header.Controls.Add(titleHost);

        PlaceHeaderText(titleHost, _headerTitle, _headerSubtitle);
        header.Paint += (_, e) => Ink.Line(
            e.Graphics, 0, header.Height - 1, header.Width, header.Height - 1, Theme.Line);

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Dip(this, 36),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(Theme.Dip(this, Theme.SpaceXl), 0, Theme.Dip(this, Theme.SpaceXl), 0),
            Font = Theme.Font(TypeRole.Caption),
            ForeColor = Theme.TextDim,
            BackColor = Theme.Bg,
            Text = I18n.Get("cp.status.notRefreshed"),
            Name = "panel-status",
        };
        _status.Paint += (_, e) => Ink.Line(e.Graphics, 0, 0, _status.Width, 0, Theme.Line);

        // 页容器：每页一个真正独立的 Panel（原实现用 TabControl，纯为切页却带一圈原生边框）
        _content = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Name = "pages",
        };
        foreach (string id in PanelPages.All)
        {
            Panel page = MakePage(id);
            _pageOf[id] = page;
            _content.Controls.Add(page);
        }

        _list = new ThemedList { Name = "page-list", Dock = DockStyle.Fill };

        // 插件页有自己的列表与操作栏（partial 的另一半建），所以它不进共享的 BindListOn。
        InitPluginPage();

        // 宠物页有自己的导入入口与内联错误条（另一个 partial 建）。
        InitPetsPage();

        // 设置页有自己的可写控件（开机自启 / 空闲降帧阈值 / 应用）。
        InitSettingsPage();

        // 设置页还有两个诊断入口（RFC-17 §3.4：打开日志目录 / 导出诊断包）。
        InitDiagnosticsPage();

        // 停靠顺序：先加 Fill 再加边，边先占位、Fill 吃剩下的空间。
        Controls.Add(_content);
        Controls.Add(_status);
        Controls.Add(header);
        Controls.Add(_rail);

        ShowPage(PanelPages.Pets);

        if (showWindow)
        {
            Show();
        }
        else
        {
            // 自检形态：创建控件树但不显示窗口（避免无人值守环境挂死，RFC-12 §3）。
            CreateControl();
            foreach (Panel page in _pageOf.Values)
            {
                page.CreateControl();
            }
        }
    }

    /// <summary>当前页 id（自检用）。</summary>
    public string CurrentPage => _current;

    /// <summary>按页面 id 取那一页（四页在构造期就全部存在）。</summary>
    internal Panel PageOf(string id) => _pageOf[id];

    /// <summary>切页（§2.5：右内容随导航切换，且所有四页都真实存在）。</summary>
    public void ShowPage(string id)
    {
        if (!_pageOf.ContainsKey(id))
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "unknown panel page");
        }

        _current = id;
        foreach (KeyValuePair<string, Panel> pair in _pageOf)
        {
            pair.Value.Visible = string.Equals(pair.Key, id, StringComparison.Ordinal);
        }

        _pageOf[id].BringToFront();
        _rail.Select(id);
        _headerTitle.Text = PanelPages.Title(id);
        _headerSubtitle.Text = PanelPages.Note(id);

        // 状态栏先清成中性的「就绪」：不清的话切到别的页会留着上一页的话
        // （2026-10-28 截图实测：性能页底部写着「尚无插件装载」）。各页 Bind 可以覆盖它。
        _status.Text = I18n.Get("cp.status.ready");
        RefreshCurrent();
    }

    /// <summary>按当前页重建列表。数据始终来自一次新的快照——面板不缓存旧值。</summary>
    public void RefreshCurrent()
    {
        UiSnapshot snapshot = _snapshot();
        switch (_current)
        {
            case PanelPages.Pets:
                BindPets(snapshot);
                break;
            case PanelPages.Plugins:
                BindPlugins(snapshot);
                break;
            case PanelPages.Settings:
                BindSettings(snapshot);
                break;
        }
    }

    private void BindPets(UiSnapshot snapshot)
    {
        _list.Columns.Clear();
        _list.Items.Clear();
        _list.Columns.Add(I18n.Get("cp.pets.col.name"));
        _list.Columns.Add(I18n.Get("cp.pets.col.source"));
        _list.Columns.Add(I18n.Get("cp.pets.col.state"));
        _list.Columns.Add(I18n.Get("cp.pets.col.memory"));

        foreach (PetLine line in snapshot.Pets)
        {
            _list.Items.Add(new ListViewItem(new[] { line.Name, line.Source, line.State, line.Memory }));
        }

        _list.FitColumns();
        BindListOn(PageOf(PanelPages.Pets));
        _status.Text = snapshot.Pets.Count == 0
            ? I18n.Get("cp.pets.empty")
            : I18n.Get("cp.pets.count", snapshot.Pets.Count);
    }

    private void BindSettings(UiSnapshot snapshot)
    {
        // 设置页没有只读摘要表了（2026-10-28 重构）：它和下面的可编辑控件说的是同一件事，
        // 两处并存就是「同一事实存两遍」，必然漂移。这里只把当前值灌进控件。
        BindSettingsValues(snapshot.Settings);
        _status.Text = I18n.Get("cp.settings.hint");
    }

    private void BindListOn(Panel page)
    {
        if (!page.Controls.Contains(_list))
        {
            page.Controls.Add(_list);

            // 停靠顺序：WinForms 从 Controls 集合的**末尾**往头排 Dock，
            // 所以最后被加进来的 Fill 控件会先吃掉整块区域，把 Dock=Bottom 的操作栏压在下面
            // （2026-10-28 截图实测：设置页的操作栏整条看不见）。把列表挪到集合头上，
            // 它就最后参与排版，只吃操作栏剩下的空间。
            page.Controls.SetChildIndex(_list, 0);
        }
    }

    /// <summary>四页是否齐全（自检断言，§2.5）。</summary>
    public IReadOnlyList<string> PageIds
    {
        get
        {
            var ids = new List<string>();
            foreach (string id in PanelPages.All)
            {
                if (_pageOf.ContainsKey(id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
    }
}
