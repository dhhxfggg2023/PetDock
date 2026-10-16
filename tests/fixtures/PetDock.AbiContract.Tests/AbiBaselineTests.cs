// D-11a 正向 + 诚实性自证（t4）。
// 正向：整份基线对着 PetDock.Core.dll 反射核验，缺失/签名漂移 = 0。
// 负向自证（M1 t5 教训的直接要求 —— 空操作夹具曾被当 PASS）：把基线**在内存里**改坏
// 三种典型形态（删句柄、改枚举序号、类型整体改名），核验器三种都必须报出来。
// 安全网没被触发过的判据等于没有判据。
using System;
using System.Linq;
using Xunit;

namespace PetDock.AbiContract.Tests;

public sealed class AbiBaselineTests
{
    private static readonly AbiBaseline Baseline = AbiBaselineLoader.Load();

    [Fact]
    public void FixtureIsNotEmpty()
    {
        // 防空转：夹具本身必须真的带了料（每个角色至少一个句柄，共 ≥30 个）。
        Assert.True(Baseline.Roles.Length >= 6, $"roles={Baseline.Roles.Length}");
        Assert.True(Baseline.Roles.All(r => r.Handles.Length >= 1));
        int handles = Baseline.Roles.Sum(r => r.Handles.Length);
        Assert.True(handles >= 30, $"handles={handles}");
        Assert.Equal(5, Baseline.Enums.Length);
    }

    [Fact]
    public void WholeBaselineResolvesAgainstCoreDll()
    {
        System.Collections.Generic.List<string> problems = AbiVerifier.CheckAll(Baseline);
        Assert.True(
            problems.Count == 0,
            $"D-11b violation ({problems.Count}):\n" + string.Join("\n", problems));
    }

    [Fact]
    public void AbiVersionMarkerMatchesBaselineApi()
    {
        // D-11b 的版本号不变量（可判定部分）：宿主 ABI 标记与基线声明的主版本一致。
        // 次版本/修订号的「只加不减」由上面 WholeBaselineResolves 那条守：基线 append-only，
        // 任何删除/收窄都会让 CheckAll 非空 —— 与版本号数字无关，句柄本身是判据。
        Assert.Equal(PetDock.Core.PetRuntime.PetRuntimeAbi.Major, Baseline.ApiMajor);
        Assert.Equal(PetDock.Core.PetRuntime.PetRuntimeAbi.InterfaceName, Baseline.Interface);
    }

    [Fact]
    public void NegativeProof_RemovedHandleIsCaught()
    {
        // 违例形态 = 宿主把成员**删了**，而基线还在要求它。判据方向是「基线 ⊆ live」，
        // 所以变异必须让基线**多出**一条 live 里没有的句柄（模拟旧 ABI 的 QueryFrame 被宿主移除），
        // 而不是从基线里删 —— 从基线删条目是合法的（append-only 只约束不许少、不约束你不写）。
        BaselineRole mutated = Baseline.Roles[0] with
        {
            Handles = Baseline.Roles[0].Handles.Append("GhostMember(System.Int32):System.Void").ToArray(),
        };
        AbiBaseline broken = Baseline with { Roles = new[] { mutated } };
        System.Collections.Generic.List<string> problems = AbiVerifier.CheckAll(broken);
        Assert.Contains("GhostMember", string.Join("\n", problems));
    }

    [Fact]
    public void NegativeProof_RenamedHandleIsCaught()
    {
        // 签名变更（改名）= 旧插件调用点断裂，同样是 D-11b 违例。
        BaselineRole mutated = Baseline.Roles[0] with
        {
            Handles = Baseline.Roles[0].Handles
                .Select(h => h.Contains('(') ? h.Replace("QueryFrame", "QueryFrameX") : h)
                .ToArray()
        };
        AbiBaseline broken = Baseline with { Roles = new[] { mutated } };
        System.Collections.Generic.List<string> problems = AbiVerifier.CheckAll(broken);
        Assert.Contains("handle-missing", string.Join("\n", problems));
    }

    [Fact]
    public void NegativeProof_ChangedEnumOrdinalIsCaught()
    {
        BaselineEnum mutated = Baseline.Enums.First(e => e.Type.EndsWith("ClipLoop", StringComparison.Ordinal))
            with { Values = new[] { "Once=0", "Loop=2", "PingPong=1" } }; // 重排 = 数值含义变更
        AbiBaseline broken = Baseline with { Roles = Array.Empty<BaselineRole>(), Enums = new[] { mutated } };
        System.Collections.Generic.List<string> problems = AbiVerifier.CheckAll(broken);
        Assert.Contains("enum-ordinal-changed", string.Join("\n", problems));
    }

    [Fact]
    public void NegativeProof_MissingTypeIsCaught()
    {
        BaselineRole mutated = Baseline.Roles[0] with { Type = "PetDock.Core.PetRuntime.IGhost" };
        AbiBaseline broken = Baseline with { Roles = new[] { mutated } };
        System.Collections.Generic.List<string> problems = AbiVerifier.CheckAll(broken);
        Assert.Contains("type-missing", string.Join("\n", problems));
    }

    [Fact]
    public void NegativeProof_RemovedCapabilityHandleIsCaught()
    {
        // D-11b 的能力句柄半边：基线声明的句柄只要有一个被宿主白名单去掉，必须被抓住。
        // 变异 = 从真实基线里挑一项（persist.read:self）假装宿主已不认识它 —— 用一个不存在于
        // docs/14 §2 的 id 放进基线，CheckCapabilities 应报 capability-removed。
        AbiBaseline broken = Baseline with
        {
            Roles = Array.Empty<BaselineRole>(),
            Enums = Array.Empty<BaselineEnum>(),
            CapabilityFixed = new[] { "persist.read:self", "screen.suspendPet" },
            CapabilityPatternSamples = new[] { "network.fetch:trailing-dash-.example" },
        };
        System.Collections.Generic.List<string> problems = AbiVerifier.CheckAll(broken);
        string text = string.Join("\n", problems);
        Assert.DoesNotContain("capability-removed persist.read:self", text);
        Assert.Contains("capability-removed screen.suspendPet", text);
    }
}
