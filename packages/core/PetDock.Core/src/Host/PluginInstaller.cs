// 插件安装（2026-10-28）：把用户选的**目录或 zip** 变成用户插件根下的一份可装载插件。
//
// 参考 PCL2 的做法不是抄界面，而是抄它的顺序：先解到临时区 → 校验 → 确认 → 再落到正式位置。
// 校验不过时正式位置一个字节都没被碰过；覆盖安装先备份，换不上去再把备份搬回来。
//
// 护栏（用户装的是别人给的文件，默认不可信）：
//   · zip 条目路径不许逃出 staging（zip-slip），不许是符号链接；
//   · 条目数与总字节有上限；单个文件也有上限；
//   · 清单必须能被 ManifestValidator 全语义校验通过，入口文件必须在插件目录内；
//   · 目标只落在 <data>/plugins/<id>，id 受清单正则约束（不会带路径分隔符）。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using PetDock.Core.Ui;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

/// <summary>一次待确认的安装：已经落到临时区、校验通过、等待提交或丢弃。</summary>
public sealed record InstallPlan(
    string Id,
    string Name,
    string Version,
    string StagingRoot,
    string PluginDir,
    string TargetDir,
    bool Replaces,
    int Files,
    long Bytes);

internal sealed partial class PluginInstaller
{
    private const int MaxEntries = 5000;
    private const long MaxFileBytes = 64L * 1024 * 1024;
    private const long MaxTotalBytes = 256L * 1024 * 1024;

    private readonly string _userRoot;
    private readonly string _stageRoot;

    public PluginInstaller(string userRoot, string dataRoot)
    {
        _userRoot = Path.GetFullPath(userRoot);
        _stageRoot = Path.Combine(Path.GetFullPath(dataRoot), "plugins", ".staging");
    }

