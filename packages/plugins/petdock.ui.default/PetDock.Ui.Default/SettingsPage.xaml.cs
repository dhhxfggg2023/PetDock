using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PetDock.Core.Ui;
using UserControl = System.Windows.Controls.UserControl;

namespace PetDock.Ui.Default;

public partial class SettingsPage : UserControl, IUiPage
{
    private readonly IUiPageContext _context;
    private bool _binding;
    private bool _ready;
    private bool _petSizeDragging;

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
        PetSection.Text = context.T("cp.settings.section.pet");
        PetNameLabel.Text = context.T("cp.settings.pet.current");
        PetNameHint.Text = context.T("cp.settings.pet.current.hint");
        PetSizeLabel.Text = context.T("cp.settings.pet.size");
        PetSizeHint.Text = context.T("cp.settings.pet.size.hint");
        PetSizeSmall.Text = context.T("cp.settings.pet.size.small");
        PetSizeMedium.Text = context.T("cp.settings.pet.size.medium");
        PetSizeLarge.Text = context.T("cp.settings.pet.size.large");
        CelestialLabel.Text = context.T("cp.settings.celestial");
        CelestialHint.Text = context.T("cp.settings.celestial.hint");
        SoundSection.Text = context.T("cp.settings.section.sound");
        SoundLabel.Text = context.T("cp.settings.sound");
        SoundHint.Text = context.T("cp.settings.sound.hint");
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
        AboutSection.Text = context.T("cp.settings.section.about");
        AboutVersionLabel.Text = context.T("cp.settings.about.version");
        AboutHint.Text = context.T("cp.settings.about.hint");

        // 无障碍：自绘模板的控件没有可读文本，读屏只能靠显式名字。
        AutomationProperties.SetName(StartupSwitch, StartupLabel.Text);
        AutomationProperties.SetName(SoundSwitch, SoundLabel.Text);
        AutomationProperties.SetName(CelestialSwitch, CelestialLabel.Text);
        AutomationProperties.SetName(PetNameText, PetNameLabel.Text);
        AutomationProperties.SetName(PetSizeSlider, PetSizeLabel.Text);
        AutomationProperties.SetName(OpenLogsButton, OpenLogsButton.Content?.ToString() ?? "logs");
        AutomationProperties.SetName(ExportButton, ExportButton.Content?.ToString() ?? "export");
        AutomationProperties.SetName(AuditButton, AuditButton.Content?.ToString() ?? "audit");

        // 页面会被壳缓存：切走时 Unloaded 退订，切回时 Loaded 重新订阅。
        Loaded += (_, _) =>
        {
            context.Changed -= OnChanged;
            context.Changed += OnChanged;
            Bind();
        };
        Unloaded += (_, _) =>
        {
            context.Changed -= OnChanged;
            _petSizeDragging = false;
        };

        // 拖动过程中只更新读数，松手才落盘/改窗口（每次 ValueChanged 都写盘会让拖拽变卡）。
        AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _petSizeDragging = true));
        AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnPetSizeDragCompleted));

        _ready = true;
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
                SoundSwitch.IsChecked = view.SoundEnabled;
                CelestialSwitch.IsChecked = view.CelestialMode;
                SelectLanguage(view.Language);
                SelectTheme(view.Theme);
                SelectMaterial(view.Material);
                BindPetSize(view.PetSize);
                PetNameText.Text = string.IsNullOrWhiteSpace(view.PetName)
                    ? _context.T("cp.settings.pet.none")
                    : view.PetName;
                AboutVersionText.Text = view.Version ?? "—";
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

    /// <summary>把快照里的尺寸放上滑块；写读数但不触发落盘（_binding 拦住 ValueChanged）。</summary>
    private void BindPetSize(int size)
    {
        int clamped = Math.Clamp(size, (int)PetSizeSlider.Minimum, (int)PetSizeSlider.Maximum);
        PetSizeSlider.Value = clamped;
        PetSizeValue.Text = _context.T("cp.settings.pet.size.value", clamped);
    }

    /// <summary>拖动中只更新读数；松手才真正落盘（见 OnPetSizeDragCompleted）。</summary>
    private void OnPetSizeValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _binding)
        {
            return;
        }

        int size = (int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero);
        PetSizeValue.Text = _context.T("cp.settings.pet.size.value", size);
        if (!_petSizeDragging)
        {
            ApplyPetSize(size);
        }
    }

    private void OnPetSizeDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _petSizeDragging = false;
        if (_ready && !_binding)
        {
            ApplyPetSize((int)Math.Round(PetSizeSlider.Value, MidpointRounding.AwayFromZero));
        }
    }

    /// <summary>尺寸改动即生效：主窗与其余几只一起改，窗口不重建。</summary>
    private void ApplyPetSize(int size)
    {
        string? problem = _context.SetPetSize(size);
        ShowStatus(problem);
        _context.Refresh();
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
            SelectedTheme,
            SoundSwitch.IsChecked == true,
            CelestialSwitch.IsChecked == true);
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
