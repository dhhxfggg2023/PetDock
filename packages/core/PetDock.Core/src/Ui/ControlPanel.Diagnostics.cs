// 设置页的诊断入口（RFC-17 §3.4 / §3.5；docs/18 §2.3 的两行）。
//
// 面板只**发出意图**（与设置页的「应用」同款纪律）：面板不碰文件系统、不碰进程。
// 宿主侧执行完把一句话回来说，面板把它显示成内联状态标签 —— 不弹模态框（docs/18 §5）。
//
// 为什么不弹保存对话框选导出位置：RFC-15 §4 已经登记过一次「自检形态下弹目录选择器
// 会挂住自检进程」。导出路径固定在数据区下、完成后打开所在目录，用户想换地方自己拿。
using System;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;

namespace PetDock.Core.Ui;

internal sealed partial class ControlPanel
{
    /// <summary>「打开日志目录」按钮。</summary>
    private readonly ThemedButton _openLogsButton = new()
    {
        Text = I18n.Get("cp.diag.openLogs"),
        Name = "diag-open-logs",
    };

    /// <summary>「导出诊断包」按钮。</summary>
    private readonly ThemedButton _exportBundleButton = new()
    {
        Text = I18n.Get("cp.diag.exportBundle"),
        Name = "diag-export-bundle",
    };

    /// <summary>「查看审计流水」按钮（docs/18 §2.8）。</summary>
    private readonly ThemedButton _auditButton = new()
    {
        Text = I18n.Get("cp.diag.viewAudit"),
        Name = "diag-view-audit",
    };

    /// <summary>诊断动作的回调（宿主侧执行）。</summary>
    private DiagnosticCommand? _diagnosticCommand;

    /// <summary>诊断动作的回调形状：给一个动作名，宿主侧回成败与一句话（失败原因是文案）。</summary>
    internal delegate (bool Ok, string Message) DiagnosticCommand(string op);

    /// <summary>设置页诊断入口的控件名（自检按它们断言入口存在）。</summary>
    public IReadOnlyList<string> DiagnosticControlNames =>
        new[] { _openLogsButton.Name, _exportBundleButton.Name, _auditButton.Name };

    /// <summary>诊断动作名（自检按它调宿主侧回调，不靠模拟点击）。</summary>
    public const string DiagnosticOpOpenLogs = "open-logs";

    /// <summary>诊断动作名：导出诊断包。</summary>
    public const string DiagnosticOpExportBundle = "export-bundle";

    /// <summary>诊断动作名：打开审计流水的只读视图。</summary>
    public const string DiagnosticOpViewAudit = "view-audit";

    /// <summary>设置页诊断两行的行名**键**（判据断键：文案在 assets/i18n，会跟着语言变）。</summary>
    public const string DiagnosticLogsRow = "cp.settings.row.diagnosticLogs", DiagnosticBundleRow = "cp.settings.row.diagnosticBundle";

    /// <summary>给设置页装一个宿主侧的诊断回调（构造时传入）。</summary>
    public void SetDiagnosticCommand(DiagnosticCommand? command) => _diagnosticCommand = command;

    /// <summary>
    /// 设置页列表当前所有行的文字（自检断言诊断两行不再是「(未接线)」）。
    /// 走的是**共享列表**（面板四页共用一个 ListView），所以自检要先切到设置页。
    /// </summary>
    public IReadOnlyList<string> SettingsRowTexts
    {
        get
        {
            var texts = new List<string>();
            foreach (ListViewItem item in _list.Items)
            {
                var cells = new List<string>();
                foreach (ListViewItem.ListViewSubItem sub in item.SubItems)
                {
                    cells.Add(sub.Text);
                }

                texts.Add(string.Join(" | ", cells));
            }

            return texts;
        }
    }

    /// <summary>
    /// 自检入口：直接走宿主侧回调（与点按钮同一条路），
    /// 区别只是不经过按钮事件 —— 自检不该靠模拟点击来证明功能。
    /// </summary>
    internal string TryDiagnosticForCheck(string op) => RunDiagnostic(op).Message;

    /// <summary>三个诊断入口的点击接线；摆放交给设置页的表单网格（一页一处布局）。</summary>
    private void InitDiagnosticsPage()
    {
        _openLogsButton.Click += (_, _) => RunDiagnostic(DiagnosticOpOpenLogs);
        _exportBundleButton.Click += (_, _) => RunDiagnostic(DiagnosticOpExportBundle);
        _auditButton.Click += (_, _) => RunDiagnostic(DiagnosticOpViewAudit);

        _openLogsButton.FitToText();
        _exportBundleButton.FitToText();
        _auditButton.FitToText();
    }

    /// <summary>
    /// 执行一个诊断动作并把结果显示成内联状态。
    /// 成败取回调给的布尔，**不解析**刚生成的文案：文案会被翻译，解析字面量会静默恒真（RFC-42 §3.4）。
    /// </summary>
    private (bool Ok, string Message) RunDiagnostic(string op)
    {
        (bool Ok, string Message) outcome;
        if (_diagnosticCommand is null)
        {
            outcome = (false, I18n.Get("cp.diag.unavailable"));
        }
        else
        {
            try
            {
                outcome = _diagnosticCommand(op);
            }
            catch (Exception error)
            {
                outcome = (false, I18n.Get("cp.diag.failed") + error.GetType().Name + " " + error.Message);
            }
        }

        ShowSettingsStatus(outcome.Message, outcome.Ok);
        return outcome;
    }
}
