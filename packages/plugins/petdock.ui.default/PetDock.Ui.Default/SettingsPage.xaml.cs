using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PetDock.Core.Ui;
using UserControl = System.Windows.Controls.UserControl;

namespace PetDock.Ui.Default;

public partial class SettingsPage : UserControl, IUiPage
{
    private readonly IUiPageContext _context;
    private bool _binding;

    public SettingsPage(IUiPageContext context)
    {
        _context = context;
        InitializeComponent();

        StartupSection.Text = context.T("cp.settings.section.startup");
        StartupLabel.Text = context.T("cp.settings.startup");
        StartupHint.Text = context.T("cp.settings.startup.hint");
        IdleLabel.Text = context.T("cp.settings.idleFrameLabel");
        IdleHint.Text = context.T("cp.settings.idleFrame.note");
        AppearanceSection.Text = context.T("cp.settings.section.appearance");
        ThemeLabel.Text = context.T("cp.settings.theme");
        ThemeHint.Text = context.T("cp.settings.theme.hint");
        MaterialLabel.Text = context.T("cp.settings.material");
        MaterialHint.Text = context.T("cp.settings.material.hint");
        DiagnosticsSection.Text = context.T("cp.settings.section.diagnostics");
        OpenLogsButton.Content = context.T("cp.diag.openLogs");
        ExportButton.Content = context.T("cp.diag.exportBundle");
        AuditButton.Content = context.T("cp.diag.viewAudit");
        ApplyButton.Content = context.T("cp.settings.apply");

        ThemeSystem.Content = context.T("cp.settings.theme.system");
        ThemeLight.Content = context.T("cp.settings.theme.light");
        ThemeDark.Content = context.T("cp.settings.theme.dark");
        MaterialOpaque.Content = context.T("cp.settings.material.opaque");
        MaterialFrosted.Content = context.T("cp.settings.material.frosted");
        MaterialGlass.Content = context.T("cp.settings.material.glass");

        // 页面会被壳缓存：切走时 Unloaded 退订，切回时 Loaded 重新订阅。
        Loaded += (_, _) =>
        {
            context.Changed -= OnChanged;
            context.Changed += OnChanged;
            Bind();
        };
        Unloaded += (_, _) => context.Changed -= OnChanged;
        Bind();
    }

    public string Id => "settings";

    public string Title => _context.T("cp.tab.settings");

    public string Note => _context.T("cp.page.settings.hint");

    public string Glyph => "⚙";

    public int Order => 30;

    public FrameworkElement CreateView() => this;

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(Bind));

    private void Bind()
    {
        _binding = true;
        try
        {
            SettingsView? view = _context.Snapshot.Settings;
            if (view is not null)
            {
                StartupSwitch.IsChecked = view.StartupEnabled;
                IdleBox.Text = view.IdleFrameBudgetMs.ToString("0.#", CultureInfo.InvariantCulture);
                SelectTheme(view.Theme);
                SelectMaterial(view.Material);
            }
        }
        finally
        {
            _binding = false;
        }
    }

    private void SelectTheme(string? token)
    {
        ThemeSystem.IsChecked = token is null or "system";
        ThemeLight.IsChecked = token == "light";
        ThemeDark.IsChecked = token == "dark";
    }

    private void SelectMaterial(string? token)
    {
        MaterialOpaque.IsChecked = token is null or "opaque";
        MaterialFrosted.IsChecked = token == "frosted";
        MaterialGlass.IsChecked = token == "glass";
    }

    private string SelectedTheme =>
        ThemeLight.IsChecked == true ? "light" : ThemeDark.IsChecked == true ? "dark" : "system";

    private string SelectedMaterial =>
        MaterialFrosted.IsChecked == true ? "frosted" : MaterialGlass.IsChecked == true ? "glass" : "opaque";

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (_binding)
        {
            return;
        }

        if (!double.TryParse(IdleBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double idle)
            || idle <= 0)
        {
            ShowStatus(_context.T("cp.settings.idleFrame.note"), ok: false);
            return;
        }

        string? problem = _context.ApplySettings(
            idle,
            StartupSwitch.IsChecked == true,
            SelectedMaterial,
            SelectedTheme);
        ShowStatus(problem ?? _context.T("cp.settings.saved"), problem is null);
        _context.Refresh();
    }

    private void ShowStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Pd.Good" : "Pd.Bad");
        _context.SetStatus(text);
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => _context.OpenLogs();

    private void OnExport(object sender, RoutedEventArgs e)
    {
        Window owner = Window.GetWindow(this);
        _context.ExportDiagnostics(owner is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle);
    }

    private void OnAudit(object sender, RoutedEventArgs e) => _context.OpenAudit();
}
