// 主控面板（docs/18 §2 + RFC-12 §2.5）：单窗口、左导航右内容、四页。
// 面板只读展示（§2.4「面板只读展示，不改任何宠物/插件状态」），写操作只有三处：
//   ① 刷新快照与采样；② 插件页的「授权」「禁用/启用」两个入口（RFC-13 §2.6）。
// 后者只**发出意图**（把选中的 pluginId 交给回调），真正的判定与落账在宿主侧。
// 单实例由 ControlPanelHost 保证（RFC-12 §2.5「再次点托盘只激活已有窗口」）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui;

/// <summary>插件页操作回调（带被操作的 pluginId）。
/// D-03b：系统自带的同名泛型委托含黑名单子串，所以自声明一个，理由同 UiCommand。</summary>
internal delegate void PluginCommand(string pluginId);

/// <summary>四个页面的稳定 id（自检按 id 断言，不按标题文字）。</summary>
internal static class PanelPages
{
    public const string Pets = "pets";
    public const string Plugins = "plugins";
    public const string Perf = "perf";
    public const string Settings = "settings";

    public static readonly string[] All = { Pets, Plugins, Perf, Settings };

    public static string Title(string id) => id switch
    {
        Pets => "宠物",
        Plugins => "插件",
        Perf => "性能",
        Settings => "设置",
        _ => id,
    };
}

/// <summary>
/// 主控面板窗口。数据来源是一个取快照的委托（不持有宿主引用，便于自检在没有宿主时也能建树）。
/// 插件页的操作入口在另一个 partial 文件里（ControlPanel.Plugins.cs），避免单文件超行数上限。
/// </summary>
internal sealed partial class ControlPanel : Form
{
    private readonly Func<UiSnapshot> _snapshot;
    private readonly ListView _list;
    private readonly Label _status;
    private readonly Dictionary<string, Button> _nav = new(StringComparer.Ordinal);
    private readonly TabControl _pages;
    private readonly PerfPage _perfPage;

    /// <summary>插件页「授权」入口的回调（可空：没人接就静默）。</summary>
    private readonly PluginCommand? _authorize;

    /// <summary>插件页「禁用/启用」入口的回调（可空：没人接就静默）。</summary>
    private readonly PluginCommand? _toggleDisabled;

    private string _current = PanelPages.Pets;

