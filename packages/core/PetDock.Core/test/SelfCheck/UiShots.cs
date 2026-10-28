// 界面截图工件（2026-10-28 WPF 重构）：不再由测试代码搭一棵假界面树，而是
// 走生产路径 UiPluginCatalog.Load 找到真实界面插件，用预览上下文提供只读快照，
// 然后把壳渲染成 PNG。这样截图拍到的就是用户会看到的页面。
//
// 口径：--uitest --shots <目录>；96dpi 与 144dpi 各一套、浅色与深色各一套。
// 渲染不依赖窗口显示（无前台、锁屏也能跑），所以不再有旧的 SHOT-SKIP 分支。
//
// 引导窗是 WinForms 模态框，WPF 的壳截图拍不到它（2026-10-28：改引导文案的判据只看得到字，
// 看不到样子，而「留太多空白会读成界面没画完」恰恰只有看图才发现）。所以它单独走 DrawToBitmap。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PetDock.Core.Ui;
using PetDock.Core.Ui.Design;
using Size = System.Windows.Size;

namespace PetDock.Hosting;

internal static class UiShots
{
    private const int Width = 1040;
    private const int Height = 680;

    public static int Capture(string dir, LineSink emit)
    {
        // 截图走的是页面插件自己的本地化；不先加载文案表，拍到的全是键名。
        I18n.Load(I18n.DefaultLanguage);
        Directory.CreateDirectory(dir);
        string? root = FindDevPluginRoot();
        if (root is null)
        {
            emit("SHOT-SKIP reason=no-ui-plugin-root");
            return 0;
        }

        ThemeMode original = Theme.Mode;
        int count = 0;
        try
        {
            foreach (bool light in new[] { true, false })
            {
                Theme.Mode = light ? ThemeMode.Light : ThemeMode.Dark;
                var context = new PreviewUiContext(SampleSnapshot());
                IReadOnlyList<IUiPlugin> plugins = UiPluginCatalog.Load(root, context);
                IUiPage[] pages = plugins
                    .SelectMany(plugin => plugin.CreatePages(context))
                    .OrderBy(page => page.Order)
                    .ThenBy(page => page.Id, StringComparer.Ordinal)
                    .ToArray();

                if (pages.Length == 0)
                {
                    emit("SHOT-SKIP reason=no-ui-pages root=" + root);
                    return 0;
                }

                using var panel = new ControlPanel(context, pages, showWindow: false);
                foreach (IUiPage page in pages)
                {
                    panel.ShowPage(page.Id);
                    foreach (int dpi in new[] { 96, 144 })
                    {
                        string path = Path.Combine(
                            dir,
                            "panel-" + page.Id + "@" + dpi.ToString() + (light ? "-light" : "-dark") + ".png");
                        Capture(panel.Workspace, path, dpi);
                        count++;
                    }
                }

                // 长任务进度条的样子：确定态 + 取消按钮。只在宠物页上拍，证明它不是浮在空中的控件。
                panel.ShowPage("pets");
                foreach (int dpi in new[] { 96, 144 })
                {
                    string progressText = I18n.Get("ui.work.copyProgress", "42.0", "100.0");
                    panel.Workspace.BeginWork(progressText, () => { });
                    panel.Workspace.SetWorkProgress(0.42, progressText);
                    string path = Path.Combine(
                        dir,
                        "panel-progress@" + dpi.ToString() + (light ? "-light" : "-dark") + ".png");
                    Capture(panel.Workspace, path, dpi);
                    panel.Workspace.EndWork();
                    count++;
                }
            }
        }
        catch (Exception error)
        {
            emit("SHOT-FAIL " + error);
        }
        finally
        {
            Theme.Mode = original;
        }

        count += CaptureOnboarding(dir, emit);
        emit("SHOT-OK n=" + count + " dir=" + dir);
        return count;
    }

