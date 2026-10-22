// 插件日志的宿主侧路由（RFC-23 §3.2）。
//
// 四行规格照 docs/20 §3 分流：
//   info  → 通用日志（KernelLog → 常驻形态落 logs/kernel-*.log）
//   warn  → 跨进程审计流水（kind=log, level=warn）+ 通用日志
//   error → 同 warn（level=error）+ 按插件累计的错误数（诊断包摘要读它）
//   debug → 门面层就不落盘（只计数），到不了这里
//
// 为什么 warn/error 要进审计流水：docs/20:52-53 明写这两级进审计日志；而审计流水是本项目里
// 唯一跨进程、脱敏、有容量上限的证据层（RFC-20）。info 不进 —— 它量大且不是「一次决定」。
using System;
using System.Collections.Generic;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>插件日志的宿主收口。单实例、由装配层赋给 <see cref="PluginLog.Sink"/>。</summary>
internal sealed class HostPluginLog : IPluginLogSink
{
    /// <summary>审计流水里这一类的 kind（RFC-23 §8 第 2 条：对 RFC-20 §3.2 分类表的扩展）。</summary>
    public const string KindLog = "log";

    private readonly AuditTrail? _trail;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _errors = new(StringComparer.Ordinal);
    private int _written;
    private int _trailRows;

    public HostPluginLog(AuditTrail? trail) => _trail = trail;

    /// <summary>写进通用日志的条数（info + warn + error）。</summary>
    public int Written
    {
        get
        {
            lock (_gate)
            {
                return _written;
            }
        }
    }

    /// <summary>写进审计流水的行数（warn + error）。</summary>
    public int TrailRows
    {
        get
        {
            lock (_gate)
            {
                return _trailRows;
            }
        }
    }

    /// <summary>按插件累计的 error 条数（诊断包摘要读它；保持按 id Ordinal 排序）。</summary>
    public IReadOnlyList<KeyValuePair<string, int>> ErrorCounts()
    {
        lock (_gate)
        {
            var list = new List<KeyValuePair<string, int>>(_errors);
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return list;
        }
    }

    /// <summary>某个插件的 error 条数（判据读它）。</summary>
    public int ErrorCountOf(string pluginId)
    {
        lock (_gate)
        {
            return _errors.TryGetValue(pluginId, out int n) ? n : 0;
        }
    }

    /// <summary>
    /// 一行日志的落点。**绝不抛**：门面已经兜了一层，这里再兜一层是为了让
    /// 「流水写不下去」不变成「插件的调用失败」。
    /// </summary>
    public void Write(string pluginId, PluginLogLevel level, string message)
    {
        string id = string.IsNullOrEmpty(pluginId) ? PluginLog.Unattributed : pluginId;
        string text = message ?? string.Empty;

        // 通用日志：三个级别都留痕（行首字段固定，便于 grep 与诊断包对照）。
        KernelLog.Write(
            "plugin-log plugin=" + id + " level=" + Token(level) + " " + OneLine(text));

        lock (_gate)
        {
            _written++;
            if (level == PluginLogLevel.Error)
            {
                _errors.TryGetValue(id, out int n);
                _errors[id] = n + 1;
            }
        }

        AuditTrail? trail = _trail;
        if (trail is null || level < PluginLogLevel.Warn)
        {
            return;
        }

        // 没有归属的 warn/error 不进流水：审计是证据层，证据必须有归属（RFC-23 §3.4）。
        if (id == PluginLog.Unattributed)
        {
            return;
        }

        try
        {
            trail.RecordDecision(id, KindLog, "level=" + Token(level) + " " + OneLine(text));
            lock (_gate)
            {
                _trailRows++;
            }
        }
        catch (Exception)
        {
            // 记审计失败不许影响业务（RFC-20 §3.5）。
        }
    }

    /// <summary>级别的字面值（与 docs/20 §3 的方法名一致，小写）。</summary>
    public static string Token(PluginLogLevel level) => level switch
    {
        PluginLogLevel.Debug => "debug",
        PluginLogLevel.Info => "info",
        PluginLogLevel.Warn => "warn",
        _ => "error",
    };

    /// <summary>
    /// 压成一行：日志行是按行读的，插件给一段多行文本会把一条日志撑成好几行，
    /// 后面的行就会看起来像「没有 plugin= 前缀的野行」。
    /// </summary>
    private static string OneLine(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        return text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0
            ? text
            : text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
    }
}
