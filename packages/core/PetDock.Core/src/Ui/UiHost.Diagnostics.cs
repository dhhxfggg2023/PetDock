// 宿主侧的诊断接线（RFC-17 §3.4）：设置页的两个入口 → 宿主侧执行体。
//
// 面板只发意图（原规格 18 §2.4「面板只读展示」在这里仍成立）：
// 判定、落盘、打开目录都在这一侧完成，完成后把一句话回给面板。
using System;
using PetDock.App;
using PetDock.Core.App;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>诊断服务（可空：没有数据区就没有诊断入口；自检形态常常是 null）。</summary>
    private DiagnosticService? _diagnostics;

    private readonly IDiagnosticsService? _diagOps;

    /// <summary>诊断服务（自检与宿主侧用）。</summary>
    public DiagnosticService? Diagnostics => _diagnostics;

    /// <summary>
    /// 诊断在新契约下的口子（R1）。旧面板暂时仍从 <see cref="RunDiagnosticOp"/> 进来，
    /// 但「打开日志 / 导出诊断包」两条已经只经这个接口执行 —— 新界面层将来直接用它。
    /// </summary>
    internal IDiagnosticsService? DiagnosticOps => _diagOps;

    /// <summary>
    /// 设置页两个诊断入口的宿主侧执行体（UI 线程）。
    /// 返回成败与一句话：成功路径或失败原因，面板把它显示成内联状态（成败不看文案）。
    /// </summary>
    private (bool Ok, string Message) RunDiagnosticOp(string op)
    {
        if (_diagnostics is null)
        {
            return (false, I18n.Get("ui.diag.unavailable"));
        }

        try
        {
            // 两条纯服务操作一律走新契约：旧面板只是「另一个调用方」，
            // 新界面层将来直接拿 DiagnosticOps，不经过这里的 op 字符串分派。
            if (string.Equals(op, ControlPanel.DiagnosticOpOpenLogs, StringComparison.Ordinal))
            {
                return Unpack(_diagOps!.OpenLogs());
            }

            if (string.Equals(op, ControlPanel.DiagnosticOpExportBundle, StringComparison.Ordinal))
            {
                return Unpack(_diagOps!.ExportBundle());
            }

            if (string.Equals(op, ControlPanel.DiagnosticOpViewAudit, StringComparison.Ordinal))
            {
                return OpenAuditWindow();
            }

            return (false, I18n.Get("ui.diag.unknown", op));
        }
        catch (Exception error)
        {
            KernelLog.Write($"diagnostic-failed op={op} {error.GetType().Name}: {error.Message}");
            return (false, I18n.Get("ui.diag.failed", error.GetType().Name, error.Message));
        }
    }

    private static (bool Ok, string Message) Unpack(AppResult result) => (result.Ok, result.Message);

    /// <summary>
    /// 审计流水的只读视图（原规格 18 §2.8）。读盘在这里，窗口只负责列。
    /// 非模态：它是「看一眼」的窗，不该把面板和宠物一起冻住（Exit 确认那种才需要模态）。
    /// </summary>
    private (bool Ok, string Message) OpenAuditWindow()
    {
        AuditTrailRead read = AuditTrail.ReadAll(_diagnostics!.AuditDirectory);
        if (!read.Available)
        {
            return (false, read.Note);
        }

        AuditWindow window = AuditWindow.Create(read);
        window.FormClosed += (_, _) => window.Dispose();
        window.Show(_panel);

        return read.Rows.Count == 0
            ? (true, I18n.Get("cp.audit.empty"))
            : (true, I18n.Get("cp.audit.summary", read.Rows.Count, read.Rows.Count)
                + I18n.Get("cp.audit.files", read.Files));
    }
}