    public ControlPanel(
        Func<UiSnapshot> snapshot,
        bool showWindow,
        PluginCommand? authorize = null,
        PluginCommand? toggleDisabled = null)
    {
        _snapshot = snapshot ?? (() => UiSnapshot.Empty);
        _authorize = authorize;
        _toggleDisabled = toggleDisabled;

        Text = "PetDock — 主控面板";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 900;
        Height = 560;
        MinimumSize = new Size(680, 420);
        Font = SystemFonts.MessageBoxFont;
        ShowInTaskbar = true;

        // 左导航（§2.5：固定四项，顺序固定）
        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 132,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(8),
            WrapContents = false,
            BackColor = SystemColors.Control,
        };
        foreach (string id in PanelPages.All)
        {
            var button = new Button
            {
                Text = PanelPages.Title(id),
                Width = 112,
                Height = 34,
                Name = id,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            string captured = id;
            button.Click += (_, _) => ShowPage(captured);
            _nav[id] = button;
            nav.Controls.Add(button);
        }

        // 右内容：每页一个 TabPage（不显示标签页头，导航由左栏驱动）
        _pages = new TabControl
        {
            Dock = DockStyle.Fill,
            Appearance = TabAppearance.FlatButtons,
            ItemSize = new Size(0, 1),
            SizeMode = TabSizeMode.Fixed,
        };
        _pages.TabPages.Add(MakePage(PanelPages.Pets));
        _pages.TabPages.Add(MakePage(PanelPages.Plugins));
        _pages.TabPages.Add(MakePage(PanelPages.Settings));

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Name = "page-list",
        };

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 0),
            Text = "(尚未刷新)",
        };

        _perfPage = new PerfPage(_snapshot);

        // 插件页有自己的列表与操作栏（partial 的另一半建），所以它不进共享的 BindListOn。
        InitPluginPage();

        // 宠物页有自己的导入入口与内联错误条（另一个 partial 建）。
        InitPetsPage();

        // 性能页是独立控件页（有采样按钮），单独挂一个 TabPage
        var perfTab = MakePage(PanelPages.Perf);
        _perfPage.Dock = DockStyle.Fill;
        perfTab.Controls.Add(_perfPage);
        _pages.TabPages.Insert(2, perfTab);

        var refresh = new Button
        {
            Text = "刷新",
            Dock = DockStyle.Top,
            Height = 30,
            Name = "refresh",
        };
        refresh.Click += (_, _) => RefreshCurrent();

        Controls.Add(_pages);
        Controls.Add(_status);
        Controls.Add(nav);

        ShowPage(PanelPages.Pets);

        if (showWindow)
        {
            Show();
        }
        else
        {
            // 自检形态：创建控件树但不显示窗口（避免无人值守环境挂死，RFC-12 §3）。
            CreateControl();
            foreach (TabPage page in _pages.TabPages)
            {
                page.CreateControl();
            }
        }
    }

    /// <summary>当前页 id（自检用）。</summary>
    public string CurrentPage => _current;

    private static TabPage MakePage(string id) => new(id) { Name = "tab-" + id, Padding = new Padding(4) };

    /// <summary>切页（§2.5：右内容随导航切换，且所有四页都真实存在）。</summary>
    public void ShowPage(string id)
    {
        int index = Array.IndexOf(PanelPages.All, id);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "unknown panel page");
        }

        _current = id;
        if (index < _pages.TabPages.Count)
        {
            _pages.SelectedIndex = index;
        }

        foreach (KeyValuePair<string, Button> pair in _nav)
        {
            Font baseFont = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
            pair.Value.Font = string.Equals(pair.Key, id, StringComparison.Ordinal)
                ? new Font(baseFont, FontStyle.Bold)
                : baseFont;
        }

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
            case PanelPages.Perf:
                _perfPage.Bind(snapshot.Perf);
                break;
            case PanelPages.Settings:
                BindSettings(snapshot);
                break;
        }
    }

    private void BindPets(UiSnapshot snapshot)
    {
        _list.Items.Clear();
        _list.Columns.Clear();
        _list.Columns.Add("宠物名", 180);
        _list.Columns.Add("来源生态", 160);
        _list.Columns.Add("状态", 120);
        _list.Columns.Add("内存", 160);

        foreach (PetLine line in snapshot.Pets)
        {
            _list.Items.Add(new ListViewItem(new[] { line.Name, line.Source, line.State, line.Memory }));
        }

        BindListOn(_pages.TabPages[0]);
        _status.Text = snapshot.Pets.Count == 0
            ? "尚无宠物运行。可点「导入素材包…」选一个素材包目录（docs/18 §2.1）。"
            : $"共 {snapshot.Pets.Count} 只宠物。内存为进程级读数。";
    }

    private void BindSettings(UiSnapshot snapshot)
    {
        _list.Items.Clear();
        _list.Columns.Clear();
        _list.Columns.Add("项目", 200);
        _list.Columns.Add("值", 300);
        _list.Columns.Add("说明", 300);

        AddSetting("开机自启", "关", "默认关（docs/18 §2.3）");
        AddSetting("语言", "zh-CN", "zh-CN / en-US");
        AddSetting("空闲降帧阈值", "33.3 ms", "= 1000/30，见 FrameBudget.IdleFrameBudgetMs");
        AddSetting("宠物数", snapshot.Pets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "当前实例");
        AddSetting("诊断：日志目录", "(未接线)", "打开日志目录属后续切片");
        AddSetting("诊断：导出诊断包", "(未接线)", "脱敏导出，docs/18 §2.3");

        BindListOn(_pages.TabPages[3]);
        _status.Text = "设置项为只读展示；持久化（docs/17）未接线。";
    }

    private void AddSetting(string name, string value, string note) =>
        _list.Items.Add(new ListViewItem(new[] { name, value, note }));

    private void BindListOn(TabPage page)
    {
        if (!page.Controls.Contains(_list))
        {
            page.Controls.Add(_list);
        }
    }

    /// <summary>四页是否齐全（自检断言，§2.5）。</summary>
    public IReadOnlyList<string> PageIds
    {
        get
        {
            var ids = new List<string>();
            foreach (TabPage page in _pages.TabPages)
            {
                ids.Add(page.Name.StartsWith("tab-", StringComparison.Ordinal) ? page.Name[4..] : page.Name);
            }

            return ids;
        }
    }
}
