// t1 契约 §2.1/§1 在测试侧的**只读镜像**：13 个总线事件名 + 五种 dispatch 模式 + payload 字段口径。
//
// 权威链：docs/16-behavior-events.md（冻结 v1.0，§2 八条 + §3 五条）→ t1 契约 §2.1（逐字定死，
// 含 §2.3 的 payload 编码口径与 §2.5 的 velocity 语义）→ 本表。本表**不是**第二个 owner：
// core 的 `EventCatalog`（t2 按 §2.1 落地）是运行期唯一名字表；CatalogMirrorTests 在它存在时
// 把两边逐项比对，不一致即 FAIL；它不存在时该条 SKIP 并写明原因（§2.6：SKIP 不是 PASS）。
//
// 本表存在的两个具体理由：
//   1. 夹具格式规则 R5（t1 §8.2「夹具里出现的任何事件 type 必须 ∈ §2.1 的 13 个」）需要一个
//      在**测试进程内**可读的可判定名字集 —— 不能靠读 docs/ 的 markdown；
//   2. 验收④要求逐条列出「覆盖 / 未覆盖及原因」，这需要一个逐格可勾选的坐标系（13 名字 × 5 模式）。
using System;
using System.Collections.Generic;

namespace PetDock.EventReplay.Tests;

/// <summary>docs/16 §1 的五种 dispatch 模式（t1 §1.1 给了逐条语义与失败传播）。</summary>
public enum DispatchMode
{
    Emit,
    Parallel,
    Serial,
    Bail,
    Waterfall,
}

/// <summary>payload 字段的类型口径（t1 §2.3：camelCase；整型用 int；int[2]=[x,y]；int[4]=[x,y,w,h]）。</summary>
public enum FieldKind
{
    Int,
    Double,
    Bool,
    Str,
    Int2,
    Int4,
}

/// <summary>一个 payload 字段的规则。<see cref="Optional"/> = true 的字段缺省时**不写**而不是写 null（§2.3）。</summary>
public sealed record PayloadField(string Name, FieldKind Kind, bool Optional = false);

/// <summary>一个表内事件的完整口径。</summary>
public sealed record CatalogEntry(
    string Name,
    int Section,
    DispatchMode Mode,
    IReadOnlyList<PayloadField> Payload,
    string SourceNote);

/// <summary>docs/16 §2（八条）+ §3（五条）的 13 个名字，逐字。</summary>
public static class EventCatalogTable
{
    /// <summary>t1 §2.2 的「待补表豁免」：docs/11:260 逐字要求 emit，docs/16 §2 却没列它。
    /// 补表前它算表内（只此一个，RFC-05 §B 已裁决接受）；**不得新增第二个**（§2.2 判据 2）。</summary>
    public const string PendingTableExemption = "host.canceled";

    private static readonly PayloadField[] NoFields = Array.Empty<PayloadField>();

    /// <summary>13 条（docs/16 §2 八条 + §3 五条），顺序 = 文档表序。</summary>
    public static readonly CatalogEntry[] All =
    {
        new("core.clock.tick", 2, DispatchMode.Serial,
            new[] { new PayloadField("dt", FieldKind.Int) }, "帧循环第 3 步（t1 §1.3）"),
        new("core.window.dpiChanged", 2, DispatchMode.Serial,
            new[] { new PayloadField("scale", FieldKind.Double), new PayloadField("rect", FieldKind.Int4) },
            "WM_DPICHANGED（LayeredFrameWindow.WndProc）"),
        new("core.window.moved", 2, DispatchMode.Parallel,
            new[] { new PayloadField("windowId", FieldKind.Str), new PayloadField("position", FieldKind.Int2) },
            "moveTo 指令落地（§6.2）+ 拖拽结束"),
        new("core.window.focus", 2, DispatchMode.Parallel,
            new[] { new PayloadField("focused", FieldKind.Bool) },
            "WM_ACTIVATEAPP 或 GetForegroundWindow 边沿检测"),
        new("host.suspend", 2, DispatchMode.Bail,
            new[] { new PayloadField("reason", FieldKind.Str) },
            "HostArbiter 处理 Suspend 指令时**先征询**（§6.5）"),
        new("host.resume", 2, DispatchMode.Parallel,
            new[] { new PayloadField("reason", FieldKind.Str) },
            "HostArbiter 处理 Resume 指令时发布（§6.6）"),
        new("pet.state.commit", 2, DispatchMode.Waterfall,
            new[] { new PayloadField("petId", FieldKind.Str) },
            "IPetState.commit（M4 只落分发器，§2.4）"),
        new("pet.state.migrated", 2, DispatchMode.Parallel,
            new[] { new PayloadField("from", FieldKind.Str), new PayloadField("to", FieldKind.Str) },
            "状态层迁移路径（M4 无生产触发源，§2.6）"),
        new("input.pointer.move", 3, DispatchMode.Serial,
            new[] { new PayloadField("pos", FieldKind.Int2), new PayloadField("velocity", FieldKind.Double, Optional: true) },
            "OnMouseMove + 帧泵 GetCursorPos（§2.5：首次移动 / dt≤0 时不写该字段）"),
        new("input.pointer.down", 3, DispatchMode.Emit,
            new[] { new PayloadField("button", FieldKind.Int), new PayloadField("windowPoint", FieldKind.Int2) },
            "OnMouseDown → PetInstance.PointerDown"),
        new("input.pointer.up", 3, DispatchMode.Emit,
            new[] { new PayloadField("button", FieldKind.Int), new PayloadField("windowPoint", FieldKind.Int2) },
            "OnMouseUp → PetInstance.PointerUp"),
        new("input.key.down", 3, DispatchMode.Emit,
            new[] { new PayloadField("vkey", FieldKind.Int), new PayloadField("modifiers", FieldKind.Int) },
            "仅当钩子已装（§7.3）；未授权时**无源**"),
        new("input.key.up", 3, DispatchMode.Emit,
            new[] { new PayloadField("vkey", FieldKind.Int), new PayloadField("modifiers", FieldKind.Int) },
            "仅当钩子已装（§7.3）；未授权时**无源**"),
    };

