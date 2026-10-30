// 插件回收站（2026-10-28 第三轮）：删除进 .removed 之后，用户要能看见并拿回来。
//
// 为什么不另存一份元数据：被移动的目录里清单是完整的 —— id/名称/版本都能从
// petdock-plugin.json 读回来，目录名的尾部时间戳就是删除时刻。多存一份反而会漂移。
//
// 三条护栏：
//   · 恢复前重新校验清单：回收站里的目录可能被人手动改过，不能直接搬回插件根；
//   · 同 id 已经装着就拒绝恢复，绝不覆盖（覆盖会把当前可用的那只删掉，无法撤销）；
//   · 保留策略（最近 20 个 / 512 MB）在每次删除后修剪，回收站不会无限长。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal sealed partial class PluginInventory
{
    /// <summary>回收站目录名（与 MoveToTrash 共用同一常量口径）。</summary>
    public const string TrashFolder = ".removed";

    private const int TrashKeepCount = 20;
    private const long TrashKeepBytes = 512L * 1024 * 1024;
    private const int StampLength = 17; // yyyy-MM-dd-HHmmss

    /// <summary>回收站里的一只插件。Restorable=false 表示目录还在但清单已不可用（只能彻底删除）。</summary>
    public sealed record TrashEntry(
        string DirName,
        string Id,
        string Name,
        string Version,
        long Bytes,
        DateTime RemovedAt,
        bool Restorable);

    private string TrashRoot => Path.Combine(_userRoot, TrashFolder);

    /// <summary>列出回收站（按删除时间从新到旧）。只读，不修改任何目录。</summary>
    public IReadOnlyList<TrashEntry> Trash()
    {
        var entries = new List<TrashEntry>();
        string root = TrashRoot;
        if (!Directory.Exists(root))
        {
            return entries;
        }

        foreach (string dir in Directory.GetDirectories(root))
        {
            string name = Path.GetFileName(dir);
            DateTime removedAt = RemovedAtOf(name, dir);
            long bytes = MeasureBytes(dir);
            TrashEntry entry;
            try
            {
                Result<PluginManifest, IReadOnlyList<ManifestError>> result = ManifestValidator.Validate(dir);
                entry = result.IsOk && result.Ok is not null
                    ? new TrashEntry(name, result.Ok.Id, result.Ok.Name, result.Ok.Version, bytes, removedAt, true)
                    : new TrashEntry(name, string.Empty, name, string.Empty, bytes, removedAt, false);
            }
            catch (Exception error)
            {
                KernelLog.Write($"plugin-trash-scan-failed dir={name} {error.GetType().Name}: {error.Message}");
                entry = new TrashEntry(name, string.Empty, name, string.Empty, bytes, removedAt, false);
            }

            entries.Add(entry);
        }

        entries.Sort((left, right) => right.RemovedAt.CompareTo(left.RemovedAt));
        return entries;
    }

    /// <summary>
    /// 把一只插件从回收站搬回用户插件根。成功返回插件 id；失败返回 null 并给一句可读的原因。
    /// 调用方负责随后 SetEnabled(id, true)（恢复 = 重新可用，而不是「文件放回来了但列表里没有」）。
    /// </summary>
    public string? Restore(string dirName, out string? error)
    {
        error = null;
        string? source = ResolveTrashDir(dirName);
        if (source is null)
        {
            error = I18n.Get("cp.plugins.restore.missing");
            return null;
        }

        PluginManifest manifest;
        try
        {
            Result<PluginManifest, IReadOnlyList<ManifestError>> result = ManifestValidator.Validate(source);
            if (!result.IsOk || result.Ok is null)
            {
                error = I18n.Get("cp.plugins.restore.invalid");
                return null;
            }

            manifest = result.Ok;
        }
        catch (Exception failure)
        {
            KernelLog.Write($"plugin-restore-validate-failed dir={dirName} {failure.GetType().Name}: {failure.Message}");
            error = I18n.Get("cp.plugins.restore.invalid");
            return null;
        }

        foreach (PluginEntry row in Rows())
        {
            if (string.Equals(row.Id, manifest.Id, StringComparison.Ordinal))
            {
                error = I18n.Get("cp.plugins.restore.exists");
                return null;
            }
        }

        string target = Path.Combine(_userRoot, manifest.Id);
        if (Directory.Exists(target))
        {
            error = I18n.Get("cp.plugins.restore.exists");
            return null;
        }

        try
        {
            Directory.CreateDirectory(_userRoot);
            try
            {
                Directory.Move(source, target);
            }
            catch (IOException)
            {
                // 跨卷：复制完再删源；复制失败就把目标清掉，回收站保持原样。
                try
                {
                    CopyTree(source, target);
                    Directory.Delete(source, recursive: true);
                }
                catch
                {
                    TryDelete(target);
                    throw;
                }
            }

            KernelLog.Write($"plugin-restored plugin={manifest.Id} version={manifest.Version} dir={target}");
            return manifest.Id;
        }
        catch (Exception failure)
        {
            error = I18n.Get("cp.plugins.restore.failed");
            KernelLog.Write($"plugin-restore-failed dir={dirName} {failure.GetType().Name}: {failure.Message}");
            return null;
        }
    }

    /// <summary>彻底删除回收站里的一只（不可撤销；界面负责先确认）。</summary>
    public bool Purge(string dirName, out string? error)
    {
        error = null;
        string? path = ResolveTrashDir(dirName);
        if (path is null)
        {
            error = I18n.Get("cp.plugins.trash.missing");
            return false;
        }

        try
        {
            Directory.Delete(path, recursive: true);
            KernelLog.Write($"plugin-purged dir={dirName}");
            return true;
        }
        catch (Exception failure)
        {
            error = I18n.Get("cp.plugins.purge.failed");
            KernelLog.Write($"plugin-purge-failed dir={dirName} {failure.GetType().Name}: {failure.Message}");
            return false;
        }
    }

    /// <summary>按「最近 20 个 / 512 MB」修剪回收站（每次删除后调用；失败只记日志）。</summary>
    public void PruneTrash()
    {
        try
        {
            int kept = 0;
            long bytes = 0;
            foreach (TrashEntry entry in Trash())
            {
                bool fits = kept < TrashKeepCount && bytes + entry.Bytes <= TrashKeepBytes;
                if (fits)
                {
                    kept++;
                    bytes += entry.Bytes;
                    continue;
                }

                string path = Path.Combine(TrashRoot, entry.DirName);
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    KernelLog.Write($"plugin-trash-pruned dir={entry.DirName}");
                }
            }
        }
        catch (Exception error)
        {
            KernelLog.Write($"plugin-trash-prune-failed {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>把目录名解析回回收站里的绝对路径；任何跳出回收站根的输入一律拒绝。</summary>
    private string? ResolveTrashDir(string dirName)
    {
        if (string.IsNullOrWhiteSpace(dirName)
            || !string.Equals(dirName, Path.GetFileName(dirName), StringComparison.Ordinal))
        {
            return null;
        }

        string root = Path.GetFullPath(TrashRoot);
        string path = Path.GetFullPath(Path.Combine(root, dirName));
        string prefix = root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)
            ? path
            : null;
    }

    private static DateTime RemovedAtOf(string dirName, string dir)
    {
        if (dirName.Length > StampLength)
        {
            string stamp = dirName.Substring(dirName.Length - StampLength);
            if (DateTime.TryParseExact(
                    stamp,
                    "yyyy-MM-dd-HHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTime parsed))
            {
                return parsed;
            }
        }

        return Directory.GetCreationTime(dir);
    }

    private static long MeasureBytes(string dir)
    {
        long bytes = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                bytes += new FileInfo(file).Length;
            }
        }
        catch (Exception error)
        {
            KernelLog.Write($"plugin-trash-measure-failed dir={dir} {error.GetType().Name}: {error.Message}");
        }

        return bytes;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception error)
        {
            KernelLog.Write($"plugin-trash-cleanup-failed dir={dir} {error.GetType().Name}: {error.Message}");
        }
    }
}