    /// <summary>引导窗逐档 DPI 拍一张（窗在构造时按 DPI 定型，所以每档重建一次）。</summary>
    private static int CaptureOnboarding(string dir, LineSink emit)
    {
        int count = 0;
        int savedDpi = Theme.SimulatedDpi;
        try
        {
            foreach (int dpi in new[] { 96, 144 })
            {
                Theme.SetSimulatedDpi(dpi);
                using var window = new OnboardingWindow(WriteNothing, hasPet: true);

                // DrawToBitmap 只画「已经建过窗、画过一次」的控件树：没显示过的窗拍出来是空白
                // （2026-10-28 实测）。所以摆到屏幕外显示一次再拍，用户看不到它。
                window.StartPosition = FormStartPosition.Manual;
                window.Location = new System.Drawing.Point(-32000, -32000);
                window.Show();
                System.Windows.Forms.Application.DoEvents();
                window.PerformLayout();
                try
                {
                    using var bitmap = new System.Drawing.Bitmap(window.Width, window.Height);
                    window.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, window.Width, window.Height));
                    string name = "onboarding@" + dpi.ToString() + ".png";
                    bitmap.Save(Path.Combine(dir, name), System.Drawing.Imaging.ImageFormat.Png);
                    emit("SHOT " + name + " " + bitmap.Width + "x" + bitmap.Height);
                    count++;
                }
                finally
                {
                    window.Hide();
                }
            }
        }
        catch (Exception error)
        {
            emit("SHOT-FAIL onboarding " + error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            Theme.SetSimulatedDpi(savedDpi);
        }

        return count;
    }

    /// <summary>截图用的写设置回调：不落盘（截图不该改这台机器的设置）。</summary>
    private static bool WriteNothing(bool complete, out string error)
    {
        error = string.Empty;
        return true;
    }

    private static void Capture(FrameworkElement workspace, string path, int dpi)
    {
        workspace.Measure(new Size(Width, Height));
        workspace.Arrange(new Rect(0, 0, Width, Height));
        workspace.UpdateLayout();

        int pixelWidth = Width * dpi / 96;
        int pixelHeight = Height * dpi / 96;
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(workspace);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>开发树里向上找 packages/plugins（发行包里则用 exe 旁的 plugins）。</summary>
    private static string? FindDevPluginRoot()
    {
        string packaged = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(packaged))
        {
            return packaged;
        }

        DirectoryInfo? cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && cursor is not null; depth++)
        {
            string candidate = Path.Combine(cursor.FullName, "packages", "plugins");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            cursor = cursor.Parent;
        }

        return null;
    }

    /// <summary>截图用快照：两只宠物、几行插件（含一个停用项），设置页有真实数值。</summary>
    private static UiSnapshot SampleSnapshot() => new(
        new[]
        {
            new PetLine("sample-pet", "示例宠物", I18n.Get("cp.pets.source.builtin"),
                I18n.Get("host.roster.state.running"), "51 MB"),
            new PetLine("sample-pet-2", "示例宠物 2", I18n.Get("cp.pets.source.builtin"),
                I18n.Get("host.roster.state.running"), "51 MB"),
        },
        new[]
        {
            new PluginLine("petdock.brain.default", "默认行为层", "0.1.0", 0,
                I18n.Get("cp.plugins.state.running"), 0, I18n.Get("cp.plugins.origin.builtin"),
                Removable: true),
            new PluginLine("petdock.state.null", "空状态层", "0.1.0", 0,
                I18n.Get("cp.plugins.state.running"), 0, I18n.Get("cp.plugins.origin.builtin"),
                Removable: true),
            new PluginLine("petdock.ui.default", "默认界面", "0.1.0", 0,
                I18n.Get("cp.plugins.state.running"), 0, I18n.Get("cp.plugins.origin.builtin"),
                Essential: true),
            new PluginLine("petdock.sample.petimage", "示例图片宠物", "0.1.0", 0,
                I18n.Get("cp.plugins.state.running"), 0, I18n.Get("cp.plugins.origin.builtin"),
                Removable: true),
        },
        new SettingsView(1000.0 / 30.0, "zh-CN", false, "system", "opaque", 128, "示例宠物", "0.1.0"));
}
