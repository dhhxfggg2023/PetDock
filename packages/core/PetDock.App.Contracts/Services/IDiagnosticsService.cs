namespace PetDock.App;

/// <summary>
/// 诊断：打开日志目录、导出诊断包。两条都是纯服务 —— **不弹窗、不写状态栏**，
/// 「怎么把结果说给用户」由界面层决定。
/// </summary>
public interface IDiagnosticsService
{
    /// <summary>在资源管理器里打开日志目录。</summary>
    AppResult OpenLogs();

    /// <summary>导出诊断包（含日志与审计流水）。</summary>
    AppResult ExportBundle();
}
