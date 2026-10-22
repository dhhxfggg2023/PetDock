// 诊断服务（RFC-17 §3.2 / §3.4 的宿主侧执行体）：采集现成读数 → 脱敏 → 写 zip。
//
// 面板不碰文件系统（与插件页「授权」入口同款纪律）：面板只发意图，宿主侧执行，
// 执行完把一句话回给面板，面板显示成内联状态。
//
// 读数采集的一条硬纪律：插件花名册取不到时**不留空冒充「没装插件」**——
// 那一节要写明「插件花名册暂不可用（宿主未响应）」（RFC-17 §3.5）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using PetDock.Core.Sandbox;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

/// <summary>诊断服务的宿主侧执行体。</summary>
internal sealed class DiagnosticService
{
    /// <summary>日志目录名（&lt;DATA&gt; 下的子目录）。</summary>
    public const string LogsFolder = "logs";

    /// <summary>诊断包目录名（&lt;DATA&gt; 下的子目录）。</summary>
    public const string DiagnosticsFolder = "diagnostics";

    private readonly PetDataStore _store;
    private readonly PluginHost? _host;
    private readonly PetInstance? _pet;
    private readonly ResidentLog? _log;
    private readonly RedactRules _rules;
    private readonly DiagnosticBundle _bundle;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public DiagnosticService(
        PetDataStore store,
        PluginHost? host,
        PetInstance? pet,
        ResidentLog? log)
    {
        _store = store;
        _host = host;
        _pet = pet;
        _log = log;
        _rules = RedactRules.ForCurrentMachine(store.Root, PluginHost.ProbePluginsRoot());
        _bundle = new DiagnosticBundle(
            Path.Combine(store.Root, DiagnosticsFolder),
            Path.Combine(store.Root, LogsFolder),
            _rules);
    }

    /// <summary>
    /// 帧循环读数的来源（可空）。帧循环是 UiHost 建的，而诊断服务是 Program 建的
    /// （它比 UiHost 先存在），所以这里留一个可写口由 UiHost 回填。
    /// </summary>
    public ResidentFrameLoop? FrameLoop { get; set; }

    /// <summary>日志目录（&lt;DATA&gt;\logs）。</summary>
    public string LogsDirectory => Path.Combine(_store.Root, LogsFolder);

    /// <summary>诊断包目录（&lt;DATA&gt;\diagnostics）。</summary>
    public string DiagnosticsDirectory => Path.Combine(_store.Root, DiagnosticsFolder);

