// t1 契约 §8.5 的落地：t3 **只消费 core 的公开面**，需要新的可观测点就提给 t2，绝不改 core。
//
// 本文件是「消费公开面」的具体做法：**全程反射**，按简单类型名在每个判据运行时去找 M4 的面。
// 三个理由，都是硬的：
//   1. **编译期解耦**：t2 是 core 的唯一写入者，其签名在落地期间会变（本会话实测：EventBus 的
//      Emit 从 (evt) 变成返回 (ran, faults, afterFaultRan) 只在几分钟内）。测试若编译期引用它，
//      每一次 t2 的中间态都会让 tests/ 构建红 —— 那不是我该修的，却会让我交不出任何证据。
//   2. **§2.6 的诚实性**：面不存在时必须 SKIP + 具名原因。反射让「不存在」是一个**可判定的事实**，
//      而不是编译错误（编译错误会把整条判据连结果一起吞掉）。
//   3. **判据只认行为**：反射找到的类型仍是 core 的真类型，调用的仍是真实现 ——
//      这不是替身，只是晚绑定。找不到时才 SKIP。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.EventReplay.Tests;

/// <summary>M4 新面的存在性报（同一份报告供多个判据复用；字段名即判据打印的键名）。</summary>
public sealed record CoreSurfaceReport(
    bool HasEventCatalog,
    string EventCatalogType,
    bool HasEventBus,
    string EventBusType,
    bool HasCatalogGate,
    string CatalogGateMember,
    bool HasCapabilityBroker,
    string CapabilityBrokerType,
    bool HasWorldProbe,
    string WorldProbeType,
    bool HasFrameDirtyFlag,
    bool HasScriptedBrainInjection)
{
    public string Detail =>
        $"catalog={(HasEventCatalog ? EventCatalogType : "absent")}"
        + $" bus={(HasEventBus ? EventBusType : "absent")}"
        + $" gate={(HasCatalogGate ? CatalogGateMember : "absent")}"
        + $" broker={(HasCapabilityBroker ? CapabilityBrokerType : "absent")}"
        + $" probe={(HasWorldProbe ? WorldProbeType : "absent")}"
        + $" frame-dirty={HasFrameDirtyFlag}"
        + $" brain-injection={HasScriptedBrainInjection}";
}

/// <summary>core 程序集的反射探针与晚绑定适配器。</summary>
public static class CoreSurface
{
    private static readonly string[] CatalogNames = { "EventCatalog", "Catalog" };
    private static readonly string[] BusNames = { "EventBus", "HostEventBus" };
    private static readonly string[] BrokerNames = { "CapabilityBroker" };
    private static readonly string[] ProbeNames = { "WorldProbe", "WorldSampler" };

    private static readonly Lazy<CoreSurfaceReport> Report = new(Detect);

    public static CoreSurfaceReport Current => Report.Value;

    /// <summary>core 程序集（测试与生产同一份，含 M4 新面）。</summary>
    public static Assembly Assembly => typeof(PetInstance).Assembly;

    private static CoreSurfaceReport Detect()
    {
        Type? catalog = FindType(CatalogNames);
        Type? bus = FindType(BusNames);
        Type? broker = FindType(BrokerNames);
        Type? probe = FindType(ProbeNames);
        PropertyInfo? outside = typeof(HostEventDispatch)
            .GetProperty("OutsideCatalog", BindingFlags.Public | BindingFlags.Instance);
        bool brainInjection = typeof(PetInstance)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.Name.Equals("Brain", StringComparison.Ordinal) && p.CanWrite);

        return new CoreSurfaceReport(
            catalog is not null,
            catalog?.FullName ?? "(none)",
            bus is not null,
            bus?.FullName ?? "(none)",
            outside is not null,
            outside is null ? "(none)" : $"HostEventDispatch.{outside.Name}",
            broker is not null,
            broker?.FullName ?? "(none)",
            probe is not null,
            probe?.FullName ?? "(none)",
            typeof(PetInstance).GetProperty("frameDirty", BindingFlags.Public | BindingFlags.Instance) is not null,
            brainInjection);
    }

    /// <summary>按简单名找一个 public 类型（命名空间可能被 t2 调整，故不按全名）。</summary>
    public static Type? FindType(params string[] names)
    {
        Type[] types;
        try
        {
            types = Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            types = error.Types.Where(t => t is not null).Select(t => t!).ToArray();
        }

        foreach (Type type in types)
        {
            if (!type.IsPublic && !type.IsNestedPublic)
            {
                continue;
            }

            foreach (string name in names)
            {
                if (string.Equals(type.Name, name, StringComparison.Ordinal))
                {
                    return type;
                }
            }
        }

        return null;
    }

    /// <summary>SKIP 的行文统一口径：什么面、属于哪一节、谁欠它。</summary>
    public static string Reason(string surface, string contractSection, string owner) =>
        $"{surface} 在测试进程内不可达（t1 {contractSection}）：本判据已实现、无法执行；欠于 {owner}";
}

