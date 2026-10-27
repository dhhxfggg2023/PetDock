using System.Text.Json.Nodes;

namespace PetDock.Core.PetRuntime;

/// <summary>Event catalog: the only event names the bus will ever dispatch (M4 contract §2).</summary>
/// <remarks>Authoritative list lives in 原规格 16 §2-§3. Outside-catalog names are rejected fail-closed.</remarks>
public static class EventCatalog
{
    public const string ClockTick = "core.clock.tick";
    public const string WindowDpiChanged = "core.window.dpiChanged";
    public const string WindowMoved = "core.window.moved";
    public const string WindowFocus = "core.window.focus";
    public const string HostSuspend = "host.suspend";
    public const string HostResume = "host.resume";
    public const string PetStateCommit = "pet.state.commit";
    public const string PetStateMigrated = "pet.state.migrated";
    public const string HostCanceled = "host.canceled";
    public const string PointerMove = "input.pointer.move";
    public const string PointerDown = "input.pointer.down";
    public const string PointerUp = "input.pointer.up";
    public const string KeyDown = "input.key.down";
    public const string KeyUp = "input.key.up";

    /// <summary>Dispatch mode per catalog name (contract §2 table column Mode).</summary>
    public enum DispatchMode { Emit, Parallel, Serial, Bail, Waterfall }

    public static readonly IReadOnlyList<string> Names = new[]
    {
        ClockTick, WindowDpiChanged, WindowMoved, WindowFocus,
        HostSuspend, HostResume, PetStateCommit, PetStateMigrated, HostCanceled,
        PointerMove, PointerDown, PointerUp, KeyDown, KeyUp,
    };

    private static readonly Dictionary<string, DispatchMode> Modes = new(StringComparer.Ordinal)
    {
        [ClockTick] = DispatchMode.Serial,
        [WindowDpiChanged] = DispatchMode.Serial,
        [WindowMoved] = DispatchMode.Parallel,
        [WindowFocus] = DispatchMode.Parallel,
        [HostSuspend] = DispatchMode.Bail,
        [HostResume] = DispatchMode.Parallel,
        [PetStateCommit] = DispatchMode.Waterfall,
        [PetStateMigrated] = DispatchMode.Parallel,
        [HostCanceled] = DispatchMode.Parallel,
        [PointerMove] = DispatchMode.Serial,
        [PointerDown] = DispatchMode.Emit,
        [PointerUp] = DispatchMode.Emit,
        [KeyDown] = DispatchMode.Emit,
        [KeyUp] = DispatchMode.Emit,
    };

    /// <summary>Allowed payload keys per name; null = no payload. camelCase per contract §2.3.</summary>
    private static readonly Dictionary<string, IReadOnlySet<string>> Payloads = new(StringComparer.Ordinal)
    {
        [ClockTick] = Set("dt"),
        [WindowDpiChanged] = Set("scale", "rect"),
        [WindowMoved] = Set("windowId", "position"),
        [WindowFocus] = Set("focused"),
        [HostSuspend] = Set("reason"),
        [HostResume] = Set("reason"),
        [PetStateCommit] = Set("petId"),
        [PetStateMigrated] = Set("from", "to"),
        [HostCanceled] = Set("sessionId", "reason"),
        [PointerMove] = Set("pos", "velocity"),
        [PointerDown] = Set("button", "windowPoint"),
        [PointerUp] = Set("button", "windowPoint"),
        [KeyDown] = Set("vkey", "modifiers"),
        [KeyUp] = Set("vkey", "modifiers"),
    };

    public static bool Contains(string type) => Modes.ContainsKey(type);

    public static bool ModeOf(string type, out DispatchMode mode) => Modes.TryGetValue(type, out mode);

    public static IReadOnlySet<string>? PayloadKeysOf(string type) =>
        Payloads.TryGetValue(type, out var keys) ? keys : null;

    /// <summary>Validates payload keys against the schema; returns violation count (contract §2.3).</summary>
    public static int CountPayloadViolations(string type, JsonObject? payload)
    {
        if (payload is null) return 0;
        if (!Payloads.TryGetValue(type, out var allowed)) return payload.Count;
        int violations = 0;
        foreach (var key in payload)
            if (!allowed.Contains(key.Key)) violations++;
        return violations;
    }

    private static IReadOnlySet<string> Set(params string[] keys) => new HashSet<string>(keys, StringComparer.Ordinal);
}
