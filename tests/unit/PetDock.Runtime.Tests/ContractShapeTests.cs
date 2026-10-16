// t9（docs/41 D-11a 的代码侧半边）：docs/11 冻结的四个 ABI 接口与核心值类型的契约形状断言。
// 类型名、公开成员签名、枚举成员与值 —— 全部对着真实类型检查（反射），不是对着注释。
// 任何破坏性改动（改名/改签名/删成员/收窄枚举）在此立即失败 = D-11b 的「移除或收窄」。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PetDock.Core.PetRuntime;
using Xunit;

namespace PetDock.Runtime.Tests;

/// <summary>契约形状（contract shape）测试：ABI 面 = docs/11 §2-§6 的 C# 落点。</summary>
public sealed class ContractShapeTests
{
    private static MethodInfo Method(Type type, string name, params Type[] parameters)
    {
        MethodInfo? m = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, parameters, null);
        Assert.True(m is not null, $"{type.Name} 缺方法 {name}({string.Join(", ", parameters.Select(p => p.Name))})");
        return m!;
    }

    private static PropertyInfo Property(Type type, string name)
    {
        PropertyInfo? p = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(p is not null, $"{type.Name} 缺属性 {name}");
        return p!;
    }

    private static void AssertPublicProps<T>(params string[] names)
    {
        Type type = typeof(T);
        var actual = new HashSet<string>(type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name));
        foreach (string name in names)
        {
            Assert.True(actual.Contains(name), $"{type.Name} 缺公开属性 {name}");
        }
    }

    private static void AssertEnum<TEnum>(params (int Value, string Name)[] entries)
        where TEnum : struct, Enum
    {
        Type type = typeof(TEnum);
        Assert.True(type.IsEnum, $"{type.Name} 不是枚举");
        foreach ((int value, string name) in entries)
        {
            Assert.True(Enum.IsDefined(type, name), $"{type.Name} 缺枚举值 {name}");
            Assert.Equal(value, Convert.ToInt32(Enum.Parse(type, name)));
        }
    }

    /// <summary>
    /// 公开成员（属性/方法/字段）存在性断言。必须是普通静态调用而不是
    /// <c>Assert.HasPublicMember(...)</c> 扩展写法：扩展方法查找要求接收者是实例表达式，
    /// 而 <c>Assert</c> 在这里是类型名（静态成员访问），编译器只走静态成员 → CS0117。
    /// </summary>
    private static void HasMember(Type type, string member)
    {
        bool found = type.GetMembers(BindingFlags.Public | BindingFlags.Instance).Any(m => m.Name == member);
        Assert.True(found, $"{type.Name} 缺公开成员 {member}");
    }

    [Fact]
    public void AbiVersionMarkerIsFrozen()
    {
        Assert.Equal("petdock.runtime", PetRuntimeAbi.InterfaceName);
        Assert.Equal(1, PetRuntimeAbi.Major);
        Assert.Equal(0, PetRuntimeAbi.Minor);
        Assert.Equal("1.0", PetRuntimeAbi.Version);
    }

    [Fact]
    public void IPetViewMembersMatchDocs11Section3()
    {
        Assert.True(typeof(IPetView).IsInterface);
        Assert.Equal(typeof(InterfaceMeta), Property(typeof(IPetView), "Meta").PropertyType);
        Assert.Equal(typeof(Task), Method(typeof(IPetView), "InitAsync", typeof(PetViewContext)).ReturnType);
        Assert.Equal(typeof(PetDescription), Method(typeof(IPetView), "Describe").ReturnType);
        Assert.Equal(typeof(void), Method(typeof(IPetView), "Attach", typeof(RenderTarget)).ReturnType);
        Assert.Equal(typeof(void), Method(typeof(IPetView), "Detach").ReturnType);
        Assert.Equal(typeof(Task), Method(typeof(IPetView), "DisposeAsync").ReturnType);
        Assert.Equal(typeof(bool), Method(typeof(IPetView), "SetAnimation", typeof(string), typeof(bool)).ReturnType);
        Assert.Equal(typeof(Frame), Method(typeof(IPetView), "QueryFrame", typeof(int)).ReturnType);
        Assert.Equal(typeof(void), Method(typeof(IPetView), "OnEvent", typeof(PetEvent)).ReturnType);
    }

    [Fact]
    public void IBrainMembersMatchDocs11Section4()
    {
        Assert.True(typeof(IBrain).IsInterface);
        Assert.Equal(typeof(InterfaceMeta), Property(typeof(IBrain), "Meta").PropertyType);
        Assert.Equal(typeof(Task), Method(typeof(IBrain), "InitAsync", typeof(BrainContext)).ReturnType);
        Assert.Equal(typeof(Task), Method(typeof(IBrain), "DisposeAsync").ReturnType);
        MethodInfo tick = Method(typeof(IBrain), "Tick", typeof(int), typeof(WorldSnapshot));
        Assert.Equal(typeof(IReadOnlyList<BrainDirective>), tick.ReturnType);
        Assert.Equal(typeof(void), Method(typeof(IBrain), "OnEvent", typeof(PetEvent)).ReturnType);
    }

    [Fact]
    public void IPointerSurfaceAndSessionMatchDocs11Section5()
    {
        Assert.Equal(typeof(string), Property(typeof(IPointerSurface), "Id").PropertyType);
        Assert.Equal(typeof(int), Property(typeof(IPointerSurface), "Priority").PropertyType);
        Assert.Equal(typeof(bool), Method(typeof(IPointerSurface), "HitTest", typeof(PetPoint)).ReturnType);
        Assert.Equal(
            typeof(IPointerSession),
            Method(typeof(IPointerSurface), "OnPointerDown", typeof(PetPoint), typeof(PointerButton)).ReturnType);

        Assert.Equal(typeof(void), Method(typeof(IPointerSession), "OnPointerMove", typeof(PetPoint)).ReturnType);
        Assert.Equal(typeof(void), Method(typeof(IPointerSession), "OnPointerUp", typeof(PetPoint)).ReturnType);
        Assert.Equal(typeof(void), Method(typeof(IPointerSession), "OnCancel").ReturnType);
        Assert.Equal(typeof(string), Property(typeof(IPointerSession), "Cursor").PropertyType);
    }

    [Fact]
    public void IPetStateMatchesDocs11Section6()
    {
        Assert.Equal(typeof(int), Property(typeof(IPetState), "SchemaVersion").PropertyType);
        Assert.Equal(
            typeof(IReadOnlyDictionary<string, JsonNode?>),
            Method(typeof(IPetState), "ReadAll").ReturnType);
        Assert.Equal(typeof(JsonNode), Method(typeof(IPetState), "Read", typeof(string)).ReturnType);
        Assert.Equal(
            typeof(void),
            Method(typeof(IPetState), "Write", typeof(IReadOnlyDictionary<string, JsonNode?>)).ReturnType);
        Assert.Equal(typeof(Task), Method(typeof(IPetState), "CommitAsync").ReturnType);
        Assert.Equal(typeof(void), Method(typeof(IPetState), "Rollback").ReturnType);
        Assert.Equal(
            typeof(IReadOnlyDictionary<string, JsonNode?>),
            Method(typeof(IPetState), "Migrate", typeof(int), typeof(int), typeof(IReadOnlyDictionary<string, JsonNode?>)).ReturnType);
    }

    [Fact]
    public void IClipCatalogIsOptionalCompatibleIncrement()
    {
        Assert.Equal(typeof(IReadOnlyList<AnimationClip>), Property(typeof(IClipCatalog), "Clips").PropertyType);
    }

    [Fact]
    public void CoreValueTypeShapesAreFrozen()
    {
        AssertPublicProps<Frame>("BitmapHandle", "Size", "Scale", "ClearRect", "Hitmap");
        HasMember(typeof(Frame), "HitsAt");
        HasMember(typeof(Frame), "AlphaAt");
        HasMember(typeof(Frame), "Describe");

        AssertPublicProps<AnimationClip>("Id", "Frames", "Fps", "Loop", "Anchor", "PeriodMs");
        AssertPublicProps<PetEvent>("Type", "At", "Payload", "Trace");
        AssertPublicProps<PetPoint>("X", "Y");
        AssertPublicProps<PetRect>("X", "Y", "W", "H");
        AssertPublicProps<PetSize>("W", "H");
        AssertPublicProps<PetColor>("R", "G", "B", "A");
        AssertPublicProps<InterfaceMeta>("Id", "Version");
        AssertPublicProps<WorldSnapshot>("Cursor", "WindowRect", "FocusedApp", "TimeOfDayMinutes", "IsOccluded");
        AssertPublicProps<BrainDirective>("Kind", "Clip", "Restart", "Options", "At", "Speed", "Event", "Capability", "Justification", "Reason");
        AssertPublicProps<BrainContext>("PetInstanceId", "State", "Budget");
        AssertPublicProps<PetViewContext>("PetInstanceId", "Assets", "Budget");
        AssertPublicProps<RenderTarget>("PixelWidth", "PixelHeight", "Scale", "Budget", "Bitmaps");
        AssertPublicProps<BitmapRegistry>("LiveCount", "Acquired", "Released");
        AssertPublicProps<BitmapHandle>("Id", "Bitmap", "PixelWidth", "PixelHeight", "PixelCount");
        AssertPublicProps<ResourceBudget>("TargetFrameMs", "IdleFrameMs", "MaxFramePixels");
        AssertPublicProps<StateLimitException>("Key", "Bytes", "Limit");
        AssertPublicProps<ClipAnchor>("Name", "At");
        AssertPublicProps<StateTransition>("From", "To", "Reason", "AtMs");
        AssertPublicProps<DegradationStep>("Tier", "Reason", "AtMs");
        AssertPublicProps<DegradationChain>("Tier", "Steps", "Refused");
        HasMember(typeof(DegradationChain), "Advance");
        HasMember(typeof(DegradationChain), "Describe");
        HasMember(typeof(DegradationChain), "IsForwardOnly");
        AssertPublicProps<PetDescription>("NaturalSize", "PreferredFps", "RequiresHitmap", "Capabilities", "Assets");

        // BrainDirective 的六种静态工厂 = docs/11 §4 的六种指令。
        foreach (string factory in new[] { "SetAnimation", "MoveTo", "Suspend", "Resume", "Emit", "RequestCapability" })
        {
            Assert.True(
                typeof(BrainDirective).GetMethod(factory, BindingFlags.Public | BindingFlags.Static) is not null,
                $"BrainDirective 缺静态工厂 {factory}");
        }

        // 状态机与降级链的静态面（宿主强制执行的判定器）。
        Assert.True(typeof(InstanceStateMachine).GetProperty("Order", BindingFlags.Public | BindingFlags.Static) is not null);
        Assert.True(typeof(InstanceStateMachine).GetMethod("CanTransition", BindingFlags.Public | BindingFlags.Static) is not null);
        Assert.True(typeof(InstanceStateMachine).GetMethod("Require", BindingFlags.Public | BindingFlags.Static) is not null);
        Assert.True(typeof(InstanceStateMachine).GetMethod("Describe", BindingFlags.Public | BindingFlags.Static) is not null);
        Assert.True(
            typeof(PetRuntimeAbi).GetField("InterfaceName", BindingFlags.Public | BindingFlags.Static) is not null);
    }

    [Fact]
    public void EnumMembersAndValuesAreFrozen()
    {
        AssertEnum<DirectiveKind>(
            (0, "RequestCapability"), (1, "Emit"), (2, "MoveTo"), (3, "SetAnimation"), (4, "Resume"), (5, "Suspend"));
        AssertEnum<ClipLoop>((0, "Once"), (1, "Loop"), (2, "PingPong"));
        AssertEnum<PointerButton>((0, "Left"), (1, "Right"), (2, "Middle"));
        AssertEnum<InstanceState>(
            (0, "Discovered"), (1, "Imported"), (2, "Instantiated"), (3, "Running"), (4, "Suspended"), (5, "Disposed"));
        AssertEnum<DisplayTier>((0, "Real"), (1, "ReducedRate"), (2, "Placeholder"), (3, "Hidden"));
    }
}

// 原先挂在 Assert 上的 HasPublicMember 扩展已删除：对「类型名」做扩展方法查找会落到静态
// 成员上 → CS0117（"Assert"未包含"HasPublicMember"的定义）。断言改为 ContractShapeTests.HasMember。