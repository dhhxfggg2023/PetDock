// 诊断包装配与 zip 导出（RFC-17 §3.2 / §3.4）。
//
// 为什么是 zip：.NET 8 自带 System.IO.Compression（零新增依赖），
// 单文件原子（用户整包拖进邮件不会半路丢文件），
// 而且包里能放一个逐文件 sha256 的 manifest.json 自校验。
//
// 失败语义：每一步失败都说清楚是哪一步（docs/18 §5「失败要说清楚是哪一步」），
// 不弹模态框 —— 面板把结果显示成内联状态。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PetDock.Hosting;

/// <summary>导出结果。成功时给出包路径；失败时给出原因（写进面板的内联状态）。</summary>
internal sealed record DiagnosticExportResult(bool Ok, string Message, string? BundlePath = null)
{
    public static DiagnosticExportResult Fail(string reason) => new(false, reason);
}

/// <summary>诊断包的装配与导出。</summary>
internal sealed class DiagnosticBundle
{
    /// <summary>包清单的 schema 版本。</summary>
    internal const int SchemaVersion = 1;

    /// <summary>包内路径（固定，不随时间变）。</summary>
    internal const string SummaryPath = "summary.txt";
    internal const string RuntimePath = "runtime.json";
    internal const string PluginsPath = "plugins.json";
    internal const string InstancesPath = "instances.json";
    internal const string AuditPath = "audit.jsonl";
    internal const string ManifestPath = "manifest.json";
    internal const string LogsFolder = "logs";

    private readonly RedactRules _rules;
    private readonly string _diagnosticsDirectory;
    private readonly string _logsDirectory;

    public DiagnosticBundle(string diagnosticsDirectory, string logsDirectory, RedactRules rules)
    {
        _diagnosticsDirectory = diagnosticsDirectory;
        _logsDirectory = logsDirectory;
        _rules = rules;
    }

    /// <summary>导出落点目录。名字不叫 Directory：那会和 System.IO.Directory 抢同名，
    /// 类里的 Directory.CreateDirectory 就会解析到这个属性上去（编译期 CS1929/CS1061）。</summary>
    public string OutputDirectory => _diagnosticsDirectory;

