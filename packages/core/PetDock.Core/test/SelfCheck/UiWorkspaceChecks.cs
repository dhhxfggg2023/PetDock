// WPF 壳的机器判据（2026-10-28）：页面全部由 IUiPage 插件贡献之后，壳只剩下
// 排序、切页、页头/状态条、主题资源这四件事。这里用假插件 + 预览上下文验这四件事，
// 不加载真实插件（真实插件的截图在 UiShots，命令面单测在插件自己的工程）。
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PetDock.Core.Ui;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace PetDock.Hosting;

internal static class UiWorkspaceChecks
{
    public static void Run(Report check)
    {
        OrderedPages(check);
        SwitchAndStatus(check);
        UnknownPage(check);
        Render(check);
        EmptyWorkspace(check);
    }

    private static void OrderedPages(Report check)
    {
        var context = new PreviewUiContext();
        var pages = new IUiPage[]
        {
            new TestPage("c", "Title C", 30),
            new TestPage("a", "Title A", 10),
            new TestPage("b", "Title B", 20),
        };

        using var panel = new ControlPanel(context, pages, showWindow: false);
        IReadOnlyList<string> ids = panel.PageIds;
        bool ok = ids.Count == 3 && ids[0] == "a" && ids[1] == "b" && ids[2] == "c";
        check("ui-workspace-page-order", ok, "pages=" + string.Join(",", ids));

        // Order 相同的页按 id 稳定排序（否则每次启动导航都会换位置）。
        var tied = new IUiPage[]
        {
            new TestPage("z", "Title Z", 10),
            new TestPage("m", "Title M", 10),
        };
        using var tiedPanel = new ControlPanel(new PreviewUiContext(), tied, showWindow: false);
        IReadOnlyList<string> tiedIds = tiedPanel.PageIds;
        check("ui-workspace-page-order-tie", tiedIds.Count == 2 && tiedIds[0] == "m" && tiedIds[1] == "z",
            "pages=" + string.Join(",", tiedIds));
    }

    private static void SwitchAndStatus(Report check)
    {
        var context = new PreviewUiContext();
        var pages = new IUiPage[]
        {
            new TestPage("a", "Title A", 10),
            new TestPage("b", "Title B", 20),
        };

        using var panel = new ControlPanel(context, pages, showWindow: false);
        bool first = string.Equals(panel.CurrentPage, "a", StringComparison.Ordinal);
        panel.ShowPage("b");
        bool second = string.Equals(panel.CurrentPage, "b", StringComparison.Ordinal);
        // 视图是 内容承载器里的内容，没有布局就没有可视子节点：先量一次再找文字。
        panel.Workspace.Measure(new Size(1040, 680));
        panel.Workspace.Arrange(new Rect(0, 0, 1040, 680));
        panel.Workspace.UpdateLayout();
        string text = FindBody(panel.Workspace, "page-body-b") ? "found" : "missing";
        check("ui-workspace-switch", first && second && text == "found", $"first={first} second={second} body={text}");

        panel.SetStatus("saved");
        check("ui-workspace-status", string.Equals(panel.Workspace.StatusTextValue, "saved", StringComparison.Ordinal),
            "status=" + panel.Workspace.StatusTextValue);
    }

    private static void UnknownPage(Report check)
    {
        using var panel = new ControlPanel(new PreviewUiContext(), new IUiPage[] { new TestPage("a", "Title A", 10) }, showWindow: false);
        bool threw = false;
        try
        {
            panel.ShowPage("nope");
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        check("ui-workspace-unknown-page", threw, "threw=" + threw);
    }

    private static void Render(Report check)
    {
        var context = new PreviewUiContext();
        using var panel = new ControlPanel(context, new IUiPage[] { new TestPage("a", "Title A", 10) }, showWindow: false);
        UiWorkspaceView workspace = panel.Workspace;
        try
        {
            workspace.Measure(new Size(1040, 680));
            workspace.Arrange(new Rect(0, 0, 1040, 680));
            workspace.UpdateLayout();

            var bitmap = new RenderTargetBitmap(1040, 680, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(workspace);
            var pixels = new byte[1040 * 680 * 4];
            bitmap.CopyPixels(pixels, 1040 * 4, 0);
            var colors = new HashSet<uint>();
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                if (colors.Count < 8)
                {
                    colors.Add((uint)(pixels[i] | (pixels[i + 1] << 8) | (pixels[i + 2] << 16) | (pixels[i + 3] << 24)));
                }
            }

            check("ui-workspace-render", colors.Count >= 3, "colors=" + colors.Count);
        }
        catch (Exception error)
        {
            check("ui-workspace-render", false, error.GetType().Name + ": " + error.Message);
        }
    }

    private static void EmptyWorkspace(Report check)
    {
        try
        {
            var workspace = new UiWorkspaceView(Array.Empty<IUiPage>(), new PreviewUiContext());
            check("ui-workspace-empty", workspace.PageIds.Count == 0 && workspace.CurrentPage is null,
                $"pages={workspace.PageIds.Count} current={workspace.CurrentPage ?? "null"}");
        }
        catch (Exception error)
        {
            check("ui-workspace-empty", false, error.GetType().Name + ": " + error.Message);
        }
    }

    private static bool FindBody(DependencyObject root, string text)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock block && string.Equals(block.Text, text, StringComparison.Ordinal))
            {
                return true;
            }

            if (FindBody(child, text))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class TestPage : IUiPage
    {
        public TestPage(string id, string title, int order)
        {
            Id = id;
            Title = title;
            Order = order;
        }

        public string Id { get; }

        public string Title { get; }

        public string Note => "note-" + Id;

        public string Glyph => "•";

        public int Order { get; }

        public FrameworkElement CreateView() => new Border
        {
            Padding = new Thickness(24),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3A)),
            Child = new TextBlock
            {
                Text = "page-body-" + Id,
                FontSize = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            },
        };
    }
}
