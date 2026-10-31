// 旧 UI 宿主对新契约（PetDock.App）的实现半边（R1 起）。
//
// 这里只做**翻译**：旧方法的失败形式是「null = 成功，非 null = 一句给人看的话」，
// 契约统一成 AppResult。等 R8 删掉旧层时，这个文件与 UiHost*.cs 一起搬走——
// 那时才真正把实现从旧 UI 里挪出来，现在先把调用面固定住。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PetDock.App;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost : IPetsService, IPluginsService, IMarketService, IUpdateService
{
    AppResult IPetsService.SetOnDesktop(string petId, bool onDesktop) =>
        Translate(SetPetOnDesktopFromPanel(petId, onDesktop));

    AppResult IPetsService.Duplicate(string petId) => Translate(DuplicatePetFromPanel(petId));

    AppResult IPetsService.Delete(string petId) => Translate(DeletePetFromPanel(petId));

    AppResult IPetsService.SetSize(string? petId, int size) => Translate(SetPetSizeFromPanel(petId, size));

    /// <summary>旧层的失败形式是 <c>string?</c>：null = 成功，非 null = 原因。</summary>
    private static AppResult Translate(string? failure) =>
        failure is null ? AppResult.Success() : AppResult.Failure(failure);
    // ── IPluginsService（R1c）──────────────────────────────────────────────

    IReadOnlyList<PluginTrashItem> IPluginsService.Trash() =>
        TrashPlugins().Select(item => new PluginTrashItem(
            item.DirName, item.Id, item.Name, item.Version, item.Bytes, item.RemovedAt, item.Restorable)).ToList();

    AppResult IPluginsService.Remove(string pluginId) => Translate(RemovePluginFromPanel(pluginId));

    async Task<AppResult> IPluginsService.RestoreAsync(string dirName) =>
        Folding(await RestorePluginFromPanelAsync(dirName));

    async Task<AppResult> IPluginsService.PurgeAsync(string dirName) =>
        Folding(await PurgePluginFromPanelAsync(dirName));

    // ── IMarketService（R1e）───────────────────────────────────────────────

    // 市场的两个值类型已经搬进契约（只有 3 个文件引用，搬比复制便宜），所以这里不需要映射。
    Task<MarketResult> IMarketService.LoadAsync() => LoadMarketAsync();

    async Task<AppResult> IMarketService.InstallAsync(string pluginId) =>
        Folding(await InstallFromMarketAsync(pluginId));

    // ── IUpdateService（R1d）───────────────────────────────────────────────

    string? IUpdateService.PendingVersion => ((IUiPageContext)this).PendingUpdateVersion;

    string? IUpdateService.LastReport => UpdateReport;

    async Task<UpdateCheck> IUpdateService.CheckAsync() => FoldingCheck(await CheckUpdateAsync());

    async Task<UpdateInstall> IUpdateService.DownloadAsync() => FoldingInstall(await DownloadUpdateAsync());

    async Task<UpdateInstall> IUpdateService.InstallAsync(bool restartNow) =>
        FoldingInstall(await InstallUpdateAsync(restartNow));

    AppResult IUpdateService.OpenFolder() => Translate(OpenUpdateFolder());

    /// <summary>旧的三态类型 → 契约的类型。只在宿主这一层发生，别处都不许映射。</summary>
    private static UpdateCheck FoldingCheck(UpdateCheckLine line) =>
        new(line.Current, line.Latest, line.Bytes, line.Notes, line.Error);

    private static UpdateInstall FoldingInstall(UpdateInstallLine line) =>
        new(FoldingStage(line.State), line.Message, line.Line is null ? null : FoldingCheck(line.Line));

    private static UpdateInstallStage FoldingStage(UpdateInstallState state) => state switch
    {
        UpdateInstallState.Queued => UpdateInstallStage.Queued,
        UpdateInstallState.Restarting => UpdateInstallStage.Restarting,
        _ => UpdateInstallStage.Failed,
    };

    /// <summary>旧命令的三种结局 → 契约的两种（取消在恢复/清空这两条上走不到，按失败报）。</summary>
    private static AppResult Folding(UiCommandResult result) =>
        result.Ok ? AppResult.Success(result.Message) : AppResult.Failure(result.Message);}
