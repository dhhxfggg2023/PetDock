// 宿主侧沙箱事件落点（t3 契约：事件落点属于宿主策略，沙箱只负责发）。
// 每插件一个实例：环形缓冲 + 分类计数 + KernelLog —— per-插件计数是「连续 8 次失败 →
// 自动挂起」判据（M1 契约 §5.3）的数据来源。Emit 可能从宿主调度线程（tick）与装载路径
// 并发到达，内部加锁；KernelLog 无控制台时静默丢弃（常驻模式）。
using System.Collections.Generic;
using System.Text;
using PetDock.Core.Compositor;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

internal sealed class PluginEventSink : ISandboxEventSink
{
    private const int Capacity = 256;
    private readonly object _gate = new();
    private readonly Queue<SandboxEvent> _recent = new();
    private readonly string _pluginId;

    /// <summary>
    /// 跨进程审计流水（RFC-20 §3.3 第 1 点）：环形缓冲之外的多一条去向。
    /// 可空 —— 没接流水时（自检形态、旧路径）行为与今天完全一致。
    /// 写流水绝不抛给调用点（流水自己的问题不该让一次能力判定失败）。
    /// </summary>
    internal AuditTrail? Trail { get; set; }

    public PluginEventSink(string pluginId)
    {
        _pluginId = pluginId;
    }

    public int AuditCount { get; private set; }
    public int DeniedCount { get; private set; }
    public int BudgetCount { get; private set; }
    public int GuestFaultCount { get; private set; }

    public void Emit(SandboxEvent evt)
    {
        lock (_gate)
        {
            _recent.Enqueue(evt);
            if (_recent.Count > Capacity)
            {
                _recent.Dequeue();
            }
            switch (evt.Kind)
            {
                case SandboxEventKind.Audit:
                    AuditCount++;
                    break;
                case SandboxEventKind.CapabilityDenied:
                    DeniedCount++;
                    break;
                case SandboxEventKind.BudgetExceeded:
                    BudgetCount++;
                    break;
                case SandboxEventKind.GuestFault:
                    GuestFaultCount++;
                    break;
                default:
                    break;
            }
        }
        KernelLog.Write($"sandbox plugin={_pluginId} kind={evt.Kind} {evt.Detail}");

        // 跨进程流水（RFC-20）：环形缓冲会丢、进程一退就没了，流水不会。
        // 放在锁外：写盘在后台线程上做，这里只入队。
        AuditTrail? trail = Trail;
        if (trail is not null)
        {
            trail.Record(_pluginId, evt);
        }
    }

    /// <summary>
    /// 最近的沙箱事件（环形缓冲里的那些，最多 Capacity 条）。
    /// 诊断包用它导出 audit.jsonl（RFC-17 §3.2）——
    /// 这是**最近**的事件，不是全量历史，摘要里必须如实写明。
    /// </summary>
    public IReadOnlyList<SandboxEvent> RecentEvents
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToArray();
            }
        }
    }

    /// <summary>Resume 时的句柄重发审计（宿主侧策略事件，非沙箱产生；gate 无公开 Grant，
    /// M1 的重发记录由宿主补审计 —— 见 PluginHost.ResumeHandle）。</summary>
    public void EmitRegrant(string capabilityId)
    {
        Emit(new SandboxEvent(SandboxEventKind.Audit, $"re-grant id={capabilityId} (host resume)"));
    }

    /// <summary>装载/重载失败时的人可读原因（最近若干条事件，供 PluginLoadException）。</summary>
    public string Describe()
    {
        lock (_gate)
        {
            if (_recent.Count == 0)
            {
                return "no sandbox events recorded";
            }
            StringBuilder sb = new();
            foreach (SandboxEvent evt in _recent)
            {
                sb.Append(evt.Kind).Append(": ").Append(evt.Detail).Append(" | ");
            }
            return sb.ToString(0, Math.Max(0, sb.Length - 3));
        }
    }
}
