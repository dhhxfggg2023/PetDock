// 界面截图工件（2026-10-28 WPF 重构）：不再由测试代码搭一棵假界面树，而是
// 走生产路径 UiPluginCatalog.Load 找到真实界面插件，用预览上下文提供只读快照，
// 然后把壳渲染成 PNG。这样截图拍到的就是用户会看到的页面。
//
// 口径：--uitest --shots <目录>；96dpi 与 144dpi 各一套、浅色与深色各一套。
// 渲染不依赖窗口显示（无前台、锁屏也能跑），所以不再有旧的 SHOT-SKIP 分支。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
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

        emit("SHOT-OK n=" + count + " dir=" + dir);
        return count;
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
            new PetLine("sample-pet", "示例宠物", "assets/sample-pet", "idle", "51 MB"),
            new PetLine("sample-pet-2", "示例宠物 2", "assets/sample-pet", "walk", "51 MB"),
        },
        new[]
        {
            new PluginLine("petdock.brain.default", "默认行为层", "0.1.0", 0, "loaded", 0, "built-in"),
            new PluginLine("petdock.state.null", "空状态层", "0.1.0", 0, "loaded", 0, "built-in"),
            new PluginLine("petdock.ui.default", "默认界面", "0.1.0", 0, "loaded", 0, "built-in",
                Essential: true),
            new PluginLine("petdock.sample.petimage", "示例图片宠物", "0.1.0", 0, "loaded", 0, "built-in"),
        },
        new SettingsView(1000.0 / 30.0, "zh-CN", false, "system", "opaque"));
}
