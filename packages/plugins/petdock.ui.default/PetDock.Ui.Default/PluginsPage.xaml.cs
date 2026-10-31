using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PetDock.Core.Ui;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace PetDock.Ui.Default;

public partial class PluginsPage : UserControl, IUiPage
{
    private readonly IUiPageContext _context;
    private readonly ObservableCollection<PluginRow> _rows = new();
    private readonly ObservableCollection<TrashRow> _trash = new();
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
        DetailLabel.Text = context.T("cp.plugins.detail.label");

        Rows.ItemsSource = _rows;
        TrashRows.ItemsSource = _trash;
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
            EmptyText.Text = _context.T("cp.plugins.empty");
            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Rows.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            RefreshTrash();
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
        UpdateDetail();
    }

    private PluginRow? Selected => Rows.SelectedItem as PluginRow;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        UpdateDetail();
    }

    /// <summary>
    /// 选中那一行的功能说明（2026-10-31；用户报「插件没有功能解释」）。
    /// 说明来自清单的 description；没声明就显示「未声明」—— 从名字或 id 猜一句是编造。
    /// </summary>
    private void UpdateDetail()
    {
        PluginRow? row = Selected;
        if (row is null)
        {
            DetailBar.Visibility = Visibility.Collapsed;
            DetailText.Text = string.Empty;
            return;
        }

        DetailBar.Visibility = Visibility.Visible;
        DetailText.Text = string.IsNullOrWhiteSpace(row.Description)
            ? _context.T("cp.plugins.detail.undeclared")
            : row.Description!;
    }

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

    private void RefreshTrash()
    {
        _trash.Clear();
        IReadOnlyList<TrashLine> lines = _context.TrashPlugins();
        long bytes = 0;
        foreach (TrashLine line in lines)
        {
            _trash.Add(new TrashRow(line, _context));
            bytes += line.Bytes;
        }

        TrashToggle.Content = _context.T("cp.plugins.trash.header", _trash.Count);
        System.Windows.Automation.AutomationProperties.SetName(TrashToggle, TrashToggle.Content.ToString() ?? "trash");
        TrashSummary.Text = _trash.Count == 0
            ? string.Empty
            : _context.T("cp.plugins.trash.total", (bytes / 1048576.0).ToString("0.0"));

        bool empty = _trash.Count == 0;
        TrashEmpty.Text = _context.T("cp.plugins.trash.empty");
        TrashEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        TrashRows.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTrashToggle(object sender, RoutedEventArgs e) =>
        TrashPanel.Visibility = TrashPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string dirName })
        {
            return;
        }

        try
        {
            UiCommandResult result = await _context.RestorePluginAsync(dirName);
            _context.SetStatus(
                result.Message,
                result.Ok ? UiStatusKind.Success : result.Cancelled ? UiStatusKind.Info : UiStatusKind.Problem);
            _context.Refresh();
        }
        catch (Exception)
        {
            _context.SetStatus(_context.T("ui.work.failed"), UiStatusKind.Problem);
        }
    }

    private async void OnPurgeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string dirName })
        {
            return;
        }

        TrashRow? row = _trash.FirstOrDefault(candidate => string.Equals(candidate.DirName, dirName, StringComparison.Ordinal));
        string name = row?.Name ?? dirName;
        if (!_context.Confirm(_context.T("cp.plugins.trash.confirm", name), _context.T("cp.plugins.trash.confirmTitle")))
        {
            return;
        }

        try
        {
            UiCommandResult result = await _context.PurgePluginAsync(dirName);
            _context.SetStatus(
                result.Message,
                result.Ok ? UiStatusKind.Success : result.Cancelled ? UiStatusKind.Info : UiStatusKind.Problem);
            _context.Refresh();
        }
        catch (Exception)
        {
            _context.SetStatus(_context.T("ui.work.failed"), UiStatusKind.Problem);
        }
    }

    /// <summary>回收站一行的显示数据（标签与大小在这里本地化一次，DataTemplate 只做绑定）。</summary>
    public sealed class TrashRow
    {
        public TrashRow(TrashLine line, IUiPageContext context)
        {
            DirName = line.DirName;
            Name = string.IsNullOrEmpty(line.Name) ? line.DirName : line.Name;
            Version = line.Version;
            RemovedAt = line.RemovedAt;
            SizeText = context.T("cp.plugins.trash.size", (line.Bytes / 1048576.0).ToString("0.0"));
            Restorable = line.Restorable;
            RestoreLabel = context.T("cp.plugins.trash.restore");
            PurgeLabel = context.T("cp.plugins.trash.purge");
        }

        public string DirName { get; }

        public string Name { get; }

        public string Version { get; }

        public string RemovedAt { get; }

        public string SizeText { get; }

        public bool Restorable { get; }

        public string RestoreLabel { get; }

        public string PurgeLabel { get; }
    }
}
