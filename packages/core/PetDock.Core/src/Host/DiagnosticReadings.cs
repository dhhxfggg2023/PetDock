// 诊断包要收的读数（RFC-17 §3.2）：**全部现成**，不引入新的测量链。
//
// 为什么是纯数据：诊断包是本机产物，读数采集与包格式解耦，
// 于是「组装」这一侧可以被判据单独验证（造一份读数 → 装包 → 逐条断言），
// 而不必真的把宿主跑起来。
//
// 诚实性纪律（docs/18 §2.4「不得编造数字」）：
//   内存读数一律是**进程级**的（宠物与内核同进程，docs/10 §3，单只宠物的独占内存不存在）；
//   帧耗时取自帧泵的**累计值**（累计平均），不是性能页那次 60 秒采样；
//   没有宠物时实例一节就是空的，不填任何占位数字。
using System.Collections.Generic;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

/// <summary>插件页在诊断包里的一行。</summary>
internal sealed record DiagnosticPluginRow(
    string Id,
    string Name,
    string Version,
    int Tier,
    string State,
    int Granted,
    IReadOnlyList<string> DeclaredCapabilityIds,
    IReadOnlyList<string> GrantedCapabilityIds,
    IReadOnlyList<string> DeniedCapabilityIds);

/// <summary>宠物实例在诊断包里的一行。</summary>
internal sealed record DiagnosticInstanceRow(
    string Id,
    string SourceKind,
    string State,
    string Tier,
    long Frames,
    long Drops,
    long Ticks,
    int BrainFaults,
    string LastFailure,
    string Transitions);

/// <summary>
/// 审计事件在诊断包里的一行。
/// Timestamp 是 RFC-20 带来的那一半：环形缓冲里的事件本来就没有时间戳，
/// 那正是「查不清」的另一半原因。回退到环形缓冲时这一格是空串。
/// </summary>
internal sealed record DiagnosticAuditRow(string PluginId, string Kind, string Detail, string Timestamp = "");

/// <summary>
/// 跨进程审计流水的摘要（RFC-20 §3.4）。
/// 记「规模与范围」，并**如实标注**是否存在未完成的 .part 残留、
/// 以及流水本身是否可用 —— 不可用时诊断包要写明并回退，绝不假装全。
/// </summary>
internal sealed record DiagnosticTrailSummary(
    bool Available,
    int Rows,
    int Files,
    int Incomplete,
    int Dropped,
    string Note)
{
    /// <summary>没有流水时的诚实形态。</summary>
    public static DiagnosticTrailSummary Unavailable { get; } = new(
        Available: false,
        Rows: 0,
        Files: 0,
        Incomplete: 0,
        Dropped: 0,
        Note: I18n.Get("host.diag.trail.unavailable"));
}

/// <summary>
/// 插件日志的读数（RFC-23 / docs/20 §3）：写了多少条、其中多少条进了审计流水、
/// 以及**按插件**的错误条数。计数来自唯一的收口实例（<see cref="HostPluginLog"/>），
/// 诊断包不另起一份计数 —— 两份计数迟早会不一致，而不一致时没人知道信哪一份。
/// </summary>
internal sealed record DiagnosticPluginLog(
    int Written,
    int TrailRows,
    IReadOnlyList<KeyValuePair<string, int>> Errors,
    bool Connected = true)
{
    /// <summary>没接收口时的诚实形态（写「未接」，绝不写 0 —— 0 会被读成「测过是 0」）。</summary>
    public static DiagnosticPluginLog Empty { get; } =
        new(0, 0, new List<KeyValuePair<string, int>>(), Connected: false);
}

/// <summary>一次导出要收的全部读数。</summary>
internal sealed record DiagnosticReadings(
    string ProgramVersion,
    long UptimeMs,
    string ProcessMemory,
    int FrameCount,
    double AvgFrameMs,
    double MaxFrameMs,
    long OverBudgetFrames,
    long FrameElapsedMs,
    double TargetFrameMs,
    string FrameBudgetLine,
    bool RosterUnavailable,
    IReadOnlyList<DiagnosticPluginRow> Plugins,
    IReadOnlyList<DiagnosticInstanceRow> Instances,
    IReadOnlyList<DiagnosticAuditRow> Audit,
    int AuditCount,
    int DeniedCount,
    int BudgetCount,
    int GuestFaultCount,
    DiagnosticTrailSummary? Trail = null,
    string ThrottleTier = "",
    long ThrottleTicks = 0,
    long ThrottleSubmitted = 0,
    long ThrottleSkipped = 0,
    DiagnosticPluginLog? PluginLogSummary = null)
{
    /// <summary>
    /// RFC-21 §3.4 / docs/12 §4：当前档位是否是「真的读过」。
    /// 空串 = 没有帧循环（诊断包要写「未接」，绝不能把空串当 active）。
    /// </summary>
    public bool HasThrottle => ThrottleTier.Length > 0;

    /// <summary>空读数（没有宠物也没有插件时的诚实形态：一个数字都不编）。</summary>
    public static DiagnosticReadings Empty { get; } = new(
        ProgramVersion: "(unknown)",
        UptimeMs: 0,
        ProcessMemory: I18n.Get("host.diag.memory.unavailable"),
        FrameCount: 0,
        AvgFrameMs: 0,
        MaxFrameMs: 0,
        OverBudgetFrames: 0,
        FrameElapsedMs: 0,
        TargetFrameMs: 0,
        FrameBudgetLine: I18n.Get("host.diag.framebudget.unavailable"),
        RosterUnavailable: false,
        Plugins: new List<DiagnosticPluginRow>(),
        Instances: new List<DiagnosticInstanceRow>(),
        Audit: new List<DiagnosticAuditRow>(),
        AuditCount: 0,
        DeniedCount: 0,
        BudgetCount: 0,
        GuestFaultCount: 0,
        Trail: DiagnosticTrailSummary.Unavailable);
}
