// docs/41 D-11a 的判定引擎：把 abi-baseline.json 的每条「能力句柄」对着 **PetDock.Core.dll
// 的真实类型**反射核验。判据读代码，不读注释/表格（M1 t2 的 PetColor 教训：对照表声称存在、
// 代码里没有 —— 那次是注释在说谎，这一次如果还只核名字，幽灵照样能活）。
//
// 渲染规则（两侧同一函数，没有第二套口径）：
//   方法 = Name(参数类型全名,逗号分隔):返回类型全名
//   属性 = Name:类型全名
//   泛型 = Base<T1,T2>（System 类型全名，运行时可空注解不可见，不掺进去）。
// 不匹配时诊断打印**实际渲染值** —— 校准基线靠它，不靠猜。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace PetDock.AbiContract.Tests;

/// <summary>基线文件的内存形态（abi-baseline.json）。</summary>
internal sealed record BaselineRole(string Kind, string Type, string[] Handles);

internal sealed record BaselineEnum(string Type, string[] Values);

internal sealed record AbiBaseline(
    string Interface,
    int ApiMajor,
    int ApiMinor,
    string Assembly,
    BaselineRole[] Roles,
    BaselineEnum[] Enums,
    string[] CapabilityFixed,
    string[] CapabilityPatternSamples,
    string[] ViewCallSequence);

internal static class AbiBaselineLoader
{
    /// <summary>从输出目录读基线夹具（csproj 里 CopyToOutputDirectory）。</summary>
    public static AbiBaseline Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "abi-contract", "abi-baseline.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;
        var roles = new List<BaselineRole>();
        foreach (JsonElement r in root.GetProperty("roles").EnumerateArray())
        {
            roles.Add(new BaselineRole(
                r.GetProperty("kind").GetString()!,
                r.GetProperty("type").GetString()!,
                r.GetProperty("handles").EnumerateArray().Select(e => e.GetString()!).ToArray()));
        }

        var enums = new List<BaselineEnum>();
        foreach (JsonElement e in root.GetProperty("enums").EnumerateArray())
        {
            enums.Add(new BaselineEnum(
                e.GetProperty("type").GetString()!,
                e.GetProperty("values").EnumerateArray().Select(v => v.GetString()!).ToArray()));
        }

        JsonElement caps = root.GetProperty("capabilities");
        JsonElement seq = root.GetProperty("callSequence");
        return new AbiBaseline(
            root.GetProperty("interface").GetString()!,
            root.GetProperty("api").GetProperty("major").GetInt32(),
            root.GetProperty("api").GetProperty("minor").GetInt32(),
            root.GetProperty("assembly").GetString()!,
            roles.ToArray(),
            enums.ToArray(),
            caps.GetProperty("fixed").EnumerateArray().Select(v => v.GetString()!).ToArray(),
            caps.GetProperty("patternSamples").EnumerateArray().Select(v => v.GetString()!).ToArray(),
            seq.GetProperty("view").EnumerateArray().Select(v => v.GetString()!).ToArray());
    }
}

/// <summary>反射侧的句柄渲染与核验。</summary>
internal static class AbiVerifier
{
    /// <summary>被测程序集（默认 PetDock.Core；负向自证可传别处）。</summary>
    public static Assembly Core => typeof(PetDock.Core.PetRuntime.IPetView).Assembly;

    public static string Render(Type type)
    {
        if (!type.IsGenericType) return type.FullName ?? type.Name;
        string name = (type.FullName ?? type.Name);
        int tick = name.IndexOf('`');
        if (tick > 0) name = name.Substring(0, tick);
        return name + "<" + string.Join(",", type.GetGenericArguments().Select(Render)) + ">";
    }

