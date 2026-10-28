using System;
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
        LanguageSection.Text = context.T("cp.settings.section.language");
        LanguageLabel.Text = context.T("cp.settings.language");
        LanguageHint.Text = context.T("cp.settings.language.hint");
        // 语言选项永远用各自的语言写（简体中文 / English）：用户切到看不懂的语言时，
        // 还能凭这两个词走回来。所以它们不随界面语言翻译表变化。
        LanguageZh.Content = context.T("cp.settings.language.zh");
        LanguageEn.Content = context.T("cp.settings.language.en");
        AppearanceSection.Text = context.T("cp.settings.section.appearance");
        ThemeLabel.Text = context.T("cp.settings.theme");
        ThemeHint.Text = context.T("cp.settings.theme.hint");
        MaterialLabel.Text = context.T("cp.settings.material");
        MaterialHint.Text = context.T("cp.settings.material.hint");
        DiagnosticsSection.Text = context.T("cp.settings.section.diagnostics");
        OpenLogsButton.Content = context.T("cp.diag.openLogs");
        ExportButton.Content = context.T("cp.diag.exportBundle");
        AuditButton.Content = context.T("cp.diag.viewAudit");

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
                SelectLanguage(view.Language);
                SelectTheme(view.Theme);
                SelectMaterial(view.Material);
            }
        }
        finally
        {
            _binding = false;
        }
    }

    private void SelectLanguage(string? code)
    {
        bool english = string.Equals(code, "en-US", StringComparison.OrdinalIgnoreCase);
        LanguageEn.IsChecked = english;
        LanguageZh.IsChecked = !english;
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

    /// <summary>设置项改动即生效：不再有「应用」按钮。</summary>
    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_binding)
        {
            return;
        }

        double idle = _context.Snapshot.Settings?.IdleFrameBudgetMs ?? 1000.0 / 30.0;
        string? problem = _context.ApplySettings(
            idle,
            StartupSwitch.IsChecked == true,
            SelectedMaterial,
            SelectedTheme);
        ShowStatus(problem);
        _context.Refresh();
    }

    /// <summary>
    /// 语言改动即生效：宿主换文案表并重建面板。这里只发意图，不自己 Dispose 任何东西 ——
    /// 这次调用发生在单选按钮的 Checked 事件里，拆掉承载事件的页面是自找竞态。
    /// </summary>
    private void OnLanguageChanged(object sender, RoutedEventArgs e)
    {
        if (_binding)
        {
            return;
        }

        string? problem = _context.SetLanguage(LanguageEn.IsChecked == true ? "en-US" : "zh-CN");
        ShowStatus(problem);
        _context.Refresh();
    }

    private void ShowStatus(string? problem)
    {
        if (problem is null)
        {
            StatusText.Text = string.Empty;
            return;
        }

        StatusText.Text = problem;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "Pd.Bad");
        _context.SetStatus(problem, UiStatusKind.Problem);
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => _context.OpenLogs();

    private void OnOpenAudit(object sender, RoutedEventArgs e) => _context.OpenAudit();

    private void OnExport(object sender, RoutedEventArgs e)
    {
        Window owner = Window.GetWindow(this);
        _context.ExportDiagnostics(owner is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle);
    }
}
