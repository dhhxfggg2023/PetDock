using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using PetDock.Core.Ui;
using UserControl = System.Windows.Controls.UserControl;

namespace PetDock.Ui.Default;

public partial class PetsPage : UserControl, IUiPage
{
    private readonly IUiPageContext _context;
    private readonly ObservableCollection<PetRow> _rows = new();


    public PetsPage(IUiPageContext context)
    {
        _context = context;
        InitializeComponent();
        InitCare();

        ImportButton.Content = context.T("cp.pets.import");
        DuplicateButton.Content = context.T("cp.pets.duplicate");
        DeleteButton.Content = context.T("cp.pets.delete");
        DesktopButton.Content = context.T("cp.pets.desktop.show");
        ColName.Text = context.T("cp.pets.col.name");
        ColSource.Text = context.T("cp.pets.col.source");
        ColState.Text = context.T("cp.pets.col.state");
        ColMemory.Text = context.T("cp.pets.col.memory");
        EmptyTitle.Text = context.T("cp.pets.empty");
        EmptyNote.Text = string.Empty;

        DetailSizeLabel.Text = context.T("cp.pets.detail.size");
        DetailSizeSlider.ValueChanged += OnDetailSizeChanged;
        AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler((_, _) => _sizeDragging = true));
        AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler(OnDetailSizeDragCompleted));

        Rows.ItemsSource = _rows;
        Rows.SelectionChanged += (_, _) =>
        {
            UpdateActions();
            UpdateDetail();
        };
        // 页面会被壳缓存：切走时 Unloaded 退订，切回时 Loaded 重新订阅。
        // 构造期只订阅一次是不够的——第二次进入页面后 Changed 就再也不会到达。
        Loaded += (_, _) =>
        {
            context.Changed -= OnChanged;
            context.Changed += OnChanged;
            Refresh();
        };
        Unloaded += (_, _) => context.Changed -= OnChanged;
        Refresh();
    }

    public string Id => "pets";

    public string Title => _context.T("cp.tab.pets");

    public string Note => _context.T("cp.page.pets.hint");

    public string Glyph => "🐾";

    public int Order => 10;

    public FrameworkElement CreateView() => this;

    private void OnChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(Refresh));

    private void Refresh()
    {
        string? selected = (Rows.SelectedItem as PetRow)?.Id;
        _rows.Clear();
        foreach (PetLine line in _context.Snapshot.Pets)
        {
            _rows.Add(new PetRow(line));
        }

        EmptyPanel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Rows.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CountText.Text = _rows.Count == 0
            ? string.Empty
            : _context.T("cp.pets.count", _rows.Count);

        if (selected is not null)
        {
            Rows.SelectedItem = _rows.FirstOrDefault(row => string.Equals(row.Id, selected, StringComparison.Ordinal));
        }

        UpdateActions();
        UpdateDetail();
        RefreshCare();
    }

    private string? SelectedPetId => (Rows.SelectedItem as PetRow)?.Id;

    private void UpdateActions()
    {
        bool has = SelectedPetId is not null;
        DuplicateButton.IsEnabled = has;
        DeleteButton.IsEnabled = has;
        DesktopButton.IsEnabled = has;

        // 按钮说的是「点下去会发生什么」，不是「现在是哪一档」：
        // 摆着的显示「从桌面收起」，收起的显示「在桌面上显示」。
        bool onDesktop = (Rows.SelectedItem as PetRow)?.OnDesktop == true;
        DesktopButton.Content = _context.T(onDesktop ? "cp.pets.desktop.hide" : "cp.pets.desktop.show");
    }

    /// <summary>
    /// 在桌面上显示 / 从桌面收起（2026-10-30）。收起不删素材、也不删清单条目 ——
    /// 用户报的「已导入的宠物没地方看，不知道怎么打开」就是因为旧实现把这两件事做成了同一件。
    /// </summary>
    private void OnDesktopClick(object sender, RoutedEventArgs e)
    {
        string? id = SelectedPetId;
        if (id is null)
        {
            return;
        }

        bool onDesktop = (Rows.SelectedItem as PetRow)?.OnDesktop == true;
        string? problem = _context.SetPetOnDesktop(id, !onDesktop);
        if (problem is not null)
        {
            ShowError(problem);
            return;
        }

        HideError();
        _context.SetStatus(_context.T(onDesktop ? "cp.pets.desktop.hidden" : "cp.pets.desktop.shown"));
        _context.Refresh();
    }

    /// <summary>选中一行时显示作者/许可/目录；没有声明就写「未声明」，不从目录名猜。</summary>
    private void UpdateDetail()
    {
        PetRow? row = Rows.SelectedItem as PetRow;
        if (row is null)
        {
            DetailBar.Visibility = Visibility.Collapsed;
            DetailIcon.Source = null;
            DetailIconFrame.Visibility = Visibility.Collapsed;
            return;
        }

        DetailBar.Visibility = Visibility.Visible;
        DetailAuthor.Text = _context.T("cp.pets.detail.author", ValueOrUnknown(row.Author));
        DetailLicense.Text = _context.T("cp.pets.detail.license", ValueOrUnknown(row.License));
        DetailPath.Text = _context.T("cp.pets.detail.path", ValueOrUnknown(row.Path));

        BindDetailSize(row);

        DetailIcon.Source = null;
        DetailIconFrame.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(row.IconPath) || !File.Exists(row.IconPath))
        {
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(row.IconPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            DetailIcon.Source = bitmap;
            DetailIconFrame.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            // 图标坏了不影响文字信息；下一次选中会重试。
        }
    }

    private string ValueOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? _context.T("cp.pets.detail.unknown") : value;

    /// <summary>多开：同一个素材包再开一只（不复制磁盘素材）；失败走错误条。</summary>
    private void OnDuplicateClick(object sender, RoutedEventArgs e)
    {
        string? id = SelectedPetId;
        if (id is null)
        {
            return;
        }

        string? problem = _context.DuplicatePet(id);
        if (problem is not null)
        {
            ShowError(problem);
            return;
        }

        HideError();
        _context.SetStatus(_context.T("cp.pets.duplicate.done"), UiStatusKind.Success);
        _context.Refresh();
    }

    /// <summary>删除：先确认，再连素材目录一起删（最后一个引用才删）。</summary>
    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        string? id = SelectedPetId;
        if (id is null)
        {
            return;
        }

        string name = (Rows.SelectedItem as PetRow)?.Name ?? id;
        if (!_context.Confirm(
            _context.T("cp.pets.deleteConfirm", name),
            _context.T("cp.pets.deleteConfirmTitle")))
        {
            return;
        }

        string? problem = _context.DeletePet(id);
        if (problem is not null)
        {
            // 部分是「已从列表删除、素材没删掉」：那只其实已经没了，不刷新会留下一行不存在的宠物。
            _context.Refresh();
            ShowError(problem);
            return;
        }

        HideError();
        _context.SetStatus(_context.T("cp.pets.delete.done"), UiStatusKind.Success);
        _context.Refresh();
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = _context.T("cp.pets.pickFolder"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        // 导入前先让用户看到包名/作者/许可；读不出元数据就保持一键导入，不拿空信息拦人。
        string? summary = PackSummary(dialog.SelectedPath);
        if (summary is not null
            && !_context.Confirm(
                _context.T("cp.pets.importConfirm", summary),
                _context.T("cp.pets.importConfirmTitle")))
        {
            return;
        }

        // 导入在后台跑（UiHost.Work.cs）：按钮先灰掉，防止同一份对话框结果被点两次。
        ImportButton.IsEnabled = false;
        try
        {
            UiCommandResult result = await _context.ImportPetAsync(dialog.SelectedPath);
            if (result.Ok)
            {
                HideError();
                _context.SetStatus(_context.T("cp.pets.count", _context.Snapshot.Pets.Count));
                _context.Refresh();
                return;
            }

            if (result.Cancelled)
            {
                // 取消是用户自己按的：走状态条，不进错误条。
                HideError();
                _context.SetStatus(result.Message);
                return;
            }

            ShowError(result.Message);
        }
        catch (Exception)
        {
            // 宿主已经把可预期的失败翻译成文案；走到这里是没预料的异常，只说「失败了」。
            ShowError(_context.T("ui.work.failed"));
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 导入前摘要：标准包读 petdock-sample.json，图集包读 pet.json。
    /// 读不出来返回 null（仍然导入），不拿「未声明」的空表拦用户。
    /// </summary>
    private string? PackSummary(string packDir)
    {
        string standard = Path.Combine(packDir, "petdock-sample.json");
        string manifest = File.Exists(standard) ? standard : Path.Combine(packDir, "pet.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
            JsonElement root = doc.RootElement;
            string? name = Text(root, "displayName") ?? Text(root, "name");
            string? author = Text(root, "author");
            string? license = LicenseText(root);
            var text = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(name))
            {
                text.Append(name);
            }

            if (!string.IsNullOrWhiteSpace(author))
            {
                text.Append('\n').Append(_context.T("cp.pets.detail.author", author));
            }

            if (!string.IsNullOrWhiteSpace(license))
            {
                text.Append('\n').Append(_context.T("cp.pets.detail.license", license));
            }

            return text.Length == 0 ? null : text.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? LicenseText(JsonElement root)
    {
        if (!root.TryGetProperty("license", out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("spdx", out JsonElement spdx)
            && spdx.ValueKind == JsonValueKind.String)
        {
            return spdx.GetString();
        }

        return null;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBar.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBar.Visibility = Visibility.Collapsed;
}
