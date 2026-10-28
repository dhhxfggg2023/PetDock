using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PetDock.Core.Ui;
using UserControl = System.Windows.Controls.UserControl;

namespace PetDock.Ui.Default;

public partial class PluginsPage : UserControl, IUiPage
{
    private readonly IUiPageContext _context;
    private readonly ObservableCollection<PluginRow> _rows = new();
    private bool _refreshing;

    public PluginsPage(IUiPageContext context)
    {
        _context = context;
        InitializeComponent();

        InstallButton.Content = context.T("cp.plugins.install");
        RemoveButton.Content = context.T("cp.plugins.remove");
        AuthorizeButton.Content = context.T("cp.plugins.authorize");
        ColName.Text = context.T("cp.plugins.col.name");
        ColSource.Text = context.T("cp.plugins.col.source");
        ColVersion.Text = context.T("cp.plugins.col.version");
        ColState.Text = context.T("cp.plugins.col.state");
        ColGranted.Text = context.T("cp.plugins.col.granted");
        ColEnabled.Text = context.T("cp.plugins.col.enabled");
        HintText.Text = context.T("cp.plugins.disableHint");

        Rows.ItemsSource = _rows;
        // 页面会被壳缓存：切走时 Unloaded 退订，切回时 Loaded 重新订阅。
        Loaded += (_, _) =>
        {
            context.Changed -= OnChanged;
            context.Changed += OnChanged;
            Refresh();
        };
        Unloaded += (_, _) => context.Changed -= OnChanged;
        Refresh();
    }

    public string Id => "plugins";

    public string Title => _context.T("cp.tab.plugins");

    public string Note => _context.T("cp.page.plugins.hint");

    public string Glyph => "🧩";

    public int Order => 20;

    public FrameworkElement CreateView() => this;

    private void OnChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(Refresh));

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            string? selected = (Rows.SelectedItem as PluginRow)?.Id;
            _rows.Clear();
            foreach (PluginLine line in _context.Snapshot.Plugins)
            {
                _rows.Add(new PluginRow(line));
            }

            CountText.Text = _rows.Count == 0 ? string.Empty : _context.T("cp.plugins.count", _rows.Count);
            if (selected is not null)
            {
                Rows.SelectedItem = _rows.FirstOrDefault(row => string.Equals(row.Id, selected, StringComparison.Ordinal));
            }
            else if (_rows.Count > 0)
            {
                // 首次进入先选中第一行：否则「删除/授权」永远是灰的，用户会以为没有这两个动作。
                Rows.SelectedIndex = 0;
            }
        }
        finally
        {
            _refreshing = false;
        }

        UpdateButtons();
    }

    private PluginRow? Selected => Rows.SelectedItem as PluginRow;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        PluginRow? row = Selected;
        RemoveButton.IsEnabled = row is not null && row.Removable;
        RemoveButton.ToolTip = row is null
            ? null
            : _context.T(row.Removable ? "cp.plugins.remove.hint" : "cp.plugins.remove.essential");
        AuthorizeButton.IsEnabled = row is not null;
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (_refreshing || sender is not ToggleButton { Tag: string id })
        {
            return;
        }

        PluginRow? row = _rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
        if (row is null)
        {
            return;
        }

        row.Enabled = !row.Enabled;
        _context.TogglePluginDisabled(id);
        _context.Refresh();
    }

    private void OnAuthorizeClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
        {
            _context.AuthorizePlugin(row.Id);
        }
    }

    private void OnInstallClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var folder = new MenuItem { Header = _context.T("cp.plugins.install.folder") };
        folder.Click += (_, _) => Install(folder: true);
        var zip = new MenuItem { Header = _context.T("cp.plugins.install.zip") };
        zip.Click += (_, _) => Install(folder: false);
        menu.Items.Add(folder);
        menu.Items.Add(zip);
        menu.PlacementTarget = InstallButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void Install(bool folder)
    {
        string? source = folder ? PickFolder() : PickZip();
        if (source is null)
        {
            return;
        }

        // 解压/校验在后台跑（UiHost.Work.cs）：安装按钮先灰掉，避免并发写同一个插件根。
        InstallButton.IsEnabled = false;
        try
        {
            UiCommandResult result = await _context.InstallPluginAsync(source, enable: true);
            if (result.Ok)
            {
                _context.SetStatus(_context.T("cp.plugins.install.done"), UiStatusKind.Success);
                _context.Refresh();
                return;
            }

            _context.SetStatus(
                result.Message,
                result.Cancelled ? UiStatusKind.Info : UiStatusKind.Problem);
        }
        catch (Exception)
        {
            _context.SetStatus(_context.T("ui.work.failed"), UiStatusKind.Problem);
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }

    private string? PickFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = _context.T("cp.plugins.install.folder"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private string? PickZip()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Filter = _context.T("cp.plugins.install.zipFilter"),
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null;
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        // 走宿主自绘的置顶确认框：面板最大化时系统 MessageBox 会被压在宠物窗下面（看不见）。
        if (!_context.Confirm(_context.T("cp.plugins.remove.confirm", row.Name), _context.T("cp.plugins.remove.confirmTitle")))
        {
            return;
        }

        string? problem = _context.RemovePlugin(row.Id);
        _context.SetStatus(
            problem ?? _context.T("cp.plugins.remove.done"),
            problem is null ? UiStatusKind.Success : UiStatusKind.Problem);
        _context.Refresh();
    }
}
