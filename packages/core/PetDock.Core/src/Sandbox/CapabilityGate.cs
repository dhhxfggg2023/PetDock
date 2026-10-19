// M1 契约 §5（docs/handoff/M1-contract-frozen.md）：能力闸门。
// 职责：持有「已授予集合」，回答「这次调用允不允许」，并对每次判定记审计。
// docs/14 §2：白名单、最小授权、每次授予/拒绝记审计。
// docs/14 §5 越限规则：任一越限事件 → 撤销一项能力 + 拒绝下一次同类请求；
// 该「撤销一项」也走本闸门（RevokeOne），并同样记审计。
namespace PetDock.Core.Sandbox;

/// <summary>
/// 能力判定结果。允许时 <see cref="DeniedReason"/> 为 null。
/// </summary>
public readonly record struct CapabilityDecision(bool Allowed, string? DeniedReason)
{
    public static CapabilityDecision Allow() => new(true, null);
    public static CapabilityDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// 一个沙箱实例的能力闸门。线程模型：与本沙箱的 Store 同一线程使用（单线程）；
/// 内部不刻意加锁 —— 宿主契约保证同一时刻只有一个执行面在跑该客体。
/// </summary>
public sealed class CapabilityGate
{
    private readonly HashSet<string> _granted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
    private readonly ISandboxEventSink _sink;

    /// <summary>
    /// declaredIds：安装期一次性声明的全集（docs/14 §2 一次性声明）。
    /// grantedIds：本次实例化实际授予的子集（默认最小授权 = 仅声明集 ∩ 白名单，且默认全授）。
    /// 每一项授予/拒绝都会在构造期各记一条审计。
    /// </summary>
    public CapabilityGate(
        IEnumerable<string> declaredIds,
        IEnumerable<string>? grantedIds,
        ISandboxEventSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));

        foreach (var id in declaredIds)
        {
            if (!CapabilityIds.IsWellKnown(id))
            {
                _sink.Emit(new SandboxEvent(
                    SandboxEventKind.CapabilityDenied,
                    SandboxEventPayload.Build(("id", id), ("why", "not-in-whitelist"))));
                continue;
            }
            _declared.Add(id);
        }

        var grantSet = grantedIds ?? _declared;
        foreach (var id in grantSet)
        {
            if (!_declared.Contains(id))
            {
                // 未声明却请求授予 = 拒绝 + 审计（契约验收点 2 的安装期形态）。
                _sink.Emit(new SandboxEvent(
                    SandboxEventKind.CapabilityDenied,
                    SandboxEventPayload.Build(("id", id), ("why", "not-declared"))));
                continue;
            }
            _granted.Add(id);
            _sink.Emit(new SandboxEvent(
                SandboxEventKind.Audit,
                SandboxEventPayload.Build(("grant", id))));
        }
    }

    /// <summary>当前已授予集合（只读视图）。</summary>
    public IReadOnlyCollection<string> Granted => _granted;

    /// <summary>当前已声明集合（只读视图）。</summary>
    public IReadOnlyCollection<string> Declared => _declared;

    /// <summary>
    /// 判定一次能力调用。未授予即拒绝 + 记 CapabilityDenied；授予则记 Audit。
    /// 每次调用都记 —— docs/14 §2「每次授予、拒绝都记审计」的运行期形态。
    /// </summary>
    public CapabilityDecision Check(string id)
    {
        if (!_granted.Contains(id))
        {
            var why = _declared.Contains(id) ? "revoked" : "not-granted";
            _sink.Emit(new SandboxEvent(
                SandboxEventKind.CapabilityDenied,
                SandboxEventPayload.Build(("id", id), ("why", why))));
            return CapabilityDecision.Deny(why);
        }
        _sink.Emit(new SandboxEvent(
            SandboxEventKind.Audit,
            SandboxEventPayload.Build(("use", id))));
        return CapabilityDecision.Allow();
    }

    /// <summary>
    /// 越限处置的「撤销一项能力」（docs/14 §5）。
    /// 撤销指定项；若指定项不在授予集则撤销任意一项（取排序后首项，保证确定性）。
    /// 返回被撤销的 ID；授予集为空时返回 null（无可撤销，仍记审计说明）。
    /// </summary>
    public string? RevokeOne(string? preferredId)
    {
        string? victim = null;
        if (preferredId is not null && _granted.Contains(preferredId))
        {
            victim = preferredId;
        }
        else if (_granted.Count > 0)
        {
            // 确定性：按字典序取首项，避免依赖哈希表枚举顺序。
            victim = _granted.OrderBy(s => s, StringComparer.Ordinal).First();
        }

        if (victim is null)
        {
            _sink.Emit(new SandboxEvent(
                SandboxEventKind.Audit,
                SandboxEventPayload.Build(("revoke", "<none>"), ("why", "grant-set-empty"))));
            return null;
        }

        _granted.Remove(victim);
        _sink.Emit(new SandboxEvent(
            SandboxEventKind.Audit,
            SandboxEventPayload.Build(("revoke", victim))));
        return victim;
    }

    /// <summary>直接撤销一项指定能力（禁用流程用）。记审计；不在授予集时不视为错误。</summary>
    public void Revoke(string id)
    {
        if (_granted.Remove(id))
        {
            _sink.Emit(new SandboxEvent(
                SandboxEventKind.Audit,
                SandboxEventPayload.Build(("revoke", id))));
        }
    }
}
