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
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class ControlPanel
{
    /// <summary>开机自启勾选框。</summary>
    private readonly CheckBox _startupBox = new()
    {
        Text = "开机自启",
        Name = "startup-box",
        AutoSize = true,
    };

    /// <summary>空闲降帧阈值（毫秒）。</summary>
    private readonly NumericUpDown _idleFrameBox = new()
    {
        Name = "idle-frame-box",
        Minimum = 17,
        Maximum = 200,
        Increment = 1,
        Width = 90,
    };

    /// <summary>「应用」按钮。</summary>
    private readonly Button _applyButton = new()
    {
        Text = "应用",
        Name = "apply-settings",
        Width = 90,
        Height = 28,
    };

    /// <summary>内联状态标签（成功与失败都写在这里）。</summary>
    private readonly Label _settingsStatus = new()
    {
        Name = "settings-status",
        AutoSize = false,
        Height = 34,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 4, 8, 4),
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
    internal delegate string? SettingsCommand(double idleFrameBudgetMs, bool startWithWindows);

    /// <summary>设置页三个控件的名字（自检按它们断言入口存在）。</summary>
    public IReadOnlyList<string> SettingsControlNames =>
        new[] { _startupBox.Name, _idleFrameBox.Name, _applyButton.Name };

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
    internal void TryApplySettingsForCheck(double idleFrameBudgetMs, bool startWithWindows)
    {
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

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8, 6, 8, 6),
            WrapContents = false,
        };
        bar.Controls.Add(_startupBox);
        bar.Controls.Add(new Label { Text = "空闲降帧阈值(ms)", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        bar.Controls.Add(_idleFrameBox);
        bar.Controls.Add(_applyButton);

        // 按名字找设置页，不按下标：构造期性能页还没插进来，
        // 此时的「设置页下标」与插完之后的下标不是同一个数（按下标取会越界）。
        TabPage? page = FindPage(PanelPages.Settings);
        if (page is null)
        {
            return;
        }

        page.Controls.Add(_settingsStatus);
        page.Controls.Add(bar);
    }

    /// <summary>按页面 id 找那一页（找不到返回 null，不抛）。</summary>
    private TabPage? FindPage(string id)
    {
        foreach (TabPage page in _pages.TabPages)
        {
            if (string.Equals(page.Name, "tab-" + id, StringComparison.Ordinal))
            {
                return page;
            }
        }

        return null;
    }

    /// <summary>
    /// 把设置页的当前值交给宿主侧执行。宿主侧回来说一句，结果显示在内联状态标签上。
    /// 宿主侧没接（null）时如实说「设置功能不可用」，不假装应用成功。
    /// </summary>
    private void ApplySettings()
    {
        if (_settingsCommand is null)
        {
            ShowSettingsStatus("设置功能不可用：宿主未接管设置动作。", ok: false);
            return;
        }

        string? problem = _settingsCommand((double)_idleFrameBox.Value, _startupBox.Checked);
        ShowSettingsStatus(
            problem ?? "设置已保存。",
            ok: problem is null);
    }

    /// <summary>显示内联状态（成功与失败都留在这一页里，不弹模态框）。</summary>
    private void ShowSettingsStatus(string message, bool ok)
    {
        _settingsStatus.Text = message;
        _settingsStatus.ForeColor = ok ? SystemColors.ControlText : Color.FromArgb(0x99, 0x22, 0x22);
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