/// <summary>
/// 目录表（core 的 `EventCatalog`）的晚绑定视图。core 那份是**运行期唯一**名字表；
/// 本类只是读它 —— 测试侧不持有第二份可写的目录（镜像在 EventCatalogTable，且有对账判据）。
/// </summary>
public sealed class CatalogAdapter
{
    private readonly Type _type;

    private CatalogAdapter(Type type) => _type = type;

    public static CatalogAdapter? TryCreate()
    {
        Type? type = CoreSurface.FindType("EventCatalog", "Catalog");
        return type is null ? null : new CatalogAdapter(type);
    }

    public string TypeName => _type.FullName ?? _type.Name;

    /// <summary>core 的名字集合（按反射读任意 IEnumerable&lt;string&gt; 静态成员）。</summary>
    public IReadOnlyList<string> Names()
    {
        foreach (PropertyInfo property in _type.GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (property.GetValue(null) is IEnumerable<string> values)
            {
                return values.ToArray();
            }
        }

        foreach (FieldInfo field in _type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is IEnumerable<string> values)
            {
                return values.ToArray();
            }
        }

        return Array.Empty<string>();
    }

    /// <summary>core 的 `Contains(type)`。</summary>
    public bool Contains(string type)
    {
        MethodInfo? method = _type.GetMethod("Contains", BindingFlags.Public | BindingFlags.Static, null,
            new[] { typeof(string) }, null);
        return method is not null && method.Invoke(null, new object?[] { type }) is true;
    }

    /// <summary>core 的 `ModeOf(type, out mode)` 的字符串形态（枚举名）。</summary>
    public string? ModeOf(string type)
    {
        MethodInfo? method = _type.GetMethod("ModeOf", BindingFlags.Public | BindingFlags.Static);
        if (method is null)
        {
            return null;
        }

        object?[] args = { type, null };
        if (method.Invoke(null, args) is true && args[1] is not null)
        {
            return args[1]!.ToString();
        }

        return null;
    }

    /// <summary>core 的 `CountPayloadViolations(type, payload)`（§2.3 的字段口径判据）。
    /// 返回 null = 该重载不存在。</summary>
    public int? CountPayloadViolations(string type, JsonObject? payload)
    {
        MethodInfo? method = _type.GetMethod("CountPayloadViolations", BindingFlags.Public | BindingFlags.Static);
        if (method is null)
        {
            return null;
        }

        object?[] args = { type, payload };
        object? result = method.Invoke(null, args);
        return result is int count ? count : null;
    }
}

/// <summary>`HostEventDispatch` 的守门口（`OutsideCatalog` 计数）。</summary>
public static class CatalogGate
{
    /// <summary>读计数器；面不存在时抛（调用方先判 <see cref="CoreSurfaceReport.HasCatalogGate"/>）。</summary>
    public static int OutsideCatalog(HostEventDispatch dispatch)
    {
        PropertyInfo? property = typeof(HostEventDispatch)
            .GetProperty("OutsideCatalog", BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
        {
            throw new InvalidOperationException("HostEventDispatch.OutsideCatalog 不存在");
        }

        object? value = property.GetValue(dispatch);
        return value is int count ? count : -1;
    }
}

/// <summary>
/// `EventBus` 的晚绑定视图（§1 的五种 dispatch）。每次调用都反射，抓到的是 core 的真实现。
/// </summary>
public sealed class BusAdapter
{
    private readonly object _bus;
    private readonly Type _type;

    private BusAdapter(object bus, Type type)
    {
        _bus = bus;
        _type = type;
    }

    public string TypeName => _type.FullName ?? _type.Name;

    /// <summary>造一个总线（构造器取 `HostEventDispatch`）。</summary>
    public static BusAdapter? TryCreate(HostEventDispatch log)
    {
        Type? type = CoreSurface.FindType("EventBus", "HostEventBus");
        if (type is null)
        {
            return null;
        }

        ConstructorInfo? ctor = type.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 1
                && c.GetParameters()[0].ParameterType.IsInstanceOfType(log));
        if (ctor is null)
        {
            return null;
        }

        return new BusAdapter(ctor.Invoke(new object[] { log }), type);
    }

