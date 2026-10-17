using System.Text.Json.Nodes;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

public enum CapabilityVerdictKind { Accepted, Refused }

/// <summary>Verdict for one RequestCapability call (contract §4).</summary>
public readonly record struct CapabilityVerdict(CapabilityVerdictKind Kind, string Reason, long LatencyMs, int Seq);

/// <summary>One audit line — exact format from contract §4 (fixed field order, 6 fields).</summary>
public sealed record CapabilityAuditLine(int Seq, string Capability, string Verdict, string Reason, long AtMs, long LatencyMs)
{
    public override string ToString() =>
        $"capability-audit seq={Seq} cap={Capability} verdict={Verdict} reason={Reason} at={AtMs} latency={LatencyMs}";
}

/// <summary>
/// Capability broker: declared-is-ceiling, runtime confirms or refuses (contract §4 ruling).
/// Clock is injected — this type never reads Stopwatch/TickCount itself.
/// </summary>
public sealed class CapabilityBroker
{
    public const int PendingTimeoutMs = 200;

    private readonly List<CapabilityAuditLine> _audit = new();
    private readonly List<PendingCapability> _pending = new();
    private readonly HashSet<string> _declared;
    private int _seq;

    public CapabilityBroker(IEnumerable<string>? declared = null) =>
        _declared = declared is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(declared, StringComparer.Ordinal);

    public IReadOnlyList<CapabilityAuditLine> Audit => _audit;

    /// <summary>Request a capability now (caller supplies nowMs). Settles timed-out pending requests first.</summary>
    public CapabilityVerdict Request(string capability, string? justification, long nowMs)
    {
        int seq = ++_seq;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            PendingCapability p = _pending[i];
            long latency = nowMs - p.RequestedAtMs;
            if (latency > PendingTimeoutMs)
            {
                _pending.RemoveAt(i);
                Record(p.Seq, p.Capability, new CapabilityVerdict(CapabilityVerdictKind.Refused, "timeout", latency, p.Seq), p.RequestedAtMs);
            }
        }

        if (string.IsNullOrWhiteSpace(capability) || !CapabilityIds.IsWellKnown(capability))
        {
            return Decide(seq, capability ?? "", "not-whitelisted", nowMs);
        }
        if (!_declared.Contains(capability))
        {
            return Decide(seq, capability, "not-declared", nowMs);
        }
        return Decide(seq, capability, "-", nowMs, accepted: true);
    }

    /// <summary>Leave a request pending — the timeout negative test resolves it by a later Request.</summary>
    public void Defer(string capability, long nowMs) =>
        _pending.Add(new PendingCapability(++_seq, capability, nowMs));

    private CapabilityVerdict Decide(int seq, string capability, string reason, long nowMs, bool accepted = false)
    {
        var v = new CapabilityVerdict(accepted ? CapabilityVerdictKind.Accepted : CapabilityVerdictKind.Refused, reason, 0, seq);
        Record(seq, capability, v, nowMs);
        return v;
    }

    private void Record(int seq, string capability, CapabilityVerdict v, long atMs) =>
        _audit.Add(new CapabilityAuditLine(seq, capability,
            v.Kind == CapabilityVerdictKind.Accepted ? "accepted" : "refused", v.Reason, atMs, v.LatencyMs));

    private sealed record PendingCapability(int Seq, string Capability, long RequestedAtMs);
}
