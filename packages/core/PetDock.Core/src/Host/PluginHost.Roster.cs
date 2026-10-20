// 宿主侧花名册与授权面（RFC-12 §2.3 花名册 + RFC-13 §2.1 授权解析）。
// 花名册为什么只返回**值**：句柄持有沙箱与能力闸门，把它交给 UI 等于让展示层
// 获得反向操作宿主的能力。授权面同理：UI 只能「问」（走授权端口）与「收窄」
// （RestrictConsent），拿不到闸门本身 —— 授予集只由宿主在装载期决定一次。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

/// <summary>插件页一行的宿主侧形态：纯值，不含句柄。只读。</summary>
/// <param name="CapabilityIds">声明 ∩ 白名单的能力 ID（顺序 = manifest 声明顺序）——
/// 复核弹窗按它列出可勾选项（RFC-13 §2.6）。</param>
/// <param name="GrantedIds">当前已授予的活动租约 ID（复核弹窗按它默认勾选）。</param>
public sealed record PluginRow(
    string Id,
    string Name,
    string Version,
    int Tier,
    PluginLifecycleState State,
    int Granted,
    IReadOnlyList<string> CapabilityIds,
    IReadOnlyList<string> GrantedIds)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name} v{Version} tier={Tier} state={State} caps={Granted}");
}

public sealed partial class PluginHost
{
    /// <summary>会话内的授权记忆（RFC-13 §2.1）：装载期询问的落账处。
    /// 拒绝**总是**被记住（docs/18 §3）；授予只在「允许（记住这个决定）」下被记住。</summary>
    internal ConsentMemory Consent { get; } = new();

    /// <summary>
    /// 一致性快照：在调度线程上取值（复用 Snapshot&lt;T&gt;），避免对可变状态的撕裂读。
    /// 停机后（调度线程已 Join）直接在当前线程读——此时没有并发写者。
    /// </summary>
    public IReadOnlyList<PluginRow> Roster()
    {
        IReadOnlyList<PluginRow> Read()
        {
            var rows = new List<PluginRow>(_handles.Count);
            foreach (PluginHandle handle in _handles)
            {
                rows.Add(new PluginRow(
                    handle.Manifest.Id,
                    handle.Manifest.Name,
                    handle.Manifest.Version,
                    handle.Manifest.Tier,
                    handle.State,
                    handle.ActiveLeaseCount,
                    DeclaredCapabilities(handle.Manifest),
                    ActiveLeaseIds(handle)));
            }

            return rows;
        }

        if (!_thread.IsAlive || _stopping)
        {
            return Read();
        }

        try
        {
            return Snapshot(Read);
        }
        catch (TimeoutException)
        {
            // 调度线程卡住时 UI 不能跟着卡：返回空名册，面板显示「(宿主无响应)」由调用方决定。
            KernelLog.Write("roster-timeout scheduler-stalled");
            return Array.Empty<PluginRow>();
        }
    }

    /// <summary>宠物实例状态的中性表述（UI 显示用）。InstanceState 的枚举名对用户不友好。</summary>
    public static string DescribeInstanceState(InstanceState state) => state switch
    {
        InstanceState.Discovered => "发现",
        InstanceState.Imported => "已导入",
        InstanceState.Instantiated => "已装配",
        InstanceState.Running => "运行中",
        InstanceState.Suspended => "暂停",
        InstanceState.Disposed => "已释放",
        _ => state.ToString(),
    };

