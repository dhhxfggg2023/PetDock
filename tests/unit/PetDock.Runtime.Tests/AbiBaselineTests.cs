// t9（docs/41 D-11a 的代码侧半边）：真的把 abi-baseline.json 读进来，逐条对着
// PetDock.Core.dll 反射解析 —— 夹具数据与真实类型在这里对上，不是 grep 注释。
//
// 这是 acceptance 3 之前缺的那半边：夹具文件躺在 tests/unit/.../fixtures/ 下，却没有任何
// 测试读它（夹具注释里写的 PetDock.Core.Tests/AbiBaselineTests.cs 在本工程不存在）。
// 红法：夹具里任何一个名字在 PetDock.Core.dll 里解析不到、或声明为接口的成员名消失，
// 这里立刻 FAIL —— 那正是 D-11b 的「移除或收窄」。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using PetDock.Core.PetRuntime;
using Xunit;

namespace PetDock.Runtime.Tests;

/// <summary>
/// 读 <c>fixtures/abi-baseline/min-old-plugin/abi-baseline.json</c> 并逐条解析到真实类型。
/// 夹具是 append-only 的：只许增加条目；删除或改名 = D-11b 的破坏性改动。
/// </summary>
public sealed class AbiBaselineTests
{
    private static string BaselinePath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(
                dir.FullName, "tests", "unit", "PetDock.Runtime.Tests", "fixtures",
                "abi-baseline", "min-old-plugin", "abi-baseline.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        // 兜底：bin 目录下的 Content 拷贝（csproj 里 fixtures\**\*.json 已 PreserveNewest）。
        string copied = Path.Combine(
            AppContext.BaseDirectory, "fixtures", "abi-baseline", "min-old-plugin", "abi-baseline.json");
        Assert.True(File.Exists(copied), $"找不到 abi-baseline.json（试过仓库根与 {copied}）");
        return copied;
    }

    private static JsonDocument LoadBaseline()
    {
        string text = File.ReadAllText(BaselinePath());

        // 夹具是 .json 且带 // 行注释（它同时是给人读的数据文件），JsonDocument 不认注释，
        // 所以先把注释行剔掉再解析 —— 被剔的只有行首是 // 的行，不影响任何数据条目。
        List<string> lines = new();
        foreach (string raw in text.Split('\n'))
        {
            if (raw.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add(raw.TrimEnd('\r'));
        }

        return JsonDocument.Parse(string.Join('\n', lines));
    }

    private static IEnumerable<string> StringsOf(JsonElement root, string property)
    {
        Assert.True(
            root.TryGetProperty(property, out JsonElement element),
            $"abi-baseline.json 缺属性 {property} —— 夹具被改坏了");
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        foreach (JsonElement item in element.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, item.ValueKind);
            yield return item.GetString()!;
        }
    }

    /// <summary>
    /// 核心装配（不是被测装配）：用反射解析限定名，供判据复用一个查找路径。
    /// 必须显式 Load PetDock.Core —— <c>Type.GetType(fqn)</c> 只在「调用程序集 + mscorlib」
    /// 范围内找，不会自动加载项目引用，会给出「类型不存在」的假象（已记账）。
    /// </summary>
    private static readonly Assembly CoreAssembly = typeof(PetRuntimeAbi).Assembly;

    private static Type Resolve(string fullyQualifiedName) =>
        CoreAssembly.GetType(fullyQualifiedName)
        ?? throw new InvalidOperationException(
            $"abi-baseline 的 {fullyQualifiedName} 在 PetDock.Core.dll 里解析不到（D-11b 破坏性改动）");

    [Fact]
    public void BaselineApiMatchesTheFrozenAbi()
    {
        using JsonDocument doc = LoadBaseline();
        JsonElement root = doc.RootElement;

        // 红：夹具的版本标记与 core 的 PetRuntimeAbi 不一致 —— 基线夹具就不再是「同一 ABI 的旧版」。
        Assert.Equal("petdock.runtime", root.GetProperty("interface").GetString());
        Assert.Equal(1, root.GetProperty("api").GetProperty("major").GetInt32());
        Assert.Equal(0, root.GetProperty("api").GetProperty("minor").GetInt32());
        Assert.Equal(PetRuntimeAbi.InterfaceName, root.GetProperty("interface").GetString());
        Assert.Equal(PetRuntimeAbi.Major, root.GetProperty("api").GetProperty("major").GetInt32());
        Assert.Equal(PetRuntimeAbi.Minor, root.GetProperty("api").GetProperty("minor").GetInt32());
    }

