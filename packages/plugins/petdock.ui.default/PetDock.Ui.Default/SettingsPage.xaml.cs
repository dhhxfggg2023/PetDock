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
        PetSizeTarget.Text = context.T("cp.settings.pet.size.target");
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
        UpdateLabel.Text = context.T("cp.settings.update.label");
        UpdateHint.Text = context.T("cp.settings.update.hint");
        UpdateCheckButton.Content = context.T("cp.settings.update.check");
        UpdateDownloadButton.Content = context.T("cp.settings.update.download");
        UpdateRestartButton.Content = context.T("cp.settings.update.restartNow");
        UpdateLaterButton.Content = context.T("cp.settings.update.later");
        UpdateOpenButton.Content = context.T("cp.settings.update.open");
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
        AutomationProperties.SetName(UpdateCheckButton, UpdateCheckButton.Content?.ToString() ?? "update");
        AutomationProperties.SetName(UpdateDownloadButton, UpdateDownloadButton.Content?.ToString() ?? "download");
        AutomationProperties.SetName(UpdateRestartButton, UpdateRestartButton.Content?.ToString() ?? "restart");
        AutomationProperties.SetName(UpdateLaterButton, UpdateLaterButton.Content?.ToString() ?? "later");
        AutomationProperties.SetName(UpdateOpenButton, UpdateOpenButton.Content?.ToString() ?? "open");

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

                // 滑块改的是这一只（2026-10-31）：名字直接写在滑块旁边。
                // 桌上一只都没有时它改的是「新宠物的默认大小」，所以那行字要说这件事。
                PetSizeTarget.Text = _context.Snapshot.Pets.Count > 0
                    ? _context.Snapshot.Pets[0].Name
                    : _context.T("cp.settings.pet.size.defaultTarget");
                AboutVersionText.Text = view.Version ?? "—";
            }

            BindUpdate();
        }
        finally
        {
            _binding = false;
        }
    }

    /// <summary>
    /// 更新那一块的**事实来源是磁盘**，不是页面自己的记忆（2026-10-31）：
    /// 用户可能上次就点了「稍后更新」然后关掉面板，这次重开必须还看得见「已登记」。
    /// 三种状态各显示各的：已下载待装（给两个时机按钮）/ 刚装好（报一句）/ 没动静（什么都不说）。
    /// </summary>
    private void BindUpdate()
    {
        string? pending = _context.PendingUpdateVersion;
        string? report = _context.LastUpdateReport;

        if (pending is not null)
        {
            UpdateDownloadButton.Visibility = Visibility.Collapsed;
            UpdateRestartButton.Visibility = Visibility.Visible;
            UpdateLaterButton.Visibility = Visibility.Visible;
            if (string.IsNullOrEmpty(UpdateStatus.Text))
            {
                UpdateStatus.Text = _context.T("cp.settings.update.pending", pending);
            }

            return;
        }

        UpdateRestartButton.Visibility = Visibility.Collapsed;
        UpdateLaterButton.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrEmpty(report))
        {
            UpdateStatus.Text = report;
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

    /// <summary>
    /// 把快照里的尺寸放上滑块；写读数但不触发落盘（_binding 拦住 ValueChanged）。
    /// 2026-10-31：尺寸逐只 —— 这里显示的是**第一只**的当前值，拖动时另见 OnPetSizeValueChanged
    /// 的说明（设置页在没有选中项时改的是默认值）。
    /// </summary>
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

    /// <summary>
    /// 尺寸改动即生效，但**只改一只**（2026-10-31；用户：「调整大小应该是每个宠物单独调整」）。
    ///
    /// 改的是哪一只：快照里的第一只（宠物页与设置页共用同一个「当前宠物」口径）。
    /// 桌上一只都没有时 petId 为 null，此处的语义退化成「改默认值」—— 新导入的宠物按它开局，
    /// 已经在桌上的不会被这条命令动到。
    /// </summary>
    private void ApplyPetSize(int size)
    {
        string? petId = _context.Snapshot.Pets.Count > 0 ? _context.Snapshot.Pets[0].Id : null;
        string? problem = _context.SetPetSize(petId, size);
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

    /// <summary>
    /// 检查更新（2026-10-31）。三种结局必须分开显示：有新版本 / 已是最新 / **没查到**。
    /// 把「没查到」画成「已是最新」是撒谎 —— 用户会在一个其实有更新的版本上永远不再检查。
    /// </summary>
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        UpdateCheckButton.IsEnabled = false;
        UpdateStatus.Text = _context.T("cp.settings.update.checking");
        UpdateDownloadButton.Visibility = Visibility.Collapsed;
        UpdateOpenButton.Visibility = Visibility.Collapsed;
        try
        {
            UpdateCheckLine result = await _context.CheckUpdateAsync();
            if (result.Error is not null)
            {
                UpdateStatus.Text = _context.T("cp.settings.update.failedShort", result.Error);
                return;
            }

            if (result.HasUpdate)
            {
                // T() 只有 0/1 个占位符的重载；这一条有三个，先取模板再自己填（不变的是键与占位符形状）。
                UpdateStatus.Text = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    _context.T("cp.settings.update.available"),
                    result.Current,
                    result.Latest!,
                    SizeText(result.Bytes));
                UpdateDownloadButton.Visibility = Visibility.Visible;
                return;
            }

            UpdateStatus.Text = _context.T("cp.settings.update.current", result.Current);
        }
        catch (Exception)
        {
            UpdateStatus.Text = _context.T("cp.settings.update.failed");
        }
        finally
        {
            UpdateCheckButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 下载 → 校验 → 解压 → 登记「待安装」。这一步**不动**正在运行的文件：
    /// 解好之后才出现「立即重启更新 / 稍后更新」两个按钮。
    /// </summary>
    private async void OnDownloadUpdate(object sender, RoutedEventArgs e)
    {
        UpdateDownloadButton.IsEnabled = false;
        UpdateStatus.Text = _context.T("cp.settings.update.downloading");
        try
        {
            UpdateInstallLine result = await _context.DownloadUpdateAsync();
            if (result.State == UpdateInstallState.Failed)
            {
                UpdateStatus.Text = result.Message ?? _context.T("cp.settings.update.failed");
                return;
            }

            UpdateStatus.Text = _context.T("cp.settings.update.pending", result.Line?.Latest ?? "?");
            UpdateDownloadButton.Visibility = Visibility.Collapsed;
            UpdateRestartButton.Visibility = Visibility.Visible;
            UpdateLaterButton.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            UpdateStatus.Text = _context.T("cp.settings.update.failed");
        }
        finally
        {
            UpdateDownloadButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 「立即重启更新」：宿主把活交给 PetDock.Update.exe 之后会退出，进程自己重启回来。
    /// 这一句文案要在进程消失之前留在用户眼前 —— 否则他会以为点崩了。
    /// </summary>
    private async void OnRestartUpdate(object sender, RoutedEventArgs e)
    {
        UpdateRestartButton.IsEnabled = false;
        UpdateLaterButton.IsEnabled = false;
        UpdateStatus.Text = _context.T("cp.settings.update.restarting", _context.PendingUpdateVersion ?? "?");
        try
        {
            UpdateInstallLine result = await _context.InstallUpdateAsync(restartNow: true);
            if (result.State == UpdateInstallState.Failed)
            {
                UpdateStatus.Text = result.Message ?? _context.T("cp.settings.update.failed");
                UpdateRestartButton.IsEnabled = true;
                UpdateLaterButton.IsEnabled = true;
            }
        }
        catch (Exception)
        {
            UpdateStatus.Text = _context.T("cp.settings.update.failed");
            UpdateRestartButton.IsEnabled = true;
            UpdateLaterButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 「稍后更新」：什么都不搬，只确认「下次启动会装」这件事真的落盘了。
    /// 说「已登记」而不是「已更新」，是因为现在装的还是旧版本 —— 用户下次开机才会看到新版本。
    /// </summary>
    private async void OnLaterUpdate(object sender, RoutedEventArgs e)
    {
        UpdateLaterButton.IsEnabled = false;
        try
        {
            UpdateInstallLine result = await _context.InstallUpdateAsync(restartNow: false);
            UpdateStatus.Text = result.State == UpdateInstallState.Failed
                ? result.Message ?? _context.T("cp.settings.update.failed")
                : _context.T("cp.settings.update.laterQueued", _context.PendingUpdateVersion ?? "?");
            if (result.State != UpdateInstallState.Failed)
            {
                UpdateRestartButton.Visibility = Visibility.Collapsed;
                UpdateLaterButton.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception)
        {
            UpdateStatus.Text = _context.T("cp.settings.update.failed");
        }
        finally
        {
            UpdateLaterButton.IsEnabled = true;
        }
    }

    private void OnOpenUpdateFolder(object sender, RoutedEventArgs e)
    {
        string? problem = _context.OpenUpdateFolder();
        if (problem is not null)
        {
            UpdateStatus.Text = problem;
        }
    }

    private static string SizeText(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";

    private void OnExport(object sender, RoutedEventArgs e)
    {
        Window owner = Window.GetWindow(this);
        _context.ExportDiagnostics(owner is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle);
    }
}
