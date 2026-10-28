// UI 页面加载与页面命令面（2026-10-28）：UiHost 实现 IUiPageContext。
//
// 为什么接口实现放在宿主而不是面板：面板里的页面插件只能拿到这个口子；
// 判定、弹窗、写盘、授权复核仍在宿主侧。页面重绑走 Changed 事件，不重建整窗。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PetDock.Core.Compositor;
using PetDock.Core.Ui.Design;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost : IUiPageContext
{
    private event EventHandler? UiChanged;

    event EventHandler? IUiPageContext.Changed
    {
        add => UiChanged += value;
        remove => UiChanged -= value;
    }

    UiSnapshot IUiPageContext.Snapshot => Snapshot();

    void IUiPageContext.Refresh() => RefreshUi();

    void IUiPageContext.SetStatus(string text, UiStatusKind kind)
    {
        if (_panel is not null && !_panel.IsDisposed)
        {
            _panel.SetStatus(text, kind);
        }
    }

    void IUiPageContext.ClearStatus()
    {
        if (_panel is not null && !_panel.IsDisposed)
        {
            _panel.ClearStatus();
        }
    }

    bool IUiPageContext.Confirm(string text, string caption) => ConfirmDialog.Ask(text, caption);

    string IUiPageContext.T(string key) => I18n.Get(key);

    string IUiPageContext.T(string key, object arg0) => I18n.Get(key, arg0);

    Task<UiCommandResult> IUiPageContext.ImportPetAsync(string sourceDir) =>
        ImportPetFromPanelAsync(sourceDir);

    string? IUiPageContext.RemovePet(string petId) => RemovePetFromPanel(petId);

    void IUiPageContext.AuthorizePlugin(string pluginId) => AuthorizePlugin(pluginId);

    void IUiPageContext.TogglePluginDisabled(string pluginId) => TogglePluginDisabled(pluginId);

    Task<UiCommandResult> IUiPageContext.InstallPluginAsync(string source, bool enable) =>
        InstallPluginFromPanelAsync(source, enable);

    string? IUiPageContext.RemovePlugin(string pluginId) => RemovePluginFromPanel(pluginId);

    IReadOnlyList<TrashLine> IUiPageContext.TrashPlugins() => TrashPlugins();

    Task<UiCommandResult> IUiPageContext.RestorePluginAsync(string dirName) =>
        RestorePluginFromPanelAsync(dirName);

    Task<UiCommandResult> IUiPageContext.PurgePluginAsync(string dirName) =>
        PurgePluginFromPanelAsync(dirName);

    string? IUiPageContext.ApplySettings(
        double idleFrameBudgetMs,
        bool startWithWindows,
        string material,
        string theme) =>
        ApplySettings(
            idleFrameBudgetMs,
            startWithWindows,
            Theme.ParseMaterial(material),
            Theme.ParseMode(theme));

    string? IUiPageContext.SetLanguage(string language) => SetLanguageFromPanel(language);

    void IUiPageContext.OpenLogs() => RunDiagnostic(ControlPanel.DiagnosticOpOpenLogs);

    void IUiPageContext.ExportDiagnostics(IntPtr owner) => RunDiagnostic(ControlPanel.DiagnosticOpExportBundle);

    void IUiPageContext.OpenAudit() => RunDiagnostic(ControlPanel.DiagnosticOpViewAudit);

    /// <summary>重新取快照并通知页面重绑。页面在 Changed 里读 Snapshot，不缓存旧值。</summary>
    private void RefreshUi()
    {
        UiChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RunDiagnostic(string op)
    {
        (bool _, string message) = RunDiagnosticOp(op);
        if (!string.IsNullOrEmpty(message))
        {
            _panel?.SetStatus(message);
        }
    }

    /// <summary>
    /// 发现界面插件：随包目录优先，开发树兜底。按插件 id 去重（开发树与发行目录同时存在时
    /// 不能让同一页出现两次）。只收 tier 0 且清单声明 pages 的插件（信任边界在目录侧）。
    /// </summary>
    private IReadOnlyList<IUiPage> LoadUiPages()
    {
        var pages = new List<IUiPage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyCollection<string>? disabled = _settings?.Load().DisabledPlugins;
        foreach (string root in UiPluginRoots())
        {
            foreach (IUiPlugin plugin in UiPluginCatalog.Load(root, this, disabled))
            {
                if (!seen.Add(plugin.Id))
                {
                    continue;
                }

                try
                {
                    pages.AddRange(plugin.CreatePages(this));
                }
                catch (Exception error)
                {
                    KernelLog.Write($"ui-plugin-pages-failed plugin={plugin.Id} {error.GetType().Name}: {error.Message}");
                }
            }
        }

        pages.Sort((left, right) =>
        {
            int order = left.Order.CompareTo(right.Order);
            return order != 0 ? order : string.CompareOrdinal(left.Id, right.Id);
        });
        return pages;
    }

    /// <summary>插件根候选：发行布局是 exe/plugins，开发树向上找 packages/plugins。</summary>
    private static IEnumerable<string> UiPluginRoots()
    {
        string baseDir = AppContext.BaseDirectory;
        string packaged = Path.Combine(baseDir, "plugins");
        if (Directory.Exists(packaged))
        {
            yield return packaged;
        }

        DirectoryInfo? cursor = new DirectoryInfo(baseDir);
        for (int depth = 0; depth < 8 && cursor is not null; depth++)
        {
            string candidate = Path.Combine(cursor.FullName, "packages", "plugins");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
                yield break;
            }

            cursor = cursor.Parent;
        }
    }
}
