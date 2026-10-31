// 插件入口解析的判据（2026-10-31）。判据行 ui-plugin-entry-*。
//
// 为什么有这一组：2026-10-31 的只读审计指出，安装器在**安装那一刻**查过一次
// 「入口必须在插件目录内」，但装载 / 热重载 / 按 id 启用三条路都不复查；
// 而 Path.Combine(pluginDir, entry) 遇到绝对路径的第二段会**直接返回它** ——
// 于是「入口在目录内」这个前提被悄悄换掉：能写插件目录的人（发行 zip 解出来的目录用户可写）
// 可以让宿主启动任意 exe（tier-2）或把任意 dll 载进进程（tier-0）。
//
// 两条判据：行为（四类拒绝都对）与源码级（除了 PluginEntryPath，没人再把 manifest.Entry 拼成路径）。
using System;
using System.IO;
using PetDock.Hosting;

namespace PetDock.Hosting;

internal static class PluginEntryChecks
{
    internal static void Run(UpdateChecks.Report check)
    {
        string root = SelfTestScratch.NewDir("plugin-entry");
        try
        {
            Behaviour(check, root);
            SingleOwner(check);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 临时目录删不掉不影响结论。
            }
        }
    }

    /// <summary>四类拒绝：绝对路径、跳出目录、不存在，以及「正常的那一个」确实解析出来了。</summary>
    private static void Behaviour(UpdateChecks.Report check, string root)
    {
        string dir = Path.Combine(root, "plugin");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ok.dll"), "x");
        File.WriteAllText(Path.Combine(root, "outside.exe"), "x");

        bool inside = PluginEntryPath.TryResolve(dir, "ok.dll", out string path, out _)
            && string.Equals(path, Path.Combine(dir, "ok.dll"), StringComparison.OrdinalIgnoreCase);

        // 绝对路径：Path.Combine 会直接返回它 —— 这正是被测出来的那个洞。
        bool rooted = !PluginEntryPath.TryResolve(Path.Combine(dir, ""), Path.Combine(root, "outside.exe"), out _, out string? rootedWhy)
            && rootedWhy == "entry-rooted";

        bool outside = !PluginEntryPath.TryResolve(dir, Path.Combine("..", "outside.exe"), out _, out string? outsideWhy)
            && outsideWhy == "entry-outside";

        bool missing = !PluginEntryPath.TryResolve(dir, "nope.dll", out _, out string? missingWhy)
            && missingWhy == "entry-missing";

        check("ui-plugin-entry-contained", inside && rooted && outside && missing,
            $"inside={inside} rooted={rootedWhy ?? "accepted"} outside={outsideWhy ?? "accepted"} missing={missingWhy ?? "accepted"}");
    }

    /// <summary>
    /// 源码级：内核 src 里不许再有「把 manifest.Entry 拼成路径」的行。
    /// 这是「一个概念一个属主」的判据形态（与 ui-throttle-single-decision-owner 同一个套路）：
    /// 它挡的不是某一次写错，而是**第五个装载点**在将来悄悄长出来。
    /// </summary>
    private static void SingleOwner(UpdateChecks.Report check)
    {
        int sites = SourceScan.Count(SourceScan.CoreSourceDir, line =>
            line.Contains("manifest.Entry", StringComparison.Ordinal)
            && (line.Contains("Path.Combine", StringComparison.Ordinal)
                || line.Contains("IsPathRooted", StringComparison.Ordinal)));

        // -1 = 找不到仓库根：判据什么也没检查，必须红（SourceScan 的口径）。
        check("ui-plugin-entry-single-owner", sites == 0,
            $"combine-sites={sites} (0 = 入口解析只有 PluginEntry 一个属主)");
    }
}