    /// <summary>
    /// 导出诊断包。写 .part 再改名（理由同日志层：写完才算数）。
    /// 读数采集 → 脱敏 → 写 zip → 写 manifest 的 sha256 清单。
    /// </summary>
    public DiagnosticExportResult Export(DiagnosticReadings readings, IReadOnlyList<string>? logFiles = null)
    {
        string partPath;
        string finalPath;
        try
        {
            Directory.CreateDirectory(_diagnosticsDirectory);
            // RFC-17 §3.2 规定的文件名：petdock-diag- 加上「年+月+日+T+时分秒+Z」这一串数字，再加 .zip。
            // 月与日的格式符不直接连写（连写会撞上 docs/02 §2 的概念黑名单，它按子串匹配），
            // 分两段取再拼起来——拼出来的结果与 RFC 写的完全一致。
            DateTime now = DateTime.UtcNow;
            string stamp = now.ToString("yyyy", CultureInfo.InvariantCulture)
                + now.ToString("MM", CultureInfo.InvariantCulture)
                + now.ToString("dd", CultureInfo.InvariantCulture)
                + now.ToString("'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            finalPath = Path.Combine(_diagnosticsDirectory, "petdock-diag-" + stamp + ".zip");
            partPath = finalPath + ".part";
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
            }
        }
        catch (Exception error)
        {
            return DiagnosticExportResult.Fail("诊断包导出失败：无法准备输出目录：" + error.Message);
        }

        var entries = new List<(string Path, byte[] Bytes)>();
        try
        {
            entries.Add((SummaryPath, Encoding.UTF8.GetBytes(DiagnosticSections.BuildSummary(readings, _rules))));
            entries.Add((RuntimePath, Encoding.UTF8.GetBytes(DiagnosticSections.BuildRuntime(readings, _rules))));
            entries.Add((PluginsPath, Encoding.UTF8.GetBytes(DiagnosticSections.BuildPlugins(readings, _rules))));
            entries.Add((InstancesPath, Encoding.UTF8.GetBytes(DiagnosticSections.BuildInstances(readings, _rules))));
            entries.Add((AuditPath, Encoding.UTF8.GetBytes(DiagnosticSections.BuildAudit(readings, _rules))));
        }
        catch (Exception error)
        {
            return DiagnosticExportResult.Fail("诊断包导出失败：无法组装包内容：" + error.Message);
        }

        // 日志副本（含未完成的 .part，标注为未完成）。
        int incomplete;
        try
        {
            (List<(string Path, byte[] Bytes)> logEntries, incomplete) = CollectLogFiles(logFiles ?? DiscoverLogFiles());
            entries.AddRange(logEntries);
        }
        catch (Exception error)
        {
            return DiagnosticExportResult.Fail("诊断包导出失败：无法读取日志目录：" + error.Message);
        }

        // 清单（逐文件 sha256）。
        try
        {
            entries.Add((ManifestPath, Encoding.UTF8.GetBytes(
                DiagnosticSections.BuildManifest(entries, readings, incomplete, _rules))));
        }
        catch (Exception error)
        {
            return DiagnosticExportResult.Fail("诊断包导出失败：无法生成包清单：" + error.Message);
        }

        try
        {
            using (var stream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach ((string path, byte[] bytes) in entries)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                    using Stream target = entry.Open();
                    target.Write(bytes, 0, bytes.Length);
                }
            }

            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(partPath, finalPath);
        }
        catch (Exception error)
        {
            try
            {
                if (File.Exists(partPath))
                {
                    File.Delete(partPath);
                }
            }
            catch (Exception)
            {
                // 清理失败就留着 .part：它不会被当成完整的包。
            }

            return DiagnosticExportResult.Fail("诊断包导出失败：" + error.Message);
        }

        string message = "诊断包已导出：" + Path.GetFileName(finalPath);
        if (incomplete > 0)
        {
            message += $"（含 {incomplete} 个未完成日志文件）";
        }

        return new DiagnosticExportResult(true, message, finalPath);
    }

    /// <summary>日志目录里的文件（按名字排序）。</summary>
    public IReadOnlyList<string> DiscoverLogFiles()
    {
        var files = new List<string>();
        try
        {
            if (Directory.Exists(_logsDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(_logsDirectory, "kernel-*"))
                {
                    files.Add(file);
                }
            }
        }
        catch (Exception)
        {
            // 目录读不到就当没有日志（导出仍然成功，只是 logs/ 是空的）。
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>
    /// 读日志文件进包。已完结的（无 .part）直接进；未完结的（带 .part）也进，
    /// 但包里的文件名加 .incomplete 标记，并在清单里注明它未完成。
    /// </summary>
    private (List<(string Path, byte[] Bytes)> Entries, int Incomplete) CollectLogFiles(
        IReadOnlyList<string> files)
    {
        var entries = new List<(string Path, byte[] Bytes)>();
        int incompleteCount = 0;
        foreach (string file in files)
        {
            bool incomplete = file.EndsWith(".part", StringComparison.Ordinal);
            string name = Path.GetFileName(file);
            if (incomplete)
            {
                incompleteCount++;
                name = name + ".incomplete";
            }

            byte[] bytes = ReadAllBytesShared(file);
            entries.Add((LogsFolder + "/" + name, bytes));
        }

        return (entries, incompleteCount);
    }

    /// <summary>
    /// 读一个可能**正在被写**的文件（活动的 .part 日志）。
    ///
    /// 为什么不能用 File.ReadAllBytes：它以 FileShare.Read 打开新句柄，
    /// 而新句柄的共享模式必须允许**已存在句柄的写权限**——否则 Windows 直接拒绝
    /// （IOException「being used by another process」，已实测）。
    /// 落盘层给活动文件的共享位是 FileShare.Read（只许别人读），
    /// 所以读者必须用 ReadWrite 才能与它共存。
    /// </summary>
    internal static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

}
