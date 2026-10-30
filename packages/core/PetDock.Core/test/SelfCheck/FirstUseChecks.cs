// 首次使用闭环的界面判据（2026-10-28 第五轮）：插件列表空状态、审计流水的真实入口。
// 与 UiWorkspaceChecks 分开：那个文件贴着 D-03c 的 400 行上限，这里的判据属于「首次使用」而不是壳本身。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
            check("ui-settings-pet-card", false, "root=missing");
            check("ui-settings-pet-size", false, "root=missing");
            check("ui-settings-a11y", false, "root=missing");
            check("ui-pets-duplicate-delete", false, "root=missing");
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

            // 宠物/关于两块：显示名与版本来自快照，不是写死的占位符。
            var petContext = new PreviewUiContext(new UiSnapshot(
                Array.Empty<PetLine>(),
                Array.Empty<PluginLine>(),
                new SettingsView(1000.0 / 30.0, "zh-CN", false, "system", "opaque", 160, "守岸人", "9.9.9")));
            IUiPage? petSettings = UiPluginCatalog.Load(root, petContext)
                .SelectMany(plugin => plugin.CreatePages(petContext))
                .FirstOrDefault(page => string.Equals(page.Id, "settings", StringComparison.Ordinal));
            FrameworkElement? petView = petSettings?.CreateView();
            var petNameText = petView?.FindName("PetNameText") as TextBlock;
            var aboutVersion = petView?.FindName("AboutVersionText") as TextBlock;
            var petSize = petView?.FindName("PetSizeSlider") as Slider;
            var petSizeValue = petView?.FindName("PetSizeValue") as TextBlock;
            bool petCard = petNameText?.Text == "守岸人" && aboutVersion?.Text == "9.9.9"
                && petSize?.Value == 160
                && petSizeValue?.Text.Contains("160", StringComparison.Ordinal) == true;
            check("ui-settings-pet-card", petCard,
                $"name={petNameText?.Text} version={aboutVersion?.Text} size={petSize?.Value} value={petSizeValue?.Text}");

            if (petSize is not null)
            {
                petSize.Value = 96;
            }
            bool sizeWired = petContext.Commands.Contains("pet-size:96")
                && petSizeValue?.Text.Contains("96", StringComparison.Ordinal) == true;
            check("ui-settings-pet-size", sizeWired,
                $"size={petSize?.Value} value={petSizeValue?.Text} commands={string.Join(",", petContext.Commands)}");

            bool a11y = petSize is not null
                && !string.IsNullOrEmpty(AutomationProperties.GetName(petSize))
                && petSizeValue is not null
                && !string.IsNullOrEmpty(petSizeValue.Text);
            check("ui-settings-a11y", a11y,
                $"sizeName={AutomationProperties.GetName(petSize!)} value={petSizeValue?.Text}");

            // 宠物页：选中一行之后「多开一只 / 删除选中」要能点，并且命令真的带着这一行的 id。
            var petsContext = new PreviewUiContext(new UiSnapshot(
                new[]
                {
                    new PetLine("user.alpha", "Alpha", "已导入", "运行中", "1 MB")
                    {
                        Author = "Alice",
                        License = "CC0-1.0",
                        Path = "C:\\packs\\alpha",
                    },
                },
                Array.Empty<PluginLine>(),
                SettingsView.Default));
            IUiPage? petsPage = UiPluginCatalog.Load(root, petsContext)
                .SelectMany(plugin => plugin.CreatePages(petsContext))
                .FirstOrDefault(page => string.Equals(page.Id, "pets", StringComparison.Ordinal));
            FrameworkElement? petsView = petsPage?.CreateView();
            var petRows = petsView?.FindName("Rows") as System.Windows.Controls.ListBox;
            var duplicate = petsView?.FindName("DuplicateButton") as System.Windows.Controls.Button;
            var delete = petsView?.FindName("DeleteButton") as System.Windows.Controls.Button;
            if (petRows is not null)
            {
                petRows.SelectedIndex = 0;
            }

            bool selected = duplicate?.IsEnabled == true && delete?.IsEnabled == true;
            if (duplicate is not null)
            {
                duplicate.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent, duplicate));
            }

            if (delete is not null)
            {
                delete.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent, delete));
            }

            bool commandsWired = selected
                && petsContext.Commands.Contains("duplicate-pet:user.alpha")
                && petsContext.Commands.Contains("delete-pet:user.alpha");
            check("ui-pets-duplicate-delete", commandsWired,
                $"selected={selected} commands={string.Join(",", petsContext.Commands)}");

            var detailAuthor = petsView?.FindName("DetailAuthor") as TextBlock;
            var detailLicense = petsView?.FindName("DetailLicense") as TextBlock;
            bool detail = detailAuthor?.Text.Contains("Alice", StringComparison.Ordinal) == true
                && detailLicense?.Text.Contains("CC0-1.0", StringComparison.Ordinal) == true;
            check("ui-pets-detail", detail,
                $"author={detailAuthor?.Text} license={detailLicense?.Text}");
        }
        catch (Exception error)
        {
            check("ui-plugins-empty-state", false, error.GetType().Name + ": " + error.Message);
            check("ui-settings-audit-entry", false, error.GetType().Name + ": " + error.Message);
            check("ui-settings-pet-card", false, error.GetType().Name + ": " + error.Message);
            check("ui-settings-pet-size", false, error.GetType().Name + ": " + error.Message);
            check("ui-settings-a11y", false, error.GetType().Name + ": " + error.Message);
            check("ui-pets-duplicate-delete", false, error.GetType().Name + ": " + error.Message);
            check("ui-pets-detail", false, error.GetType().Name + ": " + error.Message);
        }
    }
}