    [Fact]
    public void EveryBaselineTypeResolvesInCore()
    {
        using JsonDocument doc = LoadBaseline();
        List<string> missing = new();
        List<string> wrongAssembly = new();
        foreach (string name in StringsOf(doc.RootElement, "types"))
        {
            Type? type = Resolve(name);
            if (type is null)
            {
                missing.Add(name);
                continue;
            }

            // 红：ABI 类型被挪出 PetDock.Core（sdk 重声明就是 M1 t6 那个坑的入口）。
            if (!string.Equals(type.Assembly.GetName().Name, "PetDock.Core", StringComparison.Ordinal))
            {
                wrongAssembly.Add($"{name} -> {type.Assembly.GetName().Name}");
            }
        }

        Assert.True(missing.Count == 0, $"abi-baseline 类型解析不到：{string.Join(", ", missing)}");
        Assert.True(
            wrongAssembly.Count == 0,
            $"abi-baseline 类型不在 PetDock.Core 程序集里：{string.Join(", ", wrongAssembly)}");
    }

    [Fact]
    public void BaselineInterfacesDeclareEveryListedMember()
    {
        using JsonDocument doc = LoadBaseline();
        Assert.True(doc.RootElement.TryGetProperty("interfaces", out JsonElement list), "夹具缺 interfaces");
        var problems = new List<string>();
        int count = 0;
        foreach (JsonElement entry in list.EnumerateArray())
        {
            count++;
            string name = entry.GetProperty("name").GetString()!;
            Type type = Resolve(name);
            Assert.True(type.IsInterface, $"{name} 必须仍然是接口");
            foreach (JsonElement member in entry.GetProperty("members").EnumerateArray())
            {
                string memberName = member.GetString()!;
                bool found = type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                    .Any(m => m.Name == memberName);
                if (!found)
                {
                    // 红：基线依赖的成员被删/改名（D-11b「移除或收窄」）。
                    problems.Add($"{name}.{memberName}");
                }
            }
        }

        Assert.True(count > 0, "夹具里的 interfaces 列表为空 —— 夹具被改坏了");
        Assert.True(problems.Count == 0, $"abi-baseline 成员缺失：{string.Join(", ", problems)}");
    }

    [Fact]
    public void BaselineEnumsStillDeclareEveryValue()
    {
        using JsonDocument doc = LoadBaseline();
        Assert.True(doc.RootElement.TryGetProperty("enums", out JsonElement list), "夹具缺 enums");
        var problems = new List<string>();
        int count = 0;
        foreach (JsonElement entry in list.EnumerateArray())
        {
            count++;
            string name = entry.GetProperty("name").GetString()!;
            Type type = Resolve(name);
            Assert.True(type.IsEnum, $"{type.Name} 必须仍然是枚举");
            foreach (JsonElement value in entry.GetProperty("values").EnumerateArray())
            {
                // 红：枚举取值被删或被改名 —— 收窄同样算 D-11b。
                string valueName = value.GetString()!;
                if (!Enum.IsDefined(type, valueName))
                {
                    problems.Add($"{type.Name}.{valueName}");
                }
            }
        }

        Assert.True(count > 0, "夹具里的 enums 列表为空 —— 夹具被改坏了");
        Assert.True(problems.Count == 0, $"abi-baseline 枚举值缺失：{string.Join(", ", problems)}");
    }

    [Fact]
    public void BaselineDirectiveOrderMatchesTheEnumValues()
    {
        using JsonDocument doc = LoadBaseline();
        List<string> declared = StringsOf(doc.RootElement, "directiveOrderDescending").ToList();
        var actual = Enum.GetValues<DirectiveKind>()
            .OrderByDescending(k => (int)k)
            .Select(k => k.ToString())
            .ToList();

        // 红：docs/11:162 的仲裁顺序与 DirectiveKind 的数值不再一致 —— 基线夹具说的就是假的。
        Assert.Equal(declared, actual);
    }

    [Fact]
    public void BaselineCapabilitiesAreEmptyAndWhitelisted()
    {
        using JsonDocument doc = LoadBaseline();
        List<string> caps = StringsOf(doc.RootElement, "capabilities").ToList();

        // 红：基线插件开始声明能力（最小旧版插件必须零能力），或声明了白名单外的 id。
        Assert.True(caps.Count == 0, $"基线插件必须零能力，实际：{string.Join(", ", caps)}");
        foreach (string cap in caps)
        {
            Assert.True(PetDock.Core.Sandbox.CapabilityIds.IsWellKnown(cap), $"{cap} 不在 docs/14 §2 白名单");
        }
    }

    [Fact]
    public void BaselineManifestSitsNextToTheFixture()
    {
        string? dir = Path.GetDirectoryName(BaselinePath());

        // 红：manifest 与夹具被拆开 —— D-11a 的「最小旧版插件清单 + 最小旧版调用序列」就配不上了。
        Assert.True(dir is not null, "夹具目录解析不到");
        string manifest = Path.Combine(dir, "petdock-plugin.json");
        Assert.True(File.Exists(manifest), $"同级必须有 petdock-plugin.json（{manifest}）");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
        Assert.Equal("com.petdock.abibaseline", doc.RootElement.GetProperty("id").GetString());
    }
}
