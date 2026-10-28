using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        ColName.Text = context.T("cp.pets.col.name");
        ColSource.Text = context.T("cp.pets.col.source");
        ColState.Text = context.T("cp.pets.col.state");
        ColMemory.Text = context.T("cp.pets.col.memory");
        EmptyTitle.Text = context.T("cp.pets.empty");
        EmptyNote.Text = string.Empty;

        Rows.ItemsSource = _rows;
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
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
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

        if (!_context.ImportPet(dialog.SelectedPath, out string message))
        {
            ShowError(message);
            return;
        }

        HideError();
        _context.SetStatus(_context.T("cp.pets.count", _context.Snapshot.Pets.Count));
        _context.Refresh();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBar.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBar.Visibility = Visibility.Collapsed;
}