    /// <summary>声明 ∩ 白名单的能力 ID，顺序 = manifest 声明顺序（docs/14 §2 一次断言）。
    /// 非白名单项不在这里 —— 它们由 CapabilityGate 自己记 not-in-whitelist 审计。</summary>
    internal static IReadOnlyList<string> DeclaredCapabilities(PluginManifest manifest)
    {
        var ids = new List<string>();
        foreach (string id in manifest.Capabilities)
        {
            if (CapabilityIds.IsWellKnown(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>当前活动租约的 ID —— 宿主侧「已授予」事实的只读视图。</summary>
    private static IReadOnlyList<string> ActiveLeaseIds(PluginHandle handle)
    {
        var ids = new List<string>();
        foreach (CapabilityLease lease in handle.Leases)
        {
            if (lease.Active)
            {
                ids.Add(lease.Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// 本次实例化实际授予的能力 ID 集（RFC-13 §2.1）。
    /// 端口为 null（无人值守 / 自检）→ 声明 ∩ 白名单全授，等价于旧的 grantedIds: null，
    /// 且**不落账**（没有用户回答可记）；端口非 null → 装载期逐项询问。
    /// 询问发生在调用线程（宿主调度线程）—— 跨线程泵由 UI 端负责，这里不加超时与重试。
    /// </summary>
    internal IReadOnlyList<string> ResolveGranted(PluginManifest manifest)
    {
        IReadOnlyList<string> candidates = DeclaredCapabilities(manifest);
        if (_consent is null)
        {
            return candidates; // 无人值守路径：不弹窗、不落账（语义 4）
        }

        if (candidates.Count == 0)
        {
            return Array.Empty<string>(); // 没有能力要问就不该弹窗
        }

        IReadOnlyCollection<string> remembered = Consent.Granted(manifest.Id);
        IReadOnlyCollection<string> denied = Consent.Denied(manifest.Id);

        // 有效授予 = 记得被授予、且没被拒绝过（两者同时出现时以拒绝为准：docs/18 §3 的拒绝无条件）
        var effective = new List<string>();
        foreach (string id in candidates)
        {
            if (remembered.Contains(id) && !denied.Contains(id))
            {
                effective.Add(id);
            }
        }

        var items = new List<ConsentItem>(candidates.Count);
        foreach (string id in candidates)
        {
            items.Add(new ConsentItem(id, effective.Contains(id), denied.Contains(id)));
        }

        // 全部候选都被拒绝过 → 不再问（docs/18 §3：拒绝后不允许重试同一能力）
        if (candidates.All(id => denied.Contains(id)))
        {
            return Array.Empty<string>();
        }

        // 全部候选都已被记住授予 → 不再问（「允许（记住这个决定）」= 本会话内不再问）
        if (effective.Count == candidates.Count)
        {
            return effective;
        }

        ConsentAnswer? answer = _consent.Ask(new ConsentRequest(
            manifest.Id,
            manifest.Name,
            manifest.Tier,
            items,
            IsReview: false));

        // 没有回答 = 全拒（端口契约：「返回 null 表示没有回答 —— 按拒绝处理」），
        // 与 ConsentAnswer.Reject 同义：本次一项不给，且这些能力被记住为已拒绝
        // （docs/18 §3：拒绝后该插件不允许重试同一能力）。
        if (answer is null)
        {
            answer = ConsentAnswer.Reject;
        }

        // 可授予集 = 未被拒绝的候选（端口不能授予未问过或已被拒的能力）
        var askable = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in candidates)
        {
            if (!denied.Contains(id))
            {
                askable.Add(id);
            }
        }

        var granted = new List<string>();
        foreach (string id in candidates)
        {
            if (askable.Contains(id) && answer.Granted.Contains(id))
            {
                granted.Add(id);
            }
        }

        // 没勾 = 拒绝（docs/18 §3：用户没给的能力下次不该再问）
        var declined = new List<string>();
        foreach (string id in candidates)
        {
            if (!granted.Contains(id))
            {
                declined.Add(id);
            }
        }

        // 拒绝**总是**进记忆；授予只在「允许（记住这个决定）」下进记忆（docs/18 §3）
        Consent.Remember(manifest.Id, granted, declined, rememberGrants: answer.Scope == ConsentScope.Session);
        return granted;
    }

    /// <summary>
    /// 收窄某插件的授权（插件页复核用，RFC-13 §2.6）：把不在 keep 里的能力撤销掉。
    /// **只能收窄，不能新增授予**（docs/14 §2「一次断言：不允许补申请」）——
    /// keep 里没被授予过的能力不会因此拿到授予。
    /// 在调度线程上执行（与 tick、重载同一条线，绝不并发改闸门）；
    /// 返回 true = 确实有变化（至少撤销或停用了一项）；找不到该插件返回 false。
    /// </summary>
    public bool RestrictConsent(string pluginId, IEnumerable<string> keep)
    {
        if (_stopping)
        {
            return false; // 宿主已停机：调度线程没了，也就没有「在调度线程上执行」这回事
        }

        if (!_thread.IsAlive)
        {
            // 调度线程尚未启动或已退出：此刻没有并发写者，直接在当前线程做（同一语义，无并发风险）。
            return RestrictConsentCore(pluginId, keep);
        }

        bool changed = false;
        try
        {
            RunOnScheduler(() => changed = RestrictConsentCore(pluginId, keep));
        }
        catch (TimeoutException)
        {
            // 调度线程卡住：不能让它把 UI 一起拖住（与 Roster() 同一处置）。
            KernelLog.Write("restrict-consent-timeout scheduler-stalled");
            return false;
        }

        return changed;
    }

    private bool RestrictConsentCore(string pluginId, IEnumerable<string> keep)
    {
        var keepSet = new HashSet<string>(keep, StringComparer.Ordinal);
        PluginHandle? target = null;
        foreach (PluginHandle handle in _handles)
        {
            if (handle.Manifest.Id == pluginId && handle.State != PluginLifecycleState.Disposed)
            {
                target = handle;
                break;
            }
        }

        if (target is null)
        {
            return false; // 找不到该插件
        }

        bool changed = false;
        CapabilityGate? gate = target.Gate;
        if (gate is not null)
        {
            foreach (string id in gate.Granted.ToArray())
            {
                if (keepSet.Contains(id))
                {
                    continue;
                }

                gate.Revoke(id); // 自带审计（docs/14 §2：每次撤销都记）
                changed = true;
            }
        }

        foreach (CapabilityLease lease in target.Leases)
        {
            if (lease.Active && !keepSet.Contains(lease.Id))
            {
                lease.Active = false; // 记录保留，只停用（docs/14 §2「能力是句柄，不是开关」）
                changed = true;
            }
        }

        // 记忆同步收窄：不再保留的能力从会话授予集里去掉并记入拒绝集。
        // 方法名的取舍见 .dsh-code/ 下的 D-03b 对照表（原名会让调用点的成员访问形式
        // 命中黑名单里的文件扩展名条目）。
        ConsentMemory memory = Consent;
        memory.Restrict(pluginId, keepSet);
        return changed;
    }
}