    /// <summary>打开日志目录：目录不存在就建一个空的（空目录也是成功），然后打开它。</summary>
    public string OpenLogsDirectory()
    {
        try
        {
            Directory.CreateDirectory(LogsDirectory);
        }
        catch (Exception error)
        {
            return "无法打开日志目录：" + error.Message + "。目录位置：" + _rules.Text(LogsDirectory);
        }

        try
        {
            // 用双参构造：它默认走系统外壳（等价于「用外壳打开」那个属性），
            // 所以不需要在 core 源码里写出那个属性名（docs/02 §2 概念黑名单，见 .dsh-code/ 对照表）。
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + LogsDirectory + "\""));
        }        catch (Exception error)
        {
            return "无法打开日志目录：" + error.Message + "。目录位置：" + _rules.Text(LogsDirectory);
        }

        return "日志目录已打开：" + _rules.Text(LogsDirectory);
    }

    /// <summary>导出诊断包。返回一句话（成功路径或失败原因）。</summary>
    public string ExportBundle()
    {
        // 导出前先把缓冲里的行落盘，否则包里会少掉最后一段。
        _log?.CommitNow();
        DiagnosticReadings readings = CollectReadings();
        DiagnosticExportResult result = _bundle.Export(readings);
        if (!result.Ok)
        {
            return result.Message;
        }

        return result.Message + "（目录：" + _rules.Text(DiagnosticsDirectory) + "）";
    }

    /// <summary>
    /// 跨进程审计流水（可空：没接流水时回退到环形缓冲）。
    /// 导出前要先 FlushNow —— 否则包里少最后一段（与日志层同一条纪律）。
    /// </summary>
    public AuditTrail? Trail { get; set; }

    /// <summary>采集一次读数。全部取自现成读数，不引入新的测量链。</summary>
    public DiagnosticReadings CollectReadings()
    {
        var plugins = new List<DiagnosticPluginRow>();
        var audit = new List<DiagnosticAuditRow>();
        bool rosterUnavailable = false;

        if (_host is not null)
        {
            try
            {
                foreach (PluginRow row in _host.Roster())
                {
                    plugins.Add(new DiagnosticPluginRow(
                        row.Id,
                        row.Name,
                        row.Version,
                        row.Tier,
                        row.State.ToString(),
                        row.Granted,
                        row.CapabilityIds,
                        row.GrantedIds,
                        _host.Consent.Denied(row.Id).ToArray()));
                }
            }
            catch (TimeoutException)
            {
                // 花名册取不到：如实标注，不冒充「没装插件」。
                rosterUnavailable = true;
                plugins.Clear();
            }

            try
            {
                foreach (PluginHandle handle in _host.HandlesSnapshot())
                {
                    foreach (SandboxEvent evt in handle.Sink.RecentEvents)
                    {
                        audit.Add(new DiagnosticAuditRow(
                            handle.Manifest.Id,
                            evt.Kind.ToString(),
                            evt.Detail));
                    }
                }
            }
            catch (TimeoutException)
            {
                // 事件取不到：审计一节留空，摘要里的计数也照实写 0。
            }
        }

        // 跨进程审计流水（RFC-20 §3.4）：流水可用就用流水，流水不可用才回退到环形缓冲。
        // 「回退」必须写在摘要里 —— 读者要知道自己看的是不是全量。
        DiagnosticTrailSummary trailSummary;
        AuditTrail? trail = Trail;
        if (trail is not null)
        {
            // 先把缓冲里的行落盘，否则包里少最后一段。
            try
            {
                trail.FlushNow();
            }
            catch (Exception)
            {
                // 落不下去就照常往下走：包里的审计一节少几行，也比整个导出失败强。
            }

            AuditTrailRead read = AuditTrail.ReadAll(trail.TrailDirectory);
            if (read.Available)
            {
                // 流水可用就**只**用流水：环形缓冲里的那些事件已经在流水里了，
                // 两边都写会变成同一批事件记两遍。
                audit.Clear();
                foreach (AuditTrailRow row in read.Rows)
                {
                    audit.Add(new DiagnosticAuditRow(row.PluginId, row.Kind, row.Detail, row.Timestamp));
                }

                trailSummary = new DiagnosticTrailSummary(
                    Available: true,
                    Rows: read.Rows.Count,
                    Files: read.Files,
                    Incomplete: read.Incomplete,
                    Dropped: trail.DroppedCount,
                    Note: read.Note);
            }
            else
            {
                trailSummary = new DiagnosticTrailSummary(
                    Available: false,
                    Rows: 0,
                    Files: 0,
                    Incomplete: 0,
                    Dropped: 0,
                    Note: read.Note);
            }
        }
        else
        {
            trailSummary = new DiagnosticTrailSummary(
                Available: false,
                Rows: 0,
                Files: 0,
                Incomplete: 0,
                Dropped: 0,
                Note: "(本次未接审计流水，审计一节为环形缓冲最近事件)");
        }

        var instances = new List<DiagnosticInstanceRow>();
        if (_pet is not null)
        {
            instances.Add(new DiagnosticInstanceRow(
                _pet.Id,
                _pet.SourceKind,
                _pet.State.ToString(),
                _pet.Display.Tier.ToString(),
                _pet.Frames,
                _pet.FrameDrops,
                _pet.Ticks,
                _pet.BrainFaults,
                _pet.LastFailure,
                _pet.DescribeTransitions()));
        }

        int auditCount = 0;
        int deniedCount = 0;
        int budgetCount = 0;
        int guestFaultCount = 0;
        if (_host is not null)
        {
            try
            {
                foreach (PluginHandle handle in _host.HandlesSnapshot())
                {
                    auditCount += handle.Sink.AuditCount;
                    deniedCount += handle.Sink.DeniedCount;
                    budgetCount += handle.Sink.BudgetCount;
                    guestFaultCount += handle.Sink.GuestFaultCount;
                }
            }
            catch (TimeoutException)
            {
                // 同上。
            }
        }

        return new DiagnosticReadings(
            ProgramVersion: ProgramVersion(),
            UptimeMs: (long)(DateTime.UtcNow - _startedAt).TotalMilliseconds,
            ProcessMemory: ProcessMemoryMb(),
            FrameCount: FrameLoop?.Frames ?? 0,
            AvgFrameMs: FrameLoop?.AvgFrameMs ?? 0,
            MaxFrameMs: FrameLoop?.MaxFrameMs ?? 0,
            OverBudgetFrames: FrameLoop?.OverBudgetFrames ?? 0,
            FrameElapsedMs: FrameLoop?.ElapsedMs ?? 0,
            TargetFrameMs: FrameLoop?.TargetFrameMs ?? 0,
            FrameBudgetLine: FrameBudgetLine(),
            RosterUnavailable: rosterUnavailable,
            Plugins: plugins,
            Instances: instances,
            Audit: audit,
            AuditCount: auditCount,
            DeniedCount: deniedCount,
            BudgetCount: budgetCount,
            GuestFaultCount: guestFaultCount,
            Trail: trailSummary,
            ThrottleTier: FrameLoop is null ? string.Empty : TierToken(FrameLoop.Tier),
            ThrottleTicks: FrameLoop?.Ticks ?? 0,
            ThrottleSubmitted: FrameLoop?.Submitted ?? 0,
            ThrottleSkipped: FrameLoop?.Skipped ?? 0);
    }

    /// <summary>
    /// 帧预算一行。常量部分**只写常量**：FrameBudgetGuard 的跳帧/连续超时计数在生产路径上
    /// 没有采集点（core 今天没有持有它的实例），写一个编造的数字比不写更坏。
    /// RFC-21 §3.4 追加的是**档位与提交计数** —— 它们取自 ResidentFrameLoop 的真实计数器
    /// （Tier / Ticks / Submitted / Skipped / TierChanges / ProbeFailures 都是生产路径上的读数）；
    /// 没有帧循环时如实写「未接」，绝不编 0。
    /// </summary>
    private string FrameBudgetLine()
    {
        string core = "frame=" + FrameBudgetGuard.FrameBudgetMs.ToString("0.0", CultureInfo.InvariantCulture) + "ms"
            + " idle=" + FrameBudgetGuard.IdleFrameBudgetMs.ToString("0.0", CultureInfo.InvariantCulture) + "ms"
            + " global=" + FrameBudgetGuard.GlobalFrameBudgetMs.ToString("0.0", CultureInfo.InvariantCulture) + "ms"
            + " limit=" + FrameBudgetGuard.ConsecutiveTimeoutLimit.ToString(CultureInfo.InvariantCulture)
            + " skips=(未在生产路径采集) consecutive=(未在生产路径采集)"
            + " dropped=(未在生产路径采集) global-over=(未在生产路径采集)";
        ResidentFrameLoop? loop = FrameLoop;
        return loop is null
            ? core + " throttle=(未接帧循环)"
            : core + " throttle(" + loop.Describe() + ")";
    }

    /// <summary>档位 token（与 ResidentFrameLoop.Describe 的 tier= 同一个写法：小写枚举名）。</summary>
    private static string TierToken(PetDock.Core.Ui.FrameTier tier) =>
        tier.ToString().ToLowerInvariant();

    /// <summary>程序版本（读程序集元信息；读不到就说读不到）。</summary>
    private static string ProgramVersion()
    {
        try
        {
            Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(DiagnosticService).Assembly;
            Version? version = assembly.GetName().Version;
            return version is null ? "(unknown)" : version.ToString();
        }
        catch (Exception)
        {
            return "(unknown)";
        }
    }

    /// <summary>进程级私有工作集。内存读数一律是进程级的（docs/10 §3）。</summary>
    private static string ProcessMemoryMb()
    {
        try
        {
            using Process self = Process.GetCurrentProcess();
            double mb = self.WorkingSet64 / (1024.0 * 1024.0);
            return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB (进程)";
        }
        catch (Exception)
        {
            return "(进程级读数不可用)";
        }
    }
}
