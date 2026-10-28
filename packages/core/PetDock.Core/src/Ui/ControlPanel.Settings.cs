// 设置页的可写形态（原规格 18 §2.3）。
//
// 之前这一页是纯只读 ListView（诊断两项还写着「(未接线)」）。本文件把它变成可写：
//   开机自启（CheckBox）—— 事实源是注册表，不是设置文件；
//   空闲降帧阈值（NumericUpDown，毫秒）—— 改完立即推给帧泵，不用重启；
//   语言（只读展示）—— 只持久化，界面文案未本地化，这是登记在案的欠账。
//
// 面板只**收集意图**并交给宿主侧的回调（与插件页「授权」入口同款）：
// 面板不碰注册表、不碰文件、不碰帧泵。宿主侧做完了，把结果回来说一句，
// 面板把这句话显示成内联状态标签 —— 不弹 MessageBox（原规格 18 §5）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class ControlPanel
{
    /// <summary>开机自启勾选框。</summary>
    private readonly ThemedCheckBox _startupBox = new()
    {
        // 行标签已经写了「开机自启」，控件自己不再重复一遍（2026-10-28 截图里两个并排）。
        Text = string.Empty,
        Name = "startup-box",
    };

    /// <summary>空闲降帧阈值（毫秒）。</summary>
    private readonly ThemedNumber _idleFrameBox = new()
    {
        Name = "idle-frame-box",
        Minimum = 17,
        Maximum = 200,
        Increment = 1,
        Value = 50,
    };

    /// <summary>「应用」按钮。</summary>
    private readonly ThemedButton _applyButton = new()
    {
        Text = I18n.Get("cp.settings.apply"),
        Name = "apply-settings",
        Primary = true,
    };

    /// <summary>界面材质三选一（原规格 18 §2.7）。键落盘，文案给人看。</summary>
    private readonly ThemedSegmented _materialSwitch = new()
    {
        Name = "material-picker",
    };

    /// <summary>界面亮度三选一：跟随系统 / 浅色 / 深色（2026-10-28 第二版）。</summary>
    private readonly ThemedSegmented _themeSwitch = new()
    {
        Name = "theme-picker",
    };

    /// <summary>内联状态标签（成功与失败都写在这里）。</summary>
    private readonly Label _settingsStatus = new()
    {
        Name = "settings-status",
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
        Text = string.Empty,
    };

    /// <summary>设置动作的回调（宿主侧执行）。</summary>
    private SettingsCommand? _settingsCommand;

    /// <summary>状态标签「是否被要求显示」的状态（自检读它，不读 WinForms 的 Visible）。</summary>
    private bool _settingsStatusShown;

    /// <summary>
    /// 设置回调的形状：面板把用户填的意图交过来，宿主侧执行后回一句话。
    /// 返回 null = 成功；返回字符串 = 失败或部分失败的原因。
    /// </summary>
    internal delegate string? SettingsCommand(double idleFrameBudgetMs, bool startWithWindows, MaterialKind material, ThemeMode theme);

    /// <summary>设置页五个控件的名字（自检按它们断言入口存在）。</summary>
    public IReadOnlyList<string> SettingsControlNames =>
        new[] { _startupBox.Name, _idleFrameBox.Name, _applyButton.Name, _materialSwitch.Name, _themeSwitch.Name };

    /// <summary>材质选择器当前选中的键（自检读它，不读像素）。</summary>
    public string MaterialKey => _materialSwitch.SelectedKey;

    /// <summary>亮度选择器当前选中的键：system / light / dark（自检读它）。</summary>
    public string ThemeKey => _themeSwitch.SelectedKey;

    /// <summary>设置页状态标签当前是否被要求显示（自检读它）。</summary>
    public bool SettingsStatusShown => _settingsStatusShown;

    /// <summary>设置页状态标签的当前文字（自检读它，非空即可判）。</summary>
    public string SettingsStatusText => _settingsStatus.Text;

    /// <summary>状态标签上那句话是「成功」还是「失败」（自检断它，不解析文案）。</summary>
    public bool SettingsStatusOk { get; private set; }

    /// <summary>开机自启勾选框当前是否勾着（自检读它）。</summary>
    public bool StartupChecked => _startupBox.Checked;

    /// <summary>空闲降帧阈值当前值（毫秒，自检读它）。</summary>
    public decimal IdleFrameBudgetMs => _idleFrameBox.Value;

    /// <summary>给设置页装一个宿主侧的回调（构造时传入）。</summary>
    public void SetSettingsCommand(SettingsCommand? command) => _settingsCommand = command;

    /// <summary>
    /// 自检入口：把一组值交给宿主侧回调并把结果显示成内联状态。
    /// 与点击「应用」走同一条路（同一个回调、同一个状态标签），
    /// 区别只是不经过按钮事件 —— 自检不该靠模拟点击来证明功能。
    /// </summary>
    internal void TryApplySettingsForCheck(double idleFrameBudgetMs, bool startWithWindows, MaterialKind? material = null, ThemeMode? theme = null)
    {
        if (material is MaterialKind kind)
        {
            _materialSwitch.Select(Theme.MaterialToken(kind), raise: false);
        }
        if (theme is ThemeMode mode)
        {
            _themeSwitch.Select(Theme.ModeToken(mode), raise: false);
        }
        _idleFrameBox.Value = (decimal)Math.Round(
            Math.Clamp(idleFrameBudgetMs, (double)_idleFrameBox.Minimum, (double)_idleFrameBox.Maximum),
            1,
            MidpointRounding.AwayFromZero);
        _startupBox.Checked = startWithWindows;
        ApplySettings();
    }

    /// <summary>
    /// 设置页的控件树：操作栏（勾选框 + 数值框 + 应用按钮）+ 状态标签。
    /// 顺序要紧：后加的先排版 —— 状态标签与操作栏都 Dock=Bottom，状态标签先加、操作栏后加。
    /// </summary>
    private void InitSettingsPage()
    {
        _applyButton.Click += (_, _) => ApplySettings();

        // 按名字找设置页，不按下标：构造期性能页还没插进来。
        Panel? page = FindPage(PanelPages.Settings);
        if (page is null)
        {
            return;
        }

        int dpi = Theme.DpiOf(page);
        _startupBox.FitToText();
        _startupBox.Width = Theme.Dip(dpi, 120);
        _idleFrameBox.FitToFont(96);
        _idleFrameBox.Width = Theme.Dip(dpi, 120);
        _applyButton.FitToText();
        _openLogsButton.FitToText();
        _exportBundleButton.FitToText();
        _auditButton.FitToText();

        _materialSwitch.SetOptions(
            (Theme.MaterialToken(MaterialKind.Opaque), I18n.Get("cp.settings.material.opaque")),
            (Theme.MaterialToken(MaterialKind.Frosted), I18n.Get("cp.settings.material.frosted")),
            (Theme.MaterialToken(MaterialKind.Glass), I18n.Get("cp.settings.material.glass")));
        _materialSwitch.Select(Theme.MaterialToken(Theme.Material), raise: false);
        _materialSwitch.Width = Theme.Dip(dpi, 240);

        _themeSwitch.SetOptions(
            (Theme.ModeToken(ThemeMode.System), I18n.Get("cp.settings.theme.system")),
            (Theme.ModeToken(ThemeMode.Light), I18n.Get("cp.settings.theme.light")),
            (Theme.ModeToken(ThemeMode.Dark), I18n.Get("cp.settings.theme.dark")));
        _themeSwitch.Select(Theme.ModeToken(Theme.Mode), raise: false);
        _themeSwitch.Width = Theme.Dip(dpi, 240);

        // 三个诊断入口排成一行：三行各带说明会把默认高度的设置页顶出滚动区，
        // 最后一行只露出半句说明（2026-10-28 截图实测）。它们本来就是同一类只读动作。
        int gap = Theme.Dip(dpi, Theme.SpaceSm);
        _openLogsButton.Margin = new Padding(0, 0, gap, 0);
        _exportBundleButton.Margin = new Padding(0, 0, gap, 0);
        _auditButton.Margin = Padding.Empty;
        var diagnosticBar = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            BackColor = Theme.Bg,
            Name = "diagnostic-tools",
        };
        diagnosticBar.Controls.Add(_openLogsButton);
        diagnosticBar.Controls.Add(_exportBundleButton);
        diagnosticBar.Controls.Add(_auditButton);
        diagnosticBar.Size = diagnosticBar.GetPreferredSize(Size.Empty);

        // 一页 = 若干段 + 每段若干行；页面只声明内容，坐标与间距交给 FormGrid。
        Panel stack = FormGrid.Stack(page);
        FormGrid.Fill(stack, new Control[]
        {
            FormGrid.Section(page, I18n.Get("cp.settings.section.startup")),
            FormGrid.Row(page, I18n.Get("cp.settings.startup"), _startupBox, I18n.Get("cp.settings.startup.hint")),
            FormGrid.Row(page, I18n.Get("cp.settings.idleFrameLabel"), _idleFrameBox, I18n.Get("cp.settings.idleFrame.note")),
            FormGrid.Gap(page),
            FormGrid.Section(page, I18n.Get("cp.settings.section.appearance")),
            FormGrid.Row(page, I18n.Get("cp.settings.theme"), _themeSwitch, I18n.Get("cp.settings.theme.hint")),
            FormGrid.Row(page, I18n.Get("cp.settings.material"), _materialSwitch, I18n.Get("cp.settings.material.hint")),
            FormGrid.Gap(page),
            FormGrid.Section(page, I18n.Get("cp.settings.section.diagnostics")),
            // 这三个入口的名字已经说清各自做什么；再挂一行说明会把 96dpi 下的页面顶出滚动区。
            FormGrid.Row(page, I18n.Get("cp.settings.row.diagnosticTools"), diagnosticBar),
        });

        // 底栏：状态 + 唯一的保存按钮。设置页只有这一个提交口，其它控件改的只是草稿。
        // 用 FormGrid.Bar（宠物页/插件页同款），不再在页面里手写一遍 FlowLayoutPanel 的 padding。
        FlowLayoutPanel bar = FormGrid.Bar(page);
        bar.Controls.Add(_applyButton);

        _settingsStatus.Font = Theme.FontFor(page, TypeRole.Body);
        _settingsStatus.ForeColor = Theme.TextDim;
        _settingsStatus.BackColor = Theme.Bg;
        _settingsStatus.Height = Theme.Dip(dpi, 34);
        _settingsStatus.Width = Theme.Dip(dpi, 420);
        _settingsStatus.TextAlign = ContentAlignment.MiddleRight;
        _settingsStatus.Padding = new Padding(0, 0, Theme.Dip(dpi, Theme.SpaceMd), 0);
        bar.Controls.Add(_settingsStatus);

        page.Controls.Add(stack);
        page.Controls.Add(bar);
        stack.BringToFront();
    }

    /// <summary>按页面 id 找那一页（找不到返回 null，不抛）。</summary>
    private Panel? FindPage(string id) => _pageOf.TryGetValue(id, out Panel? page) ? page : null;

    /// <summary>
    /// 把设置页的当前值交给宿主侧执行。宿主侧回来说一句，结果显示在内联状态标签上。
    /// 宿主侧没接（null）时如实说「设置功能不可用」，不假装应用成功。
    /// </summary>
    private void ApplySettings()
    {
        if (_settingsCommand is null)
        {
            ShowSettingsStatus(I18n.Get("cp.settings.unavailable"), ok: false);
            return;
        }

        string? problem = _settingsCommand(
            (double)_idleFrameBox.Value,
            _startupBox.Checked,
            Theme.ParseMaterial(_materialSwitch.SelectedKey),
            Theme.ParseMode(_themeSwitch.SelectedKey));
        ShowSettingsStatus(
            problem ?? I18n.Get("cp.settings.saved"),
            ok: problem is null);
    }

    /// <summary>显示内联状态（成功与失败都留在这一页里，不弹模态框）。</summary>
    private void ShowSettingsStatus(string message, bool ok)
    {
        _settingsStatus.Text = message;
        _settingsStatus.ForeColor = ok ? Theme.Good : Theme.Bad;
        _settingsStatusShown = true;
        SettingsStatusOk = ok;
    }

    /// <summary>
    /// 用快照里的设置状态刷新设置页（读数来源只有一个：快照）。
    /// 开机自启的勾选状态来自注册表（快照里的 StartupEnabled），
    /// 阈值来自设置文件里的值。
    /// </summary>
    private void BindSettingsValues(SettingsView? view)
    {
        if (view is null)
        {
            return;
        }

        _startupBox.Checked = view.StartupEnabled;
        double clamped = Math.Clamp(
            view.IdleFrameBudgetMs,
            UserSettings.MinIdleFrameBudgetMs,
            UserSettings.MaxIdleFrameBudgetMs);
        _idleFrameBox.Value = (decimal)Math.Round(clamped, 1, MidpointRounding.AwayFromZero);
    }
}
