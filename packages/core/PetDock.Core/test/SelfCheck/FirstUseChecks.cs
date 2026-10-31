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
    public static async void Run(Report check)
    {
        string? root = PluginHost.ProbePluginsRoot();
        if (root is null)
        {
            check("ui-plugins-empty-state", false, "root=missing");
            check("ui-settings-audit-entry", false, "root=missing");
            check("ui-settings-pet-card", false, "root=missing");
            check("ui-settings-pet-size", false, "root=missing");
            check("ui-settings-a11y", false, "root=missing");
            check("ui-settings-update-entry", false, "root=missing");
            check("ui-settings-update-offline-honest", false, "root=missing");
            check("ui-settings-update-two-timings", false, "root=missing");
            check("ui-settings-update-later-is-not-restart", false, "root=missing");
            check("ui-pets-duplicate-delete", false, "root=missing");
            check("ui-pets-per-pet-size", false, "root=missing");
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
            // 检查更新（2026-10-31）：按钮必须真的把命令发出去，且三种结局分别显示。
            var updateCheck = settingsView?.FindName("UpdateCheckButton") as System.Windows.Controls.Button;
            var updateStatus = settingsView?.FindName("UpdateStatus") as TextBlock;
            updateCheck?.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, updateCheck));
            bool updateWired = context.Commands.Contains("check-update")
                && updateCheck?.Content?.ToString()?.Length > 0;
            check("ui-settings-update-entry", updateWired,
                $"wired={updateWired} content={updateCheck?.Content} commands={string.Join(",", context.Commands)}");

            // 「没查到」不许被说成「已是最新」。
            //
            // 判的是**渲染出来的那句话是哪一个键**，不是「句子里不许出现这几个字」：
            // 这条文案本身就写着「这不等于已是最新」，按字面判会把它自己判红
            // （2026-10-31 第一版判据就是这么红了一次 —— 判据写错和代码写错一样危险）。
            // 所以这里比的是「它用的是 failedShort 那一句，而不是 current 那一句」。
            await System.Threading.Tasks.Task.Yield();
            string failedPrefix = I18n.Get("cp.settings.update.failedShort", "update-offline");
            string currentPrefix = I18n.Get("cp.settings.update.current", "0.0.0");
            bool honestFailure = updateStatus is not null
                && updateStatus.Text.StartsWith(
                    failedPrefix.Substring(0, Math.Min(8, failedPrefix.Length)), StringComparison.Ordinal)
                && !updateStatus.Text.StartsWith(
                    currentPrefix.Substring(0, Math.Min(8, currentPrefix.Length)), StringComparison.Ordinal);
            check("ui-settings-update-offline-honest", honestFailure,
                $"status={updateStatus?.Text} uses-failed={updateStatus?.Text.StartsWith(failedPrefix.Substring(0, Math.Min(8, failedPrefix.Length)), StringComparison.Ordinal)}");

            // 点「下载新版本」之后必须同时出现两个**时机**按钮（2026-10-31 用户要求
            // 「下载完选择重启更新或者稍后更新」）：
            // 只有「立即重启」= 用户没法等到下次启动；
            // 只有「稍后」= 用户想马上换也做不到。
            var updateDownload = settingsView?.FindName("UpdateDownloadButton") as System.Windows.Controls.Button;
            var updateRestart = settingsView?.FindName("UpdateRestartButton") as System.Windows.Controls.Button;
            var updateLater = settingsView?.FindName("UpdateLaterButton") as System.Windows.Controls.Button;

            context.UpdateResult = new UpdateCheckLine("0.0.1", "9.9.9", 0, string.Empty, null);
            updateCheck?.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, updateCheck));
            await System.Threading.Tasks.Task.Yield();
            updateDownload?.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, updateDownload));
            await System.Threading.Tasks.Task.Yield();

            bool bothTimings = updateRestart?.Visibility == Visibility.Visible
                && updateLater?.Visibility == Visibility.Visible
                && updateRestart.Content?.ToString()?.Length > 0
                && updateLater.Content?.ToString()?.Length > 0;
            check("ui-settings-update-two-timings", bothTimings,
                $"restart={(updateRestart?.Visibility.ToString() ?? "null")}/{updateRestart?.Content}"
                + $" later={(updateLater?.Visibility.ToString() ?? "null")}/{updateLater?.Content}");

            // 「稍后更新」点下去要发命令，而且**不是**立即重启那一条 ——
            // 两个按钮走错命令的话，用户点了「稍后」程序当场重启，那是最刺眼的一种错。
            int beforeLater = context.Commands.Count;
            updateLater?.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, updateLater));
            await System.Threading.Tasks.Task.Yield();
            bool laterWired = context.Commands.Contains("later-update")
                && !context.Commands.Skip(beforeLater).Contains("restart-update");
            check("ui-settings-update-later-is-not-restart", laterWired,
                $"late={string.Join(",", context.Commands.Skip(beforeLater))}");

            // 「稍后更新」写下之后，用户关掉面板、过几天再打开：那一刻页面必须**还看得见**
            // 「已登记，下次启动安装」。判的是事实来源 —— 页面的记忆会丢，磁盘上的那一份不会。
            var restoredContext = new PreviewUiContext(context.Snapshot) { PendingVersion = "9.9.9" };
            IUiPage? loaded = UiPluginCatalog.Load(root, restoredContext)
                .SelectMany(plugin => plugin.CreatePages(restoredContext))
                .FirstOrDefault(page => string.Equals(page.Id, "settings", StringComparison.Ordinal));
            IUiPage? restoredSettings = loaded
                ?? pages.FirstOrDefault(page => string.Equals(page.Id, "settings", StringComparison.Ordinal));
            var restoredView = restoredSettings?.CreateView() as System.Windows.Controls.UserControl;
            restoredView?.Measure(new System.Windows.Size(1000, 2000));
            restoredView?.Arrange(new System.Windows.Rect(0, 0, 1000, 2000));
            restoredView?.UpdateLayout();
            var restoredRestart = restoredView?.FindName("UpdateRestartButton") as System.Windows.Controls.Button;
            var restoredLater = restoredView?.FindName("UpdateLaterButton") as System.Windows.Controls.Button;
            var restoredStatus = restoredView?.FindName("UpdateStatus") as TextBlock;
            bool restored = restoredRestart?.Visibility == Visibility.Visible
                && restoredLater?.Visibility == Visibility.Visible
                && (restoredStatus?.Text?.Contains("9.9.9", StringComparison.Ordinal) ?? false);
            check("ui-settings-update-pending-restored", restored,
                $"restart={restoredRestart?.Visibility} later={restoredLater?.Visibility} status={restoredStatus?.Text}");

            var petContext = new PreviewUiContext(new UiSnapshot(
                Array.Empty<PetLine>(),
                Array.Empty<PluginLine>(),
                new SettingsView(1000.0 / 30.0, "zh-CN", false, "system", "opaque", 160, "示例桌宠", "9.9.9")));
            IUiPage? petSettings = UiPluginCatalog.Load(root, petContext)
                .SelectMany(plugin => plugin.CreatePages(petContext))
                .FirstOrDefault(page => string.Equals(page.Id, "settings", StringComparison.Ordinal));
            FrameworkElement? petView = petSettings?.CreateView();
            var petNameText = petView?.FindName("PetNameText") as TextBlock;
            var aboutVersion = petView?.FindName("AboutVersionText") as TextBlock;
            var petSize = petView?.FindName("PetSizeSlider") as Slider;
            var petSizeValue = petView?.FindName("PetSizeValue") as TextBlock;
            bool petCard = petNameText?.Text == "示例桌宠" && aboutVersion?.Text == "9.9.9"
                && petSize?.Value == 160
                && petSizeValue?.Text.Contains("160", StringComparison.Ordinal) == true;
            check("ui-settings-pet-card", petCard,
                $"name={petNameText?.Text} version={aboutVersion?.Text} size={petSize?.Value} value={petSizeValue?.Text}");

            if (petSize is not null)
            {
                petSize.Value = 96;
            }
            // 2026-10-31：尺寸改成逐只 —— 没有选中任何一只时命令带的是 (default)。
            bool sizeWired = petContext.Commands.Contains("pet-size:(default):96")
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

            // 每只单独调大小（2026-10-31）：选中一行之后，行上的滑块必须带出**这一只**的
            // 当前边长，并且拖它发出去的命令带着**这一行**的 id —— 不带 id 就又变回"改全部"。
            var sizeSlider = petsView?.FindName("DetailSizeSlider") as Slider;
            if (sizeSlider is not null)
            {
                sizeSlider.Value = 200;
            }

            bool perPetSize = sizeSlider is not null
                && petsContext.Commands.Contains("pet-size:user.alpha:200");
            check("ui-pets-per-pet-size", perPetSize,
                $"slider={sizeSlider?.Value} commands={string.Join(",", petsContext.Commands)}");

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
            check("ui-settings-update-entry", false, error.GetType().Name + ": " + error.Message);
            check("ui-settings-update-offline-honest", false, error.GetType().Name + ": " + error.Message);
            check("ui-settings-update-two-timings", false, error.GetType().Name + ": " + error.Message);
            check("ui-settings-update-later-is-not-restart", false, error.GetType().Name + ": " + error.Message);
            check("ui-pets-duplicate-delete", false, error.GetType().Name + ": " + error.Message);
            check("ui-pets-detail", false, error.GetType().Name + ": " + error.Message);
            check("ui-pets-per-pet-size", false, error.GetType().Name + ": " + error.Message);
        }
    }
}
