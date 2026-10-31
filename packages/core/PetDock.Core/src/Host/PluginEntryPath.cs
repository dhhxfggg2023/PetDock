// 插件入口的唯一解析者（2026-10-31）。
//
// 为什么要单独一个文件：清单里的 entry 是**远端可控的字符串**，而它会被拼成路径去
// Assembly.LoadFrom / Process.Start。2026-10-31 的只读审计指出两件事：
//   1. 安装器（PluginInstaller）在**安装那一刻**查过一次「入口必须在插件目录内」，
//      但装载、热重载、按 id 启用三条路都不复查；
//   2. PluginHost.Tier2 与 PetSurfaceLoader 用的是 Path.Combine(pluginDir, entry) ——
//      第二个参数是绝对路径时 Path.Combine **直接返回它**，于是「入口在目录内」这个前提
//      被悄悄换掉：任何能写插件目录的人（发行 zip 解出来的目录本身就是用户可写）
//      都可以让宿主去启动任意 exe。
//
// 一条纪律：任何把清单 entry 变成可加载路径的地方，都必须走这里。
// 判据 ui-plugin-entry-*（行为）与 ui-plugin-entry-single-owner（源码级）盯着这条纪律。
// 名字叫 Path 而不是 Entry：PetDock.Hosting.PluginEntry 已经是「插件页那一行」的值记录，两个都叫它只会打架。
using System;
using System.IO;

namespace PetDock.Hosting;

internal static class PluginEntryPath
{
    /// <summary>
    /// 把清单的 entry 解析成「插件目录内的绝对路径」。
    /// 四类拒绝：空、绝对路径、跳出目录、不存在。返回 false 时 reason 是机器可读短码。
    /// </summary>
    internal static bool TryResolve(string pluginDir, string entry, out string path, out string? reason)
    {
        path = string.Empty;
        reason = null;
        if (string.IsNullOrWhiteSpace(entry))
        {
            reason = "entry-empty";
            return false;
        }

        // 绝对路径必须**先**拒：Path.Combine 遇到绝对的第二段会直接把它返回，
        // 后面那条前缀检查就变成「检查一个我们自己拼出来的路径」，永远通过。
        if (Path.IsPathRooted(entry))
        {
            reason = "entry-rooted";
            return false;
        }

        string root;
        string full;
        try
        {
            root = Path.GetFullPath(pluginDir);
            full = Path.GetFullPath(Path.Combine(root, entry));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "entry-bad-shape";
            return false;
        }

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            reason = "entry-outside";
            return false;
        }

        if (!File.Exists(full))
        {
            reason = "entry-missing";
            return false;
        }

        path = full;
        return true;
    }
}
