// 内核 src/ 的源码扫描（--eventtest 的静态面判据共用这一处）。
//
// 为什么要有这个文件：这套「往上找 PetDock.sln，再遍历 src/**/*.cs 逐行数」的逻辑原来只活在
// EventSelfTest.Hook.cs 里（数 SetWindowsHookEx 的安装点）。2026-10-25 加事件生产点扫描时，
// 与其复制第二份遍历，不如让两者走同一条 —— 少一个拥有者，就少一处会漂的地方。
//
// 找不到仓库根一律返回 -1：那意味着判据**什么也没检查**，必须红，不能静默通过。
using System;
using System.IO;
using System.Reflection;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

internal static class SourceScan
{
    /// <summary>内核源码目录（相对仓库根）。</summary>
    internal const string CoreSourceDir = "packages/core/PetDock.Core/src";

    /// <summary>数一遍内核 src/ 里满足 <paramref name="match"/> 的行。找不到仓库根返回 -1。</summary>
    internal static int Count(string relativeDir, Func<string, bool> match)
    {
        try
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PetDock.sln")))
            {
                dir = dir.Parent;
            }

            if (dir is null)
            {
                return -1;
            }

            string src = Path.Combine(dir.FullName, relativeDir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(src))
            {
                return -1;
            }

            int sites = 0;
            foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                foreach (string raw in File.ReadLines(file))
                {
                    if (match(raw.TrimStart()))
                    {
                        sites++;
                    }
                }
            }

            return sites;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// 事件 <paramref name="eventName"/> 的生产点数：src/ 里引用 `EventCatalog.&lt;常量名&gt;` 的行数。
    /// 数的是**常量名**而不是事件名字面量 —— 生产点写的是常量。目录文件那一行是
    /// `public const string X = "…"`、`Names` 数组里是裸名字，两者都不含 `EventCatalog.X`，
    /// 所以不需要额外把目录文件排除掉。名字不在目录里（或目录读不到）返回 -1：必须红。
    /// </summary>
    internal static int CountProducerSites(string eventName)
    {
        string reference = EventCatalogReference(eventName);
        return reference.Length == 0
            ? -1
            : Count(CoreSourceDir, line => line.Contains(reference, StringComparison.Ordinal));
    }

    /// <summary>把目录名（`pet.state.migrated`）映射成源码里的引用串（`EventCatalog.PetStateMigrated`）。
    /// 走反射而不是手抄一张表：常量改名或新增时，这张表不会漂。</summary>
    private static string EventCatalogReference(string eventName)
    {
        foreach (FieldInfo field in typeof(EventCatalog).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && field.FieldType == typeof(string)
                && string.Equals((string?)field.GetRawConstantValue(), eventName, StringComparison.Ordinal))
            {
                return "EventCatalog." + field.Name;
            }
        }

        return string.Empty;
    }
}
