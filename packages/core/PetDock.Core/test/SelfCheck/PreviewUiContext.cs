// 界面判据的预览上下文（2026-10-28）：页面插件只拿 IUiPageContext，自检就伪造这一层。
// 它不写盘、不弹窗、不碰宿主状态 —— 判据要验的是「壳把页面摆对、命令面接上」，
// 而不是把宿主再测一遍。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal sealed class PreviewUiContext : IUiPageContext
{
    private UiSnapshot _snapshot;

    public PreviewUiContext(UiSnapshot? snapshot = null) => _snapshot = snapshot ?? UiSnapshot.Empty;

    public UiSnapshot Snapshot => _snapshot;

    public event EventHandler? Changed;

    public int RefreshCount { get; private set; }

    public string LastStatus { get; private set; } = string.Empty;

    /// <summary>最后一次状态的口吻（判据用它区分「装好了」和「没装上」）。</summary>
    public UiStatusKind LastStatusKind { get; private set; } = UiStatusKind.Info;

    public List<string> Commands { get; } = new();

    public void SetSnapshot(UiSnapshot snapshot)
    {
        _snapshot = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh()
    {
        RefreshCount++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetStatus(string text, UiStatusKind kind = UiStatusKind.Info)
    {
        LastStatus = text;
        LastStatusKind = kind;
    }

    public void ClearStatus()
    {
        LastStatus = string.Empty;
        LastStatusKind = UiStatusKind.Info;
    }

    /// <summary>确认框的应答（判据默认放行；要验「取消路径」就把它设成 false）。</summary>
    public bool ConfirmAnswer { get; set; } = true;

    /// <summary>最后一次确认框的标题（判据断言页面确实问了，而不是直接动手）。</summary>
    public string LastConfirmCaption { get; private set; } = string.Empty;

    public bool Confirm(string text, string caption)
    {
        LastConfirmCaption = caption;
        Commands.Add("confirm:" + caption);
        return ConfirmAnswer;
    }

    public string T(string key) => I18n.Get(key);

    public string T(string key, object arg0) => I18n.Get(key, arg0);

    public Task<UiCommandResult> ImportPetAsync(string sourceDir)
    {
        Commands.Add("import:" + sourceDir);
        return Task.FromResult(UiCommandResult.Success());
    }

    public string? RemovePet(string petId)
    {
        Commands.Add("remove-pet:" + petId);
        return null;
    }

    public string? DuplicatePet(string petId)
    {
        Commands.Add("duplicate-pet:" + petId);
        return null;
    }

    public string? DeletePet(string petId)
    {
        Commands.Add("delete-pet:" + petId);
        return null;
    }

    public void AuthorizePlugin(string pluginId) => Commands.Add("authorize:" + pluginId);

    public void TogglePluginDisabled(string pluginId) => Commands.Add("toggle:" + pluginId);

    public Task<UiCommandResult> InstallPluginAsync(string source, bool enable)
    {
        Commands.Add("install:" + source + ":" + enable);
        return Task.FromResult(UiCommandResult.Success());
    }

    public string? RemovePlugin(string pluginId)
    {
        Commands.Add("remove-plugin:" + pluginId);
        return null;
    }

    /// <summary>判据可注入的回收站内容（默认空）。</summary>
    public List<TrashLine> Trash { get; } = new();

    public IReadOnlyList<TrashLine> TrashPlugins() => Trash;

    public Task<UiCommandResult> RestorePluginAsync(string dirName)
    {
        Commands.Add("restore-plugin:" + dirName);
        return Task.FromResult(UiCommandResult.Success());
    }

    public Task<UiCommandResult> PurgePluginAsync(string dirName)
    {
        Commands.Add("purge-plugin:" + dirName);
        return Task.FromResult(UiCommandResult.Success());
    }

    public string? ApplySettings(double idleFrameBudgetMs, bool startWithWindows, string material, string theme)
    {
        Commands.Add("settings:" + idleFrameBudgetMs.ToString("0.###") + ":" + startWithWindows + ":" + material + ":" + theme);
        return null;
    }

    public string? SetPetSize(int size)
    {
        Commands.Add("pet-size:" + size);
        return null;
    }

    public string? SetLanguage(string language)
    {
        Commands.Add("language:" + language);
        return null;
    }

    public void OpenLogs() => Commands.Add("logs");

    public void ExportDiagnostics(IntPtr owner) => Commands.Add("export");

    public void OpenAudit() => Commands.Add("audit");
}
