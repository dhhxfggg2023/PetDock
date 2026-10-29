using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

        ImportButton.Content = context.T("cp.pets.import");
        DuplicateButton.Content = context.T("cp.pets.duplicate");
        DeleteButton.Content = context.T("cp.pets.delete");
        ColName.Text = context.T("cp.pets.col.name");
        ColSource.Text = context.T("cp.pets.col.source");
        ColState.Text = context.T("cp.pets.col.state");
        ColMemory.Text = context.T("cp.pets.col.memory");
        EmptyTitle.Text = context.T("cp.pets.empty");
        EmptyNote.Text = string.Empty;

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
    }

    private string? SelectedPetId => (Rows.SelectedItem as PetRow)?.Id;

    private void UpdateActions()
    {
        bool has = SelectedPetId is not null;
        DuplicateButton.IsEnabled = has;
        DeleteButton.IsEnabled = has;
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

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBar.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBar.Visibility = Visibility.Collapsed;
}
