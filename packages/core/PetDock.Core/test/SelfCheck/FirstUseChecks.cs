// 首次使用闭环的界面判据（2026-10-28 第五轮）：插件列表空状态、审计流水的真实入口。
// 与 UiWorkspaceChecks 分开：那个文件贴着 D-03c 的 400 行上限，这里的判据属于「首次使用」而不是壳本身。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class FirstUseChecks
{
    public static void Run(Report check)
    {
        string? root = PluginHost.ProbePluginsRoot();
        if (root is null)
        {
            check("ui-plugins-empty-state", false, "root=missing");
            check("ui-settings-audit-entry", false, "root=missing");
            return;
        }

        try
        {
            var context = new PreviewUiContext();
            IReadOnlyList<IUiPage> pages = UiPluginCatalog.Load(root, context)
                .SelectMany(plugin => plugin.CreatePages(context))
                .ToArray();

            IUiPage? plugins = pages.FirstOrDefault(page => string.Equals(page.Id, "plugins", StringComparison.Ordinal));
            FrameworkElement? pluginsView = plugins?.CreateView();
            var empty = pluginsView?.FindName("EmptyText") as System.Windows.Controls.TextBlock;
            var rows = pluginsView?.FindName("Rows") as System.Windows.Controls.ListBox;
            bool emptyShown = empty?.Visibility == Visibility.Visible
                && rows?.Visibility == Visibility.Collapsed
                && empty.Text.Length > 0;
            check("ui-plugins-empty-state", emptyShown,
                $"empty={empty?.Visibility} rows={rows?.Visibility} text={empty?.Text}");

            IUiPage? settings = pages.FirstOrDefault(page => string.Equals(page.Id, "settings", StringComparison.Ordinal));
            FrameworkElement? settingsView = settings?.CreateView();
            var audit = settingsView?.FindName("AuditButton") as System.Windows.Controls.Button;
            audit?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            bool auditWired = context.Commands.Contains("audit");
            check("ui-settings-audit-entry", auditWired && audit?.Content?.ToString()?.Length > 0,
                $"wired={auditWired} content={audit?.Content}");
        }
        catch (Exception error)
        {
            check("ui-plugins-empty-state", false, error.GetType().Name + ": " + error.Message);
            check("ui-settings-audit-entry", false, error.GetType().Name + ": " + error.Message);
        }
    }
}
