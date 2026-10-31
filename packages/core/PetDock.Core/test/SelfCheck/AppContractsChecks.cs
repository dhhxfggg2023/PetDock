// 新契约程序集的边界判据（R1）。
//
// 为什么先立判据再往下写：新界面层「够不到旧 Ui」这件事，靠人记着不引用是撑不住的 ——
// 它必须是一条会红的判据，否则某天有人为图省事 using 一下就回去了，而编译照样过。
//
// 三条：
//   1. PetDock.App.Contracts 里不许出现 WPF / WinForms 命名空间；
//  2. 它的 csproj 不许 UseWPF / UseWindowsForms，也不许 ProjectReference 回 PetDock.Core（依赖方向只能是 Core → Contracts）；
//   3. 旧 Ui 的命名空间不许出现在契约里。
using System;
using System.Collections.Generic;
using System.IO;
using PetDock.Hosting;

namespace PetDock.Hosting;

internal static class AppContractsChecks
{
    internal static void Run(UpdateChecks.Report check)
    {
        string? root = FindContractsRoot();
        if (root is null)
        {
            check("app-contracts-boundary", false, "contracts-root=missing");
            return;
        }

        var violations = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            string name = Path.GetFileName(file);
            if (text.Contains("System.Windows.Forms", StringComparison.Ordinal)) violations.Add(name + ":winforms");
            if (text.Contains("System.Windows;", StringComparison.Ordinal) || text.Contains("System.Windows.", StringComparison.Ordinal)) violations.Add(name + ":wpf");
            if (text.Contains("PetDock.Core", StringComparison.Ordinal)) violations.Add(name + ":core");
        }

        string csproj = Path.Combine(root, "PetDock.App.Contracts.csproj");
        string project = File.Exists(csproj) ? File.ReadAllText(csproj) : string.Empty;
        if (project.Contains("UseWPF", StringComparison.Ordinal)) violations.Add("csproj:use-wpf");
        if (project.Contains("UseWindowsForms", StringComparison.Ordinal)) violations.Add("csproj:use-winforms");
        if (project.Contains("PetDock.Core.csproj", StringComparison.Ordinal)) violations.Add("csproj:references-core");

        check("app-contracts-boundary", violations.Count == 0,
            violations.Count == 0 ? "violations=0" : "violations=" + string.Join(",", violations));
    }

    /// <summary>从 exe 处向上找契约工程目录（与其它判据同一条口径：不写死绝对路径）。</summary>
    private static string? FindContractsRoot()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && cursor is not null; depth++)
        {
            string candidate = Path.Combine(cursor.FullName, "packages", "core", "PetDock.App.Contracts");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            cursor = cursor.Parent;
        }

        return null;
    }
}
