// 设置页的可写形态（docs/18 §2.3）。
//
// 之前这一页是纯只读 ListView（诊断两项还写着「(未接线)」）。本文件把它变成可写：
//   开机自启（CheckBox）—— 事实源是注册表，不是设置文件；
//   空闲降帧阈值（NumericUpDown，毫秒）—— 改完立即推给帧泵，不用重启；
//   语言（只读展示）—— 只持久化，界面文案未本地化，这是登记在案的欠账。
//
// 面板只**收集意图**并交给宿主侧的回调（与插件页「授权」入口同款）：
// 面板不碰注册表、不碰文件、不碰帧泵。宿主侧做完了，把结果回来说一句，
// 面板把这句话显示成内联状态标签 —— 不弹 MessageBox（docs/18 §5）。
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
        Text = I18n.Get("cp.settings.startup"),
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

    /// <summary>界面材质三选一（docs/18 §2.7）。键落盘，文案给人看。</summary>
    private readonly ThemedSegmented _materialSwitch = new()
    {
        Name = "material-picker",
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
    internal delegate string? SettingsCommand(double idleFrameBudgetMs, bool startWithWindows, MaterialKind material);

    /// <summary>设置页四个控件的名字（自检按它们断言入口存在）。</summary>
    public IReadOnlyList<string> SettingsControlNames =>
        new[] { _startupBox.Name, _idleFrameBox.Name, _applyButton.Name, _materialSwitch.Name };

    /// <summary>材质选择器当前选中的键（自检读它，不读像素）。</summary>
    public string MaterialKey => _materialSwitch.SelectedKey;

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
    internal void TryApplySettingsForCheck(double idleFrameBudgetMs, bool startWithWindows, MaterialKind? material = null)
    {
        if (material is MaterialKind kind)
        {
            _materialSwitch.Select(Theme.MaterialToken(kind), raise: false);
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

        // 按名字找设置页，不按下标：构造期性能页还没插进来，
        // 此时的「设置页下标」与插完之后的下标不是同一个数（按下标取会越界）。
        Panel? page = FindPage(PanelPages.Settings);
        if (page is null)
        {
            return;
        }

        int dpi = Theme.DpiOf(page);
        _startupBox.FitToText();
        _idleFrameBox.FitToFont(96);
        _applyButton.FitToText();
        _settingsStatus.Font = Theme.FontFor(page, TypeRole.Body);
        _settingsStatus.ForeColor = Theme.TextDim;
        _settingsStatus.BackColor = Theme.Bg;
        _settingsStatus.Height = Theme.Dip(dpi, 34);
        _settingsStatus.Padding = new Padding(
            Theme.Dip(dpi, Theme.SpaceSm), Theme.Dip(dpi, Theme.SpaceXs), Theme.Dip(dpi, Theme.SpaceSm), Theme.Dip(dpi, Theme.SpaceXs));

        FlowLayoutPanel bar = ThemedBar(page);
        bar.Controls.Add(_startupBox);
        bar.Controls.Add(ThemedHint(page, I18n.Get("cp.settings.idleFrameLabel")));
        bar.Controls.Add(_idleFrameBox);
        bar.Controls.Add(_applyButton);

        // 材质单占一行：它是「看起来整窗都变了」的选项，和阈值/自启不是同一类操作。
        _materialSwitch.SetOptions(
            (Theme.MaterialToken(MaterialKind.Opaque), I18n.Get("cp.settings.material.opaque")),
            (Theme.MaterialToken(MaterialKind.Frosted), I18n.Get("cp.settings.material.frosted")),
            (Theme.MaterialToken(MaterialKind.Glass), I18n.Get("cp.settings.material.glass")));
        _materialSwitch.Select(Theme.MaterialToken(Theme.Material), raise: false);
        _materialSwitch.Width = Theme.Dip(dpi, 240);

        FlowLayoutPanel materialBar = ThemedBar(page);
        materialBar.Controls.Add(ThemedHint(page, I18n.Get("cp.settings.material")));
        materialBar.Controls.Add(_materialSwitch);
        materialBar.Controls.Add(ThemedHint(page, I18n.Get("cp.settings.material.hint")));

        page.Controls.Add(_settingsStatus);
        page.Controls.Add(bar);
        page.Controls.Add(materialBar);
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
            Theme.ParseMaterial(_materialSwitch.SelectedKey));
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