    /// <summary>一个类型当前全部公开句柄（方法+属性），与基线同一渲染格式。</summary>
    public static HashSet<string> LiveHandles(Type type)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => !m.IsSpecialName))
        {
            string ps = string.Join(",", m.GetParameters().Select(p => Render(p.ParameterType)));
            set.Add($"{m.Name}({ps}):{Render(m.ReturnType)}");
        }

        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            set.Add($"{p.Name}:{Render(p.PropertyType)}");
        }

        return set;
    }

    /// <summary>
    /// 核验一条角色的基线句柄。返回缺失/签名变更清单（空 = 通过）。
    /// D-11b：基线句柄缺失 = 「移除或收窄」，无论版本号动不动都违例。
    /// </summary>
    public static List<string> CheckRole(string roleTypeFullName, string[] baselineHandles)
    {
        var problems = new List<string>();
        Type? role = Core.GetType(roleTypeFullName, throwOnError: false);
        if (role is null)
        {
            problems.Add($"type-missing {roleTypeFullName}");
            return problems;
        }

        HashSet<string> live = LiveHandles(role);
        foreach (string handle in baselineHandles)
        {
            if (!live.Contains(handle))
            {
                string member = handle.Split('(')[0].Split(':')[0];
                string actual = string.Join(" | ", live.Where(h => h.StartsWith(member, StringComparison.Ordinal)));
                problems.Add($"handle-missing {roleTypeFullName}::{handle} actual=[{actual}]");
            }
        }

        return problems;
    }

    /// <summary>
    /// 核验枚举：基线每一项写成 "Name=Ordinal"。值名必须仍定义（缺失=收窄），
    /// 序号必须与基线一致（重排枚举值改变已发布 ABI 的数值含义，同样是 D-11b 违例）。
    /// </summary>
    public static List<string> CheckEnum(string enumTypeFullName, string[] baselineValues)
    {
        var problems = new List<string>();
        Type? e = Core.GetType(enumTypeFullName, throwOnError: false);
        if (e is null || !e.IsEnum)
        {
            problems.Add($"enum-missing {enumTypeFullName}");
            return problems;
        }

        foreach (string entry in baselineValues)
        {
            string[] parts = entry.Split('=');
            string name = parts[0];
            if (!Enum.TryParse(e, name, out object? value))
            {
                problems.Add($"enum-value-removed {enumTypeFullName}.{name}");
                continue;
            }

            int actual = Convert.ToInt32(value);
            if (parts.Length == 2 && int.TryParse(parts[1], out int expected) && actual != expected)
            {
                problems.Add($"enum-ordinal-changed {enumTypeFullName}.{name} actual={actual} baseline={expected}");
            }
        }

        return problems;
    }

    /// <summary>
    /// 核验能力句柄集合（docs/14 §2 白名单 = D-11b 里「已发布的能力句柄」本体）。
    /// 固定项被移除、或模式项的正则被收窄（样例不再被接受），都是违例。
    /// </summary>
    public static List<string> CheckCapabilities(string[] fixedIds, string[] patternSamples)
    {
        var problems = new List<string>();
        foreach (string id in fixedIds)
        {
            if (!PetDock.Core.Sandbox.CapabilityIds.IsWellKnown(id))
            {
                problems.Add($"capability-removed {id}");
            }
        }

        foreach (string sample in patternSamples)
        {
            if (!PetDock.Core.Sandbox.CapabilityIds.IsWellKnown(sample))
            {
                problems.Add($"capability-pattern-narrowed {sample}");
            }
        }

        return problems;
    }

    /// <summary>
    /// 整份基线核验（正向判据 = 返回空清单）。负向自证喂它一份改坏的基线，必须拿到非空。
    /// </summary>
    public static List<string> CheckAll(AbiBaseline baseline)
    {
        var problems = new List<string>();
        foreach (BaselineRole role in baseline.Roles)
        {
            problems.AddRange(CheckRole(role.Type, role.Handles));
        }

        foreach (BaselineEnum e in baseline.Enums)
        {
            problems.AddRange(CheckEnum(e.Type, e.Values));
        }

        problems.AddRange(CheckCapabilities(baseline.CapabilityFixed, baseline.CapabilityPatternSamples));
        return problems;
    }
}
