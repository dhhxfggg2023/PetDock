// AuditTrail 的读回那一半：把目录里的流水读成行。
//
// 为什么要拆出来：D-03c 的单文件 400 行上限。读与写的失败模式完全不同
// （写失败要「不抛」，读失败要「如实说读不到」）——混在一起时，
// 「读不到」很容易被写成「没有内容」，那是最坏的一种撒谎。
//
// 诚实性纪律：
//   目录不存在 = 「尚无流水」（Available=true，Note 说明）；
//   目录在但读不出来 = 「不可用」（Available=false）；
//   两者**必须**区分：前者是正常空态，后者是故障。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

/// <summary>流水里的一行（读回来之后的形态）。</summary>
internal sealed record AuditTrailRow(
    string Timestamp,
    string PluginId,
    string Kind,
    string Detail,
    bool Incomplete);

/// <summary>读流水的结果。Available=false 表示「读不到」（不是「没有」）。</summary>
internal sealed record AuditTrailRead(
    IReadOnlyList<AuditTrailRow> Rows,
    int Files,
    int Incomplete,
    bool Available,
    string Note);

internal sealed partial class AuditTrail
{
    /// <summary>
    /// 读回目录里的全部流水（按文件名排序 = 按时间排序）。
    /// 目录不存在 = 「还没有流水」；读不出来 = 「不可用」。两者不一样，如实区分。
    /// </summary>
    public static AuditTrailRead ReadAll(string directory)
    {
        var rows = new List<AuditTrailRow>();
        int files = 0;
        int incomplete = 0;

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return new AuditTrailRead(rows, 0, 0, true, I18n.Get("host.diag.trail.none"));
        }

        string[] paths;
        try
        {
            paths = Directory.GetFiles(directory, "audit-*.jsonl*");
            Array.Sort(paths, StringComparer.Ordinal);
        }
        catch (Exception error)
        {
            return new AuditTrailRead(rows, 0, 0, false, I18n.Get("host.diag.trail.unreadable", error.Message));
        }

        foreach (string path in paths)
        {
            bool isPart = path.EndsWith(".part", StringComparison.Ordinal);
            files++;
            if (isPart)
            {
                incomplete++;
            }

            string text;
            try
            {
                // 活动文件正被写入线程的句柄占着：读它必须用共享读
                //（File.ReadAllText 会报 IOException「being used by another process」，已实测）。
                text = Encoding.UTF8.GetString(DiagnosticBundle.ReadAllBytesShared(path));
            }
            catch (Exception)
            {
                // 读不到的那一个文件不当成「有内容」，也不让整次读取失败。
                continue;
            }

            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                AuditTrailRow? row = ParseLine(line, isPart);
                if (row is not null)
                {
                    rows.Add(row);
                }
            }
        }

        return new AuditTrailRead(rows, files, incomplete, true, I18n.Get("host.diag.trail.count", rows.Count));
    }

    /// <summary>一行 → 流水行。解析不了就返回 null（坏行不冒充好行）。</summary>
    private static AuditTrailRow? ParseLine(string line, bool incomplete)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(line);
            if (node is null)
            {
                return null;
            }

            return new AuditTrailRow(
                node["timestamp"]?.GetValue<string>() ?? string.Empty,
                node["plugin_id"]?.GetValue<string>() ?? string.Empty,
                node["kind"]?.GetValue<string>() ?? string.Empty,
                node["detail"]?.GetValue<string>() ?? string.Empty,
                incomplete);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
