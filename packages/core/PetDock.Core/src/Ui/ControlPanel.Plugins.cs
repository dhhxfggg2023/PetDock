// 插件页的操作入口（RFC-13 §2.6）。规格依据 docs/18 §2.2 插件页：
//   「授权按钮：打开 §3 的授权弹窗」「禁用开关：撤销事件订阅，不卸载」。
// 面板在这里只**发出意图**：把选中的 pluginId 交给回调——弹窗、逐项勾选与落账都在宿主侧
// （docs/18 §2.4「面板只读展示」在这里仍成立：面板自己不改任何插件状态）。
// 为什么「禁用」不是「卸载」：docs/18 §2.2 写的是「撤销事件订阅，不卸载」，对应宿主侧的
// PluginHost.Disable —— 能力闸门逐项吊销、租约置 Inactive、状态改 Suspended，沙箱与记录保留。
// 所以这一个按钮要同时表达两种意图：选中的是 Suspended 的插件时是「启用」，否则是「禁用」。
// 拆成 partial 只是为了守住单文件行数上限（D-03c），语义上仍是同一个面板。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class ControlPanel
{
    /// <summary>插件页自己的列表（与其它三页共用的那份分开：插件页要留住自己的选中项）。
    /// 单选：两个入口都是「对**选中插件**说话」，多选时对谁说话就成了猜。</summary>
    private readonly ListView _pluginList = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        GridLines = true,
        Name = "plugin-list",
    };

    /// <summary>「授权选中插件」入口。</summary>
    private readonly Button _authorizeButton = new()
    {
        Text = I18n.Get("cp.plugins.authorize"),
        Name = "authorize",
        Width = 130,
        Height = 28,
        Enabled = false,
    };

    /// <summary>「禁用/启用选中插件」入口（文案随选中插件的状态变）。</summary>
    private readonly Button _disableButton = new()
    {
        Text = I18n.Get("cp.plugins.disable"),
        Name = "disable",
        Width = 130,
        Height = 28,
        Enabled = false,
    };

    /// <summary>
    /// 页底那行说明（2026-10-26）：用户要求「插件可以选择关闭，重启生效」——
    /// 只说「重启生效」这四个字，用户才知道这个按钮做了什么、以及为什么桌面上那只还赖着不动。
    /// </summary>
    private readonly Label _hint = new()
    {
        Text = I18n.Get("cp.plugins.disableHint"),
        Name = "plugin-hint",
        AutoSize = true,
        Padding = new Padding(8, 8, 0, 0),
    };

    /// <summary>插件页那条说明的当前文案（自检断言它确实挂上了）。</summary>
    public string PluginHintText => _hint.Text;

    /// <summary>插件页的行数据，按插件 id 索引（每次重画从新快照重建，不缓存旧值）。</summary>
    private readonly Dictionary<string, PluginLine> _pluginRows = new(StringComparer.Ordinal);

    /// <summary>当前选中行的插件 id（没有选中行时为 null）。</summary>
    private string? _selectedPluginId;

    /// <summary>程序化改选中时抑制事件：否则原生选中变化会把面板自己记的值改回去。</summary>
    private bool _settingSelection;

    /// <summary>
    /// 插件页两个入口的 Name，按加入顺序（自检按它断言两个入口确实存在）。
    /// 名字里不能出现 D-03b 黑名单的子串，所以不用「操作按钮」那类直译名。
    /// </summary>
    public IReadOnlyList<string> PluginButtonNames =>
        new[] { _authorizeButton.Name, _disableButton.Name };

    /// <summary>同 <see cref="PluginButtonNames"/>（自检按哪个名字取都行）。</summary>
    public IReadOnlyList<string> EntryButtonNames => PluginButtonNames;

    /// <summary>当前选中行的插件 id（没有选中行时为 null）。</summary>
    public string? SelectedPluginId => _selectedPluginId;

    /// <summary>两个入口当前是否可用（没有选中行时为 false）。</summary>
    public bool PluginButtonsEnabled => _authorizeButton.Enabled && _disableButton.Enabled;

    /// <summary>授权入口的当前文案。</summary>
    public string AuthorizeButtonText => _authorizeButton.Text;

    /// <summary>禁用/启用入口的当前文案（随选中插件的状态变）。</summary>
    public string DisableButtonText => _disableButton.Text;

    /// <summary>
    /// 建插件页的控件树：列表 + 底部操作栏（两个按钮）。
    /// 操作栏放在插件页自己的 TabPage 里，不放在窗体级——否则会污染另外三页。
    /// </summary>
    private void InitPluginPage()
    {
        _pluginList.SelectedIndexChanged += (_, _) => OnPluginSelectionChanged();

        _authorizeButton.Click += (_, _) => AuthorizeSelected();
        _disableButton.Click += (_, _) => ToggleSelectedDisabled();

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8, 6, 8, 6),
            WrapContents = false,
        };
        bar.Controls.Add(_authorizeButton);
        bar.Controls.Add(_disableButton);
        bar.Controls.Add(_hint);

        TabPage page = PluginPage();
        // 加控件的顺序要紧：后加的先排版，所以操作栏（Bottom）后加，它才贴页底、
        // 列表（Fill）占剩下的空间，不会被盖住。
        page.Controls.Add(_pluginList);
        page.Controls.Add(bar);
    }

    /// <summary>按名字找插件页（不靠下标——性能页会插进来把下标挪走）。</summary>
    private TabPage PluginPage()
    {
        foreach (TabPage page in _pages.TabPages)
        {
            if (string.Equals(page.Name, "tab-" + PanelPages.Plugins, StringComparison.Ordinal))
            {
                return page;
            }
        }

        throw new InvalidOperationException("plugin page missing");
    }

    /// <summary>插件页：填表 + 状态栏文案（状态栏要说清两个按钮各自做什么）。</summary>
    private void BindPlugins(UiSnapshot snapshot)
    {
        BindPluginRows(snapshot);
        _status.Text = snapshot.Plugins.Count == 0
            ? I18n.Get("cp.plugins.empty")
            : I18n.Get("cp.plugins.count", snapshot.Plugins.Count);
    }

    /// <summary>填插件页的表。列就是规格里的五列，操作交给两个按钮，不加第六列。</summary>
    private void BindPluginRows(UiSnapshot snapshot)
    {
        string? previous = _selectedPluginId;

        _pluginRows.Clear();
        _pluginList.Items.Clear();
        _pluginList.Columns.Clear();
        _pluginList.Columns.Add(I18n.Get("cp.plugins.col.name"), 180);
        _pluginList.Columns.Add(I18n.Get("cp.plugins.col.version"), 90);
        _pluginList.Columns.Add("Tier", 60);
        _pluginList.Columns.Add(I18n.Get("cp.plugins.col.granted"), 110);
        _pluginList.Columns.Add(I18n.Get("cp.plugins.col.state"), 120);

        foreach (PluginLine line in snapshot.Plugins)
        {
            _pluginRows[line.Id] = line;
            _pluginList.Items.Add(new ListViewItem(new[]
            {
                line.Name,
                line.Version,
                line.Tier.ToString(CultureInfo.InvariantCulture),
                line.Granted.ToString(CultureInfo.InvariantCulture),
                line.State,
            })
            {
                Tag = line.Id,
            });
        }

        // 重画后尽量保住原来的选中项：刷新不该把用户刚选中的插件弄丢。
        _selectedPluginId = null;
        if (previous is not null && _pluginRows.ContainsKey(previous))
        {
            SelectPlugin(previous);
        }
        else
        {
            UpdatePluginButtons();
        }
    }

    /// <summary>
    /// 按插件 id 选中对应行（自检用：无人值守环境里没法用鼠标点行）。
    /// 找不到该 id 返回 false；找到则选中该行，并让两个入口变为可用。
    /// </summary>
    public bool SelectPlugin(string id)
    {
        if (_pluginList.Items.Count == 0)
        {
            // 还没进过插件页：先按一次新快照把表填上，再谈选中。
            BindPluginRows(_snapshot());
        }

        ListViewItem? hit = null;
        foreach (ListViewItem item in _pluginList.Items)
        {
            if (string.Equals(item.Tag as string, id, StringComparison.Ordinal))
            {
                hit = item;
                break;
            }
        }

        if (hit is null)
        {
            return false;
        }

        _settingSelection = true;
        foreach (ListViewItem item in _pluginList.Items)
        {
            SetNativeSelection(item, ReferenceEquals(item, hit));
        }

        _settingSelection = false;
        _selectedPluginId = id;
        UpdatePluginButtons();
        return true;
    }

    /// <summary>
    /// 对选中行发起授权复核（docs/18 §3 的弹窗由调用方打开）。
    /// 没有选中行时静默返回——不猜一个插件来授权。
    /// </summary>
    public void AuthorizeSelected()
    {
        string? id = SelectedPluginId;
        if (id is null)
        {
            return;
        }

        _authorize?.Invoke(id);
    }

    /// <summary>对选中行发出「禁用/启用」意图（宿主侧落成 Disable / Resume）。</summary>
    public void ToggleSelectedDisabled()
    {
        string? id = SelectedPluginId;
        if (id is null)
        {
            return;
        }

        _toggleDisabled?.Invoke(id);
    }

    /// <summary>两个入口的可用性与文案：有选中行才可用；禁用按钮的文案随状态变。</summary>
    private void UpdatePluginButtons()
    {
        string? id = _selectedPluginId;
        bool has = false;
        bool suspended = false;
        if (id is not null && _pluginRows.TryGetValue(id, out PluginLine? line) && line is not null)
        {
            has = true;
            suspended = string.Equals(
                line.State,
                PluginLifecycleState.Suspended.ToString(),
                StringComparison.Ordinal);
        }

        _authorizeButton.Enabled = has;
        _disableButton.Enabled = has;
        _disableButton.Text = suspended ? I18n.Get("cp.plugins.enable") : I18n.Get("cp.plugins.disable");
    }

    /// <summary>用户改选中时同步面板自己记的选中 id。</summary>
    private void OnPluginSelectionChanged()
    {
        if (_settingSelection)
        {
            return;
        }

        string? id = null;
        if (_pluginList.IsHandleCreated)
        {
            // 逐项读 Selected 标志，而不是读 SelectedItems 集合——集合在事件里可能还没更新。
            foreach (ListViewItem item in _pluginList.Items)
            {
                if (item.Selected)
                {
                    id = item.Tag as string;
                    break;
                }
            }
        }

        _selectedPluginId = id;
        UpdatePluginButtons();
    }

    /// <summary>
    /// 改原生选中状态。没建句柄时（自检形态只 CreateControl）改不了，
    /// 那就跳过——面板自己记的选中 id 才是判据依据，不依赖原生状态。
    /// </summary>
    private void SetNativeSelection(ListViewItem item, bool selected)
    {
        if (!_pluginList.IsHandleCreated)
        {
            return;
        }

        try
        {
            item.Selected = selected;
        }
        catch (InvalidOperationException)
        {
            // 控件正在销毁：原生选中状态落不下去，面板自己记的值仍然有效。
        }
    }
}
