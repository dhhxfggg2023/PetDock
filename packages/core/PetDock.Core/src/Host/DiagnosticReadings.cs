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

/// <summary>沙箱事件在诊断包里的一行。</summary>
internal sealed record DiagnosticAuditRow(string PluginId, string Kind, string Detail);

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
    int GuestFaultCount)
{
    /// <summary>空读数（没有宠物也没有插件时的诚实形态：一个数字都不编）。</summary>
    public static DiagnosticReadings Empty { get; } = new(
        ProgramVersion: "(unknown)",
        UptimeMs: 0,
        ProcessMemory: "(进程级读数不可用)",
        FrameCount: 0,
        AvgFrameMs: 0,
        MaxFrameMs: 0,
        OverBudgetFrames: 0,
        FrameElapsedMs: 0,
        TargetFrameMs: 0,
        FrameBudgetLine: "(帧预算读数不可用)",
        RosterUnavailable: false,
        Plugins: new List<DiagnosticPluginRow>(),
        Instances: new List<DiagnosticInstanceRow>(),
        Audit: new List<DiagnosticAuditRow>(),
        AuditCount: 0,
        DeniedCount: 0,
        BudgetCount: 0,
        GuestFaultCount: 0);
}