    /// <summary>订阅一个监听者。监听者签名由 core 的委托类型决定，这里用一个
    /// 「总是返回 <paramref name="result"/>」的适配器（<see cref="Listener"/>）实现。</summary>
    public bool Subscribe(string type, Func<object?> body)
    {
        MethodInfo? subscribe = _type.GetMethod("Subscribe", BindingFlags.Public | BindingFlags.Instance);
        if (subscribe is null)
        {
            return false;
        }

        ParameterInfo[] parameters = subscribe.GetParameters();
        if (parameters.Length != 2)
        {
            return false;
        }

        Delegate handler = Listener(parameters[1].ParameterType, body);
        subscribe.Invoke(_bus, new object?[] { type, handler });
        return true;
    }

    /// <summary>把 <paramref name="body"/> 适配成 core 的监听者委托类型。
    /// 用表达式树而不是 <c>Delegate.CreateDelegate(..., target, method)</c>：后者的方法签名必须
    /// 与委托的参数类型**逐个精确匹配**，而 core 的 `EventListener` 是
    /// `object? (PetEvent, DispatchRound?)` —— 一个第二参数类型会变的形态（本会话实测它在落地期
    /// 从单参变成双参），硬编码签名就会报「signature is not compatible with that of the delegate type」。
    /// 表达式树编译出的 lambda 忽略全部参数、只调 body，与参数类型无关。</summary>
    private static Delegate Listener(Type delegateType, Func<object?> body)
    {
        MethodInfo invoke = delegateType.GetMethod("Invoke")!;
        ParameterExpression[] parameters = invoke.GetParameters()
            .Select(p => Expression.Parameter(p.ParameterType, p.Name))
            .ToArray();
        Expression call = Expression.Invoke(Expression.Constant(body));
        return Expression.Lambda(delegateType, call, parameters).Compile();
    }

    public int ListenerCount(string type)
    {
        MethodInfo? method = _type.GetMethod("ListenerCount", BindingFlags.Public | BindingFlags.Instance);
        return method?.Invoke(_bus, new object?[] { type }) is int count ? count : -1;
    }

    /// <summary>调一个 dispatch 方法，返回它的元组形态（按**位置**取值）。
    ///
    /// 必须按位置读，不能按名字读：C# 的 `ValueTuple` 在 IL 里字段恒为 `Item1..ItemN`，
    /// `ran`/`faults`/`afterFaultRan` 这些只是**签名上的名字**（由 `TupleElementNamesAttribute` 携带），
    /// 反射 `GetFields` 拿不到它们。本会话实测：按名字取 → `The given key 'faults' was not present`。
    /// 这里用 TupleElementNamesAttribute（若在）把位置映射成契约名，拿不到就退回 Item{n}。</summary>
    public (bool Ok, Dictionary<string, object?> Fields) Dispatch(string mode, PetEvent evt)
    {
        MethodInfo? method = _type.GetMethod(mode, BindingFlags.Public | BindingFlags.Instance);
        if (method is null)
        {
            return (false, new Dictionary<string, object?>());
        }

        object? result = method.Invoke(_bus, new object?[] { evt });
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (result is null)
        {
            return (true, fields);
        }

        Type resultType = result.GetType();
        string[]? names = method.ReturnParameter
            .GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.Name == "TupleElementNamesAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string[];

        FieldInfo[] items = resultType.GetFields(BindingFlags.Public | BindingFlags.Instance);
        for (int i = 0; i < items.Length; i++)
        {
            object? value = items[i].GetValue(result);
            fields[items[i].Name] = value; // Item{n}：位置口径，恒可用
            if (names is not null && i < names.Length && names[i] is string name && name.Length > 0)
            {
                fields[name] = value;      // 契约名（如 ran / faults / afterFaultRan）
            }
        }

        return (true, fields);
    }

    /// <summary>按位置取第 <paramref name="index"/> 个元组元素（0 基）——与名字无关的读法。</summary>
    public static object? Item(Dictionary<string, object?> fields, int index) =>
        fields.TryGetValue($"Item{index + 1}", out object? value) ? value : null;

    public long DispatchFaults
    {
        get
        {
            PropertyInfo? property = _type.GetProperty("DispatchFaults", BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(_bus) is long value ? value : -1;
        }
    }
}

/// <summary>`CapabilityBroker` 的晚绑定视图（§4 的 200ms 与审计）。</summary>
public sealed class BrokerAdapter
{
    private readonly object _broker;
    private readonly Type _type;

    private BrokerAdapter(object broker, Type type)
    {
        _broker = broker;
        _type = type;
    }

    public string TypeName => _type.FullName ?? _type.Name;

