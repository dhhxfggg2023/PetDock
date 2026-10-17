using System.Text.Json.Nodes;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>Immutable snapshot of the rounds completed so far in one serial dispatch (contract §1.4).</summary>
public sealed record DispatchRound(int Index, IReadOnlyList<string> Returned, JsonObject? Latest);

/// <summary>
/// Listener signature shared by all five dispatch modes (contract §1). Serial passes the
/// immutable snapshot of the rounds completed before this listener; every other mode passes null.
/// A return value is only meaningful to the mode that reads it (serial log, bail truthiness,
/// waterfall payload chain).
/// </summary>
public delegate object? EventListener(PetEvent evt, DispatchRound? round);

/// <summary>
/// Event bus: the five dispatch modes (contract §1) over the catalog-gated log in HostEventDispatch.
/// Every mode contains listener faults: log + count, never escape, never stop the frame.
/// </summary>
public sealed class EventBus
{
    private readonly HostEventDispatch _log;
    private readonly Dictionary<string, List<EventListener>> _subs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
    private long _dispatchFaults;

    public EventBus(HostEventDispatch log) => _log = log ?? throw new ArgumentNullException(nameof(log));

    public long DispatchFaults => _dispatchFaults;

    public void Subscribe(string type, EventListener handler)
    {
        if (!EventCatalog.Contains(type))
        {
            return; // catalog-closed: unregistered names get no listeners (contract §2.2)
        }
        if (!_subs.TryGetValue(type, out var list)) _subs[type] = list = new List<EventListener>();
        list.Add(handler);
    }

    public int ListenerCount(string type) => _subs.TryGetValue(type, out var list) ? list.Count : 0;

    /// <summary>Catalog-gated publish with optional idempotency key; duplicate keys are dropped.</summary>
    public bool Publish(PetEvent evt, string? idempotencyKey = null)
    {
        if (!EventCatalog.Contains(evt.Type)) return false; // gate lives in HostEventDispatch (reject line there)
        if (idempotencyKey is not null && !_emitted.Add(idempotencyKey)) return false;
        _log.Publish(evt);
        return true;
    }

    /// <summary>emit: all listeners in order; a fault is logged and the rest still run.</summary>
    public (int ran, int faults, bool afterFaultRan) Emit(PetEvent evt)
    {
        // ran = **被调用过**的监听者数（契约 §1.4 逐字 `listeners=3 ran=3 faults=1`）：
        // 抛异常的那位也被调用过，所以它同时计入 ran 与 faults，两者互不排斥。
        int ran = 0, faults = 0; bool after = false;
        foreach (EventListener h in ListenersOf(evt.Type))
        {
            ran++;
            try { h(evt, null); if (faults > 0) after = true; }
            catch (Exception ex) { faults++; Fault($"dispatch-emit-failed listener={Name(h)} {ex.GetType().Name}: {ex.Message}"); }
        }
        return (ran, faults, after);
    }

    /// <summary>parallel: every listener runs, faults contained.</summary>
    public (int ran, int faults) Parallel(PetEvent evt)
    {
        // ran 的口径同 Emit：被调用过即算，异常另计 faults。
        int ran = 0, faults = 0;
        foreach (EventListener h in ListenersOf(evt.Type))
        {
            ran++;
            try { h(evt, null); }
            catch (Exception ex) { faults++; Fault($"dispatch-parallel-failed listener={Name(h)} {ex.GetType().Name}: {ex.Message}"); }
        }
        return (ran, faults);
    }

    /// <summary>
    /// serial: each listener is called with the immutable snapshot of the rounds completed before it.
    /// </summary>
    public (int rounds, bool sawPrevious) Serial(PetEvent evt)
    {
        var subs = ListenersOf(evt.Type);
        var returned = new List<string>();
        JsonObject? latest = null;
        bool sawPrevious = false;
        for (int i = 0; i < subs.Count; i++)
        {
            var round = new DispatchRound(i, returned.ToArray(), latest); // frozen before the call
            try
            {
                if (i > 0 && (returned.Count > 0 || latest is not null)) sawPrevious = true;
                object? result = subs[i](evt, round);
                if (result is string s) returned.Add(s);
                else if (result is JsonObject o) latest = o;
            }
            catch (Exception ex) { Fault($"dispatch-serial-failed listener={Name(subs[i])} {ex.GetType().Name}: {ex.Message}"); }
        }
        return (subs.Count, sawPrevious);
    }

    /// <summary>bail: stop at the first truthy result; an exception counts as truthy (fail-closed).</summary>
    public (int ran, bool returned, int stoppedAt, bool faultSeen) Bail(PetEvent evt)
    {
        var subs = ListenersOf(evt.Type);
        for (int i = 0; i < subs.Count; i++)
        {
            object? result;
            try { result = subs[i](evt, null); }
            catch (Exception ex)
            {
                Fault($"dispatch-bail-failed listener={Name(subs[i])} {ex.GetType().Name}: {ex.Message}");
                return (i + 1, true, i, true);
            }
            if (result is bool b && b) return (i + 1, true, i, false);
            if (result is string s && s.Length > 0) return (i + 1, true, i, false);
        }
        return (subs.Count, false, -1, false);
    }

    /// <summary>waterfall: payload flows hop to hop; a faulting hop is skipped and the payload passes through.</summary>
    public (int hops, JsonObject? final, bool mutatedEachHop) Waterfall(PetEvent evt)
    {
        var subs = ListenersOf(evt.Type);
        JsonObject? payload = evt.Payload;
        bool mutated = false; int hops = 0;
        foreach (EventListener h in subs)
        {
            try
            {
                // 每一跳拿到的是**当前**载荷（可以是上一跳的产物），不是最初那个 ——
                // 「payload 逐个传递」是 waterfall 与其它四种模式的唯一区别（契约 §1.1）。
                var hop = new PetEvent(evt.Type, evt.At, payload, evt.Trace);
                if (h(hop, null) is JsonObject next && !ReferenceEquals(next, payload)) { payload = next; mutated = true; }
                hops++;
            }
            catch (Exception ex) { Fault($"dispatch-waterfall-failed listener={Name(h)} {ex.GetType().Name}: {ex.Message}"); }
        }
        return (hops, payload, mutated);
    }

    private static string Name(EventListener h) =>
        h.Method.Name.Length == 0 ? "<anon>" : h.Method.Name;

    private List<EventListener> ListenersOf(string type) =>
        _subs.TryGetValue(type, out var list) ? list : new List<EventListener>();

    private void Fault(string message)
    {
        _dispatchFaults++;
        KernelLog.Write(message);
    }
}