    private static readonly string[] AllNames = BuildNames();

    /// <summary>13 个名字（表内；不含待补表豁免）。</summary>
    public static IReadOnlyList<string> Names => AllNames;

    private static readonly string[] AllWithExemption = BuildWithExemption();

    /// <summary>表内 13 ∪ {host.canceled} —— 守门口径（t1 §2.2 判据 2 的审计面）。</summary>
    public static IReadOnlyList<string> NamesWithExemption => AllWithExemption;

    /// <summary>表内？</summary>
    public static bool IsKnown(string type) => IndexOf(type) >= 0;

    /// <summary>表内 ∪ 待补表豁免？（fail-closed 守门的接受集）</summary>
    public static bool IsAcceptable(string type) =>
        IsKnown(type) || string.Equals(type, PendingTableExemption, StringComparison.Ordinal);

    /// <summary>表内下标；表外（含豁免名）返回 -1。</summary>
    public static int IndexOf(string type)
    {
        for (int i = 0; i < All.Length; i++)
        {
            if (string.Equals(All[i].Name, type, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    public static CatalogEntry? Find(string type)
    {
        int index = IndexOf(type);
        return index < 0 ? null : All[index];
    }

    /// <summary>该名字的模式（豁免名按 parallel；表外其余 → null）。</summary>
    public static DispatchMode? ModeOf(string type) =>
        string.Equals(type, PendingTableExemption, StringComparison.Ordinal)
            ? DispatchMode.Parallel
            : Find(type)?.Mode;

    /// <summary>五种模式各自的代表事件名（覆盖对账的取样口）。</summary>
    public static readonly Dictionary<DispatchMode, string> Representative = new()
    {
        [DispatchMode.Emit] = "input.pointer.down",
        [DispatchMode.Parallel] = "core.window.moved",
        [DispatchMode.Serial] = "core.clock.tick",
        [DispatchMode.Bail] = "host.suspend",
        [DispatchMode.Waterfall] = "pet.state.commit",
    };

    /// <summary>规则 R5 的判定：夹具里的 type 必须是表内 13（负例夹具除外）。返回违规描述。</summary>
    public static IReadOnlyList<string> UnknownTypes(IEnumerable<string> types)
    {
        var problems = new List<string>();
        foreach (string type in types)
        {
            if (IsKnown(type))
            {
                continue;
            }

            problems.Add(string.Equals(type, PendingTableExemption, StringComparison.Ordinal)
                ? $"type={type} 只是待补表豁免（{PendingTableExemption}），不是 docs/16 §2/§3 的名字"
                : $"type={type} 在 docs/16 目录之外（t1 §2.1 的 13 个名字）");
        }

        return problems;
    }

    /// <summary>§2.3 的字段名全部 camelCase（判据面：名字里不许出现下划线或大写首字母）。</summary>
    public static IReadOnlyList<string> FieldNameViolations()
    {
        var problems = new List<string>();
        foreach (CatalogEntry entry in All)
        {
            foreach (PayloadField field in entry.Payload)
            {
                if (field.Name.Length == 0 || !char.IsLower(field.Name[0]) || field.Name.Contains('_'))
                {
                    problems.Add($"{entry.Name}.{field.Name} 不是 camelCase");
                }
            }

            if (entry.Payload.Count == 0)
            {
                problems.Add($"{entry.Name} 在 §2.1 里无 payload 列，本表空集是刻意的");
            }
        }

        return problems;
    }

    private static string[] BuildNames()
    {
        var names = new string[All.Length];
        for (int i = 0; i < All.Length; i++)
        {
            names[i] = All[i].Name;
        }

        return names;
    }

    private static string[] BuildWithExemption()
    {
        var names = new string[All.Length + 1];
        for (int i = 0; i < All.Length; i++)
        {
            names[i] = All[i].Name;
        }

        names[All.Length] = PendingTableExemption;
        return names;
    }
}
