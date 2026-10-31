// 新界面层（R1e）的判据：只验「边界」与「能建起来」，不验长相。
//
// 三条各自盯一件事：
//   1. WinForms 只许出现在 src/Tray/NotifyIconTray.cs —— 换泵之后最容易悄悄长回来的就是它；
//   2. 新层不许引用旧内核（否则双轨期两边纠缠，R8「删掉旧层」永远做不完）；
//   3. 空窗口与托盘真的能建起来、托盘只有两项菜单。
// 「进程起来之后窗口可见 / 托盘图标肉眼可见」不在判据里：那要人眼，见 R1 的退出条件。
using System;
using System.Collections.Generic;
using System.IO;
using PetDock.Ui;

namespace PetDock.Hosting;

internal static class NewUiChecks
{
    /// <summary>允许出现 WinForms 的唯一文件（相对仓库根）。</summary>
    private const string TrayFile = "packages/core/PetDock.Ui/src/Tray/NotifyIconTray.cs";

    public static void Run(Report check)
    {
        WinFormsIsolated(check);
        NoCoreReference(check);
        WindowAndTray(check);
    }

    /// <summary>WinForms 的引用面必须收在一个文件里：别处出现 System.Windows.Forms 就是红。</summary>
    private static void WinFormsIsolated(Report check)
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            check("ui-next-winforms-isolated", false, "repo-root not found");
            return;
        }

        var offenders = new List<string>();
        foreach (string file in SourceFiles(Path.Combine(root, "packages", "core", "PetDock.Ui")))
        {
            string rel = Relative(root, file);
            if (string.Equals(rel, TrayFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text = Read(file);
            if (text.Contains("System.Windows.Forms", StringComparison.Ordinal))
            {
                offenders.Add(rel);
            }
        }

        check("ui-next-winforms-isolated", offenders.Count == 0,
            offenders.Count == 0 ? "only=" + TrayFile : "offenders=" + string.Join(",", offenders));
    }

    /// <summary>新层只认契约：csproj 里不许出现 PetDock.Core 的引用。</summary>
    private static void NoCoreReference(Report check)
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            check("ui-next-no-core-reference", false, "repo-root not found");
            return;
        }

        string csproj = Path.Combine(root, "packages", "core", "PetDock.Ui", "PetDock.Ui.csproj");
        string text = File.Exists(csproj) ? Read(csproj) : string.Empty;
        bool referencesCore = text.Contains("PetDock.Core.csproj", StringComparison.Ordinal);
        bool referencesContracts = text.Contains("PetDock.App.Contracts.csproj", StringComparison.Ordinal);
        check("ui-next-no-core-reference", !referencesCore && referencesContracts,
            $"core={referencesCore} contracts={referencesContracts}");
    }

    /// <summary>空窗口与托盘能建起来：窗口标题就是递进去的那句，托盘恰好两项菜单。</summary>
    private static void WindowAndTray(Report check)
    {
        var strings = new NewUiStrings("PetDock", "placeholder", "tip", "open", "exit");
        var window = new AppWindow(strings);
        string title = window.Title;
        bool hasContent = window.Content is not null;
        window.Close();

        using var tray = new NotifyIconTray(strings.TrayTip, strings.TrayOpen, strings.TrayExit);
        bool twoItems = tray.MenuItemCount == 2;
        bool visible = tray.IsVisible;

        check("ui-next-window-and-tray",
            title == strings.Title && hasContent && twoItems && visible,
            $"title={title} content={hasContent} items={tray.MenuItemCount} visible={visible}");
    }

    private static IEnumerable<string> SourceFiles(string dir)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            string rel = file.Replace('\\', '/');
            if (rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    private static string Read(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    /// <summary>从可执行目录向上找仓库根（bin 树深 5~6 层，与旧层的 TrayHost.FindRepoFile 同一办法）。</summary>
    private static string? FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PetDock.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
