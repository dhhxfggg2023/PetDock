// 界面判据的预览上下文（2026-10-28）：页面插件只拿 IUiPageContext，自检就伪造这一层。
// 它不写盘、不弹窗、不碰宿主状态 —— 判据要验的是「壳把页面摆对、命令面接上」，
// 而不是把宿主再测一遍。
using System;
using System.Collections.Generic;
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

    public void SetStatus(string text) => LastStatus = text;

    public string T(string key) => I18n.Get(key);

    public string T(string key, object arg0) => I18n.Get(key, arg0);

    public bool ImportPet(string sourceDir, out string message)
    {
        Commands.Add("import:" + sourceDir);
        message = string.Empty;
        return true;
    }

    public string? RemovePet(string petId)
    {
        Commands.Add("remove-pet:" + petId);
        return null;
    }

    public void AuthorizePlugin(string pluginId) => Commands.Add("authorize:" + pluginId);

    public void TogglePluginDisabled(string pluginId) => Commands.Add("toggle:" + pluginId);

    public string? InstallPlugin(string source, bool enable)
    {
        Commands.Add("install:" + source + ":" + enable);
        return null;
    }

    public string? RemovePlugin(string pluginId)
    {
        Commands.Add("remove-plugin:" + pluginId);
        return null;
    }

    public string? ApplySettings(double idleFrameBudgetMs, bool startWithWindows, string material, string theme)
    {
        Commands.Add("settings:" + idleFrameBudgetMs.ToString("0.###") + ":" + startWithWindows + ":" + material + ":" + theme);
        return null;
    }

    public void OpenLogs() => Commands.Add("logs");

    public void ExportDiagnostics(IntPtr owner) => Commands.Add("export");

    public void OpenAudit() => Commands.Add("audit");
}
