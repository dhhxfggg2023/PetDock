// 安装包的解压那一半（2026-10-31 从 UpdateStage.cs 拆出：D-03c 单文件 400 行上限）。
//
// 为什么解压放在**下载时**而不是交接时：交接那一刻本进程正要退出，没有地方显示错误；
// 而在下载时失败，用户还看着面板，可以立刻重试。所以这条路径上的失败必须报得出来。
using System;
using System.IO;
using System.IO.Compression;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal static partial class UpdateStage
{
    /// <summary>
    /// 把安装包解到暂存树（平铺：剪掉 zip 里的顶层目录一格）。
    ///
    /// 解压在**下载时**就做，而不是等到装的时候：那时候用户还看着面板，失败能立刻重试；
    /// 而交接那一刻要尽可能短（多一秒就多一秒「程序已经在关了，但我还不能开」）。
    /// </summary>
    internal static bool Extract(string zipPath, string treeDir, out int files, out string? why)
    {
        files = 0;
        why = null;
        try
        {
            DeleteTree(treeDir);
            Directory.CreateDirectory(treeDir);
            string rootFull = Path.GetFullPath(treeDir) + Path.DirectorySeparatorChar;

            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count > MaxEntries)
            {
                why = "zip-too-many-entries";
                return false;
            }

            long total = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.Name.Length == 0)
                {
                    continue; // 目录条目：文件落盘时自己会建
                }

                string? relative = TrimTop(entry.FullName);
                if (relative is null)
                {
                    continue;
                }

                string dest = Path.GetFullPath(Path.Combine(treeDir, relative));
                if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    why = "zip-slip";
                    return false;
                }

                if (entry.Length > MaxEntryBytes)
                {
                    why = "zip-entry-too-large";
                    return false;
                }

                total += entry.Length;
                if (total > MaxEntryBytes)
                {
                    why = "zip-too-large";
                    return false;
                }

                string? parent = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                entry.ExtractToFile(dest, overwrite: true);
                files++;
            }

            if (!File.Exists(Path.Combine(treeDir, EntryName)))
            {
                why = "zip-no-entry-point";
                return false;
            }

            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException
            or UnauthorizedAccessException or NotSupportedException)
        {
            why = "unzip-" + error.GetType().Name;
            KernelLog.Write($"update-unzip-failed {error.GetType().Name}: {error.Message}");
            return false;
        }
    }

    /// <summary>剪掉 zip 里的顶层目录（发布包是 <c>PetDock-x.y.z/...</c>），并挡掉跳出目录的形状。</summary>
    private static string? TrimTop(string fullName)
    {
        string[] parts = fullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        string rest = string.Join('/', parts[1..]);
        return rest.Contains("..", StringComparison.Ordinal) ? null : rest.Replace('/', Path.DirectorySeparatorChar);
    }

}