    /// <summary>造一个 broker（构造器取已声明集合；反射挑一个可空 IEnumerable&lt;string&gt; 的重载）。</summary>
    public static BrokerAdapter? TryCreate(IEnumerable<string> declared)
    {
        Type? type = CoreSurface.FindType("CapabilityBroker");
        if (type is null)
        {
            return null;
        }

        ConstructorInfo? ctor = type.GetConstructors()
            .OrderBy(c => c.GetParameters().Length)
            .FirstOrDefault();
        if (ctor is null)
        {
            return null;
        }

        object?[] args = ctor.GetParameters()
            .Select(p => p.ParameterType == typeof(string) ? (object?)null : declared)
            .ToArray();
        return new BrokerAdapter(ctor.Invoke(args), type);
    }

    /// <summary>`Request(capability, justification, nowMs)` → 裁决的字符串形态。</summary>
    public (bool Ok, string Verdict, string Reason, long Latency, int Seq) Request(
        string capability, string? justification, long nowMs)
    {
        MethodInfo? method = _type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Request" && m.GetParameters().Length == 3);
        if (method is null)
        {
            return (false, "(none)", "(none)", -1, -1);
        }

        object? result = method.Invoke(_broker, new object?[] { capability, justification, nowMs });
        return ReadVerdict(result);
    }

    /// <summary>`Defer(capability, nowMs)`：留一条 pending，由后续 Request 触发超时结算。</summary>
    public bool Defer(string capability, long nowMs)
    {
        MethodInfo? method = _type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Defer");
        if (method is null)
        {
            return false;
        }

        method.Invoke(_broker, new object?[] { capability, nowMs });
        return true;
    }

    /// <summary>审计行（`capability-audit …` 的 ToString）。</summary>
    public IReadOnlyList<string> Audit()
    {
        PropertyInfo? property = _type.GetProperty("Audit", BindingFlags.Public | BindingFlags.Instance);
        if (property?.GetValue(_broker) is not IEnumerable items)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();
        foreach (object? item in items)
        {
            lines.Add(item?.ToString() ?? "(null)");
        }

        return lines;
    }

    private static (bool, string, string, long, int) ReadVerdict(object? verdict)
    {
        if (verdict is null)
        {
            return (false, "(none)", "(none)", -1, -1);
        }

        string kind = "(none)";
        string reason = "(none)";
        long latency = -1;
        int seq = -1;
        foreach (PropertyInfo property in verdict.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? value = property.GetValue(verdict);
            switch (property.Name)
            {
                case "Kind":
                    kind = value?.ToString() ?? "(none)";
                    break;
                case "Reason":
                    reason = value?.ToString() ?? "(none)";
                    break;
                case "LatencyMs":
                    latency = value is long l ? l : Convert.ToInt64(value ?? 0L);
                    break;
                case "Seq":
                    seq = value is int i ? i : Convert.ToInt32(value ?? -1);
                    break;
                default:
                    break;
            }
        }

        return (true, kind, reason, latency, seq);
    }
}

/// <summary>`WorldProbe` 的晚绑定视图（§7.1 的采样口径）。</summary>
public sealed class ProbeAdapter
{
    private readonly object _probe;
    private readonly Type _type;

    private ProbeAdapter(object probe, Type type)
    {
        _probe = probe;
        _type = type;
    }

    public string TypeName => _type.FullName ?? _type.Name;

    /// <summary>造一个探针；注入 tod 值（§7.1：`--eventtest` 用注入值，不读墙钟）。</summary>
    public static ProbeAdapter? TryCreate(int? injectedTod, out bool injected)
    {
        injected = false;
        Type? type = CoreSurface.FindType("WorldProbe", "WorldSampler");
        if (type is null)
        {
            return null;
        }

        ConstructorInfo? ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is null)
        {
            return null;
        }

        object?[] args = ctor.GetParameters()
            .Select(p => p.ParameterType == typeof(Func<int>) && injectedTod is int tod
                ? (object?)(() => tod)
                : null)
            .ToArray();
        injected = injectedTod is not null && ctor.GetParameters().Any(p => p.ParameterType == typeof(Func<int>));
        return new ProbeAdapter(ctor.Invoke(args), type);
    }

    /// <summary>`Sample(hwnd)` → `WorldSnapshot`。</summary>
    public bool Sample(IntPtr hwnd, out WorldSnapshot world)
    {
        world = default;
        MethodInfo? method = _type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Sample" && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(IntPtr));
        if (method is null)
        {
            return false;
        }

        object? result = method.Invoke(_probe, new object?[] { hwnd });
        if (result is WorldSnapshot snapshot)
        {
            world = snapshot;
            return true;
        }

        return false;
    }
}
