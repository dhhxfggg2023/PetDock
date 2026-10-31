// 诊断服务在新契约（PetDock.App.IDiagnosticsService）下的实现（R1 起）。
//
// 只是一个转接层：副作用仍在 Host/DiagnosticService.cs。放在这里是为了让「新界面层只认契约」
// 这件事成立 —— 新 Ui 不引用 PetDock.Hosting，也不需要知道诊断包是怎么打的。
using PetDock.App;
using PetDock.Hosting;

namespace PetDock.Core.App;

internal sealed class DiagnosticsOps : IDiagnosticsService
{
    private readonly DiagnosticService _inner;

    internal DiagnosticsOps(DiagnosticService inner) => _inner = inner;

    public AppResult OpenLogs()
    {
        (bool ok, string message) = _inner.OpenLogsDirectory();
        return new AppResult(ok, message);
    }

    public AppResult ExportBundle()
    {
        (bool ok, string message) = _inner.ExportBundle();
        return new AppResult(ok, message);
    }
}