    /// <summary>解包/复制到临时区并校验。返回 false 时 error 是给人看的一句话，临时区已清掉。</summary>
    public bool TryPrepare(
        string source,
        out InstallPlan? plan,
        out string? error,
        CancellationToken token = default,
        CopyProgressNotify? progress = null)
    {
        plan = null;
        error = null;
        string staging = Path.Combine(_stageRoot, Guid.NewGuid().ToString("N"));
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(staging);
            if (Directory.Exists(source))
            {
                long expected = Measure(source).Bytes;
                long copied = 0;
                progress?.Invoke(0, expected);
                CopyDirectory(source, staging, token, ref copied, expected, progress);
                progress?.Invoke(copied, expected);
            }
            else if (File.Exists(source))
            {
                ExtractZip(source, staging, token, progress);
            }
            else
            {
                error = I18n.Get("cp.plugins.install.missing");
                return false;
            }

            (int files, long bytes) = Measure(staging);
            if (files > MaxEntries)
            {
                error = I18n.Get("cp.plugins.install.tooManyFiles", files, MaxEntries);
                return false;
            }
            if (bytes > MaxTotalBytes)
            {
                error = I18n.Get("cp.plugins.install.tooLarge", bytes / (1024 * 1024), MaxTotalBytes / (1024 * 1024));
                return false;
            }

            string? pluginDir = FindPluginDir(staging);
            if (pluginDir is null)
            {
                error = I18n.Get("cp.plugins.install.noManifest", ManifestValidator.ManifestFileName);
                return false;
            }

            Result<PluginManifest, IReadOnlyList<ManifestError>> result = ManifestValidator.Validate(pluginDir);
            if (!result.IsOk)
            {
                error = I18n.Get("cp.plugins.install.badManifest")
                    + " " + string.Join("; ", result.Error!.Select(e => $"{e.Field}: {e.Message}"));
                return false;
            }

            PluginManifest manifest = result.Ok!;
            // 入口的「在不在目录内」只有 PluginEntryPath 一个属主：装载、热重载、按 id 启用、
            // 以及这里的安装校验都走它，免得四个地方各自漂移（2026-10-31 审计）。
            if (!PluginEntryPath.TryResolve(pluginDir, manifest.Entry, out _, out string? entryWhy))
            {
                error = entryWhy == "entry-missing"
                    ? I18n.Get("cp.plugins.install.entryMissing", manifest.Entry)
                    : I18n.Get("cp.plugins.install.entryOutside");
                return false;
            }

            string target = Path.Combine(_userRoot, manifest.Id);
            plan = new InstallPlan(
                manifest.Id,
                manifest.Name,
                manifest.Version,
                staging,
                pluginDir,
                target,
                Directory.Exists(target),
                files,
                bytes);
            return true;
        }
        catch (OperationCanceledException)
        {
            // 用户取消：临时区不能留半份解包结果。
            TryDelete(staging);
            throw;
        }
        catch (Exception ex)
        {
            KernelLog.Write($"plugin-install-prepare-failed {ex.GetType().Name}: {ex.Message}");
            error = I18n.Get("cp.plugins.install.failed");
            TryDelete(staging);
            return false;
        }
    }

    /// <summary>提交：目标存在就先搬到备份，再把新目录搬进去；失败尽量把备份搬回来。</summary>
    public bool TryCommit(InstallPlan plan, out string? error, CancellationToken token = default)
    {
        error = null;
        string? backup = null;
        try
        {
            // 取消只在「还没动正式位置」时生效：一旦开始搬目录，就要把它搬完或搬回去。
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_userRoot);
            if (Directory.Exists(plan.TargetDir))
            {
                backup = PluginDirBackup.PathFor(plan.TargetDir);
                Directory.Move(plan.TargetDir, backup);
            }

            Directory.Move(plan.PluginDir, plan.TargetDir);
            if (backup is not null)
            {
                TryDelete(backup);
            }

            KernelLog.Write(
                $"plugin-installed id={plan.Id} version={plan.Version} replaces={(plan.Replaces ? 1 : 0)}"
                + $" files={plan.Files} bytes={plan.Bytes}");
            return true;
        }
        catch (OperationCanceledException)
        {
            // 取消发生在动正式位置之前：按原样交还给调用方，临时区由 finally 清掉。
            throw;
        }
        catch (Exception ex)
        {
            if (backup is not null && Directory.Exists(backup) && !Directory.Exists(plan.TargetDir))
            {
                try
                {
                    Directory.Move(backup, plan.TargetDir);
                }
                catch (Exception restoreError)
                {
                    // 搬不回去就别把它留在插件根下：那里它会被当成一只插件背后的幽灵副本，
                    // 用户既看不到它的版本、也拿不回来。挪进回收站 = 看得见、还能恢复。
                    string parked = TryPark(plan.Id, backup);
                    KernelLog.Write(
                        $"plugin-install-restore-failed id={plan.Id} {restoreError.Message} parked={parked}");
                }
            }

            KernelLog.Write($"plugin-install-commit-failed id={plan.Id} {ex.GetType().Name}: {ex.Message}");
            error = I18n.Get("cp.plugins.install.commitFailed");
            return false;
        }
        finally
        {
            TryDelete(plan.StagingRoot);
        }
    }

    /// <summary>丢弃一次未提交的安装（用户取消确认）。</summary>
    public void Discard(InstallPlan plan) => TryDelete(plan.StagingRoot);

    private static string? FindPluginDir(string staging)
    {
        if (File.Exists(Path.Combine(staging, ManifestValidator.ManifestFileName)))
        {
            return staging;
        }

        var matches = new List<string>();
        foreach (string dir in Directory.GetDirectories(staging))
        {
            if (File.Exists(Path.Combine(dir, ManifestValidator.ManifestFileName)))
            {
                matches.Add(dir);
            }
        }

        // 多个候选 = 这个包里装了不止一只插件：v1 不猜，直接让人打包一份一个。
        return matches.Count == 1 ? matches[0] : null;
    }

    private static void ExtractZip(string zipPath, string staging, CancellationToken token, CopyProgressNotify? progress)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaxEntries)
        {
            throw new IOException(I18n.Get("cp.plugins.install.tooManyFiles", archive.Entries.Count, MaxEntries));
        }

        string rootFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        long expected = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            expected += entry.Length;
        }

        long copied = 0;
        progress?.Invoke(0, expected);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            if (relative.Length == 0)
            {
                continue;
            }

            if (Path.IsPathRooted(relative))
            {
                throw new IOException(I18n.Get("cp.plugins.install.unsafeEntry", entry.FullName));
            }

            string dest = Path.GetFullPath(Path.Combine(staging, relative));
            if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(I18n.Get("cp.plugins.install.unsafeEntry", entry.FullName));
            }

            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(I18n.Get("cp.plugins.install.unsafeEntry", entry.FullName));
            }

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(dest);
                continue;
            }

            if (entry.Length > MaxFileBytes)
            {
                throw new IOException(I18n.Get("cp.plugins.install.fileTooLarge", entry.FullName));
            }

            total += entry.Length;
            if (total > MaxTotalBytes)
            {
                throw new IOException(I18n.Get("cp.plugins.install.tooLarge", total / (1024 * 1024), MaxTotalBytes / (1024 * 1024)));
            }

            string? parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            using Stream input = entry.Open();
            using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyStream(input, output, token, ref copied, expected, progress);
        }
    }

    private static void CopyDirectory(
        string from,
        string to,
        CancellationToken token,
        ref long copied,
        long expected,
        CopyProgressNotify? progress)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(I18n.Get("cp.plugins.install.unsafeEntry", file));
            }
            if (info.Length > MaxFileBytes)
            {
                throw new IOException(I18n.Get("cp.plugins.install.fileTooLarge", file));
            }

            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            using (var output = new FileStream(Path.Combine(to, info.Name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            {
                CopyStream(input, output, token, ref copied, expected, progress);
            }
        }

        foreach (string dir in Directory.GetDirectories(from))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(I18n.Get("cp.plugins.install.unsafeEntry", dir));
            }
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)), token, ref copied, expected, progress);
        }
    }

    /// <summary>分块复制（zip 条目与插件目录共用）：每块检查取消并上报字节进度。</summary>
    private static void CopyStream(
        Stream input,
        Stream output,
        CancellationToken token,
        ref long copied,
        long expected,
        CopyProgressNotify? progress)
    {
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            copied += read;
            progress?.Invoke(copied, expected);
        }
    }

    private static (int Files, long Bytes) Measure(string dir)
    {
        int files = 0;
        long bytes = 0;
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            files++;
            bytes += new FileInfo(file).Length;
        }
        return (files, bytes);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            KernelLog.Write($"plugin-install-cleanup-failed dir={dir} {ex.GetType().Name}: {ex.Message}");
        }
    }
}
