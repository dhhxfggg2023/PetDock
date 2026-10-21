// 宿主侧的审计流水接线（RFC-20 §3.3）。
//
// 这一半讲的是**宿主自己**的两类决定：授权询问的结果与插件页复核的收窄结果。
// 沙箱侧的那几类（授予 / 拒绝 / 撤销 / 越限 / 故障）由能力闸门发事件、
// 由事件落点转进流水（HostEventSink.cs），宿主这一侧不需要重复记。
//
// 为什么要记这两类：闸门不知道「用户被问过什么、答了什么」——
// 它只知道自己拿到了哪些授予。「这次问过了、批了哪些、拒了哪些」这个**决定**
// 今天只活在 ConsentMemory（内存）里，进程一退就没了。RFC-19 落盘的是
// 「**当前授予集**」（状态），本文件落的是「**每一次决定**」（事件），
// 两者形状不同、职责不同。
using System;
using System.Collections.Generic;

namespace PetDock.Hosting;

public sealed partial class PluginHost
{
    /// <summary>跨进程审计流水（可空：没接就没有流水，行为与今天一致）。</summary>
    private AuditTrail? _trail;

    /// <summary>
    /// 接上审计流水。**必须在 Start() 之前调**——
    /// 装载期就会发授予事件，那时流水还没接上，那批授权证据就永远丢了
    /// （与 RFC-19 §2.3 第 1 点「播种必须在 Start 之前」同一纪律）。
    /// 已在运行的句柄不会被回补（那要跨线程问调度线程，装载期调这里会把自己等死）——
    /// 装载期接上就覆盖了全部句柄，热插拔进来的插件走同一条装载路径。
    /// </summary>
    internal void AttachAuditTrail(AuditTrail? trail)
    {
        _trail = trail;
    }

    /// <summary>宿主侧记一条流水（绝不抛给调用点）。</summary>
    internal void RecordAuditDecision(string pluginId, string kind, string detail)
    {
        AuditTrail? trail = _trail;
        if (trail is null)
        {
            return;
        }

        try
        {
            trail.RecordDecision(pluginId, kind, detail);
        }
        catch (Exception)
        {
            // 记审计失败不许影响业务（RFC-20 §3.5）。
        }
    }

    /// <summary>
    /// 记一条授权询问的结果：问了哪些、批了哪些、拒了哪些、作用域是什么。
    /// 作用域用 words 表述（记住 / 仅本次），不写枚举名 —— 流水是给人看的。
    /// </summary>
    internal void RecordConsentDecision(
        string pluginId,
        IReadOnlyList<string> asked,
        IReadOnlyList<string> granted,
        IReadOnlyList<string> declined,
        string scope)
    {
        string detail = "asked=" + Join(asked)
            + " granted=" + Join(granted)
            + " declined=" + Join(declined)
            + " scope=" + scope;
        RecordAuditDecision(pluginId, AuditTrail.KindConsent, detail);
    }

    /// <summary>记一条收窄的结果：留下了哪些、撤了哪些。</summary>
    internal void RecordRestrictDecision(string pluginId, IReadOnlyList<string> kept, IReadOnlyList<string> revoked)
    {
        string detail = "kept=" + Join(kept) + " revoked=" + Join(revoked);
        RecordAuditDecision(pluginId, AuditTrail.KindRestrict, detail);
    }

    private static string Join(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            return "(none)";
        }

        var parts = new List<string>(ids.Count);
        foreach (string id in ids)
        {
            parts.Add(id);
        }

        return string.Join(",", parts);
    }
}
