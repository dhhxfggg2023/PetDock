// D-11a 的另一半：「最小旧版插件清单」能被**今天的宿主**装载（docs/41:71 逐字：
// 「每次宿主改动后，CI 用基线夹具做一次『旧插件加载』模拟，任一基线能力缺失或签名变更即失败」）。
// 两条路径都是宿主的真实公共面，不是测试专用桩：
//   1. manifest 校验：PluginHost.Discover + ValidateManifest（docs/21 语义）。
//   2. 名册装配：PetSurfaceRoster.Discover —— core 的装配面读同一份旧清单。
// **零写入**：插件根直接用复制到输出目录的夹具树本身。PluginHost.Discover 只扫一层子目录，
// 所以 `abi-contract/` 下发现的就是 `min-old-plugin/` 一个；深一层的 `negative/broken-manifest/`
// 不会被扫到，第四条用例先把这条隔离钉死，第三条再显式 ValidateManifest 它。
// 为什么必须零写入（实测教训）：这台机器的沙箱让**子进程**既写不了 %TEMP%、也可能写不了
// 自己的输出目录（code_verify 通道一律不可写）。判据一旦依赖临时暂存，就会被绑死在通道
// 权限上 —— 同一份字节在只读通道里跑不完，等于判据依赖环境而不是依赖代码。
// 诚实性：负向夹具必须被 ValidateManifest 拒绝且原因非空 —— 否则校验器坏掉时正向用例一起变绿。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PetDock.Hosting;
using Xunit;

namespace PetDock.AbiContract.Tests;

public sealed class OldPluginLoadTests
{
    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "abi-contract");

    [Fact]
    public void MinimalOldManifestStillValidates()
    {
        string pluginDir = Path.Combine(FixtureRoot, "min-old-plugin");
        Assert.True(File.Exists(Path.Combine(pluginDir, "petdock-plugin.json")),
            "夹具没被复制到输出目录（csproj Content 断链）");
        Assert.True(File.Exists(Path.Combine(pluginDir, "main.js")),
            "旧清单 entry 指向的 main.js 不在输出目录 —— 装载路径成空引用");

        using var host = new PluginHost(FixtureRoot, autoLoad: false);
        IReadOnlyList<string> dirs = host.Discover(FixtureRoot);
        Assert.Contains(dirs, d => d.EndsWith("min-old-plugin", StringComparison.Ordinal));
        var result = host.ValidateManifest(pluginDir);
        Assert.True(
            result.IsOk,
            "D-11a: 最小旧版清单在今天宿主不再合法: "
            + string.Join(" | ", (result.Error ?? Array.Empty<ManifestError>()).Select(e => $"{e.Field}:{e.Message}")));
        Assert.Equal(1, result.Ok!.ApiMajor);
        Assert.Equal(0, result.Ok.ApiMinor);
    }

    [Fact]
    public void RosterDiscoveryFindsOldManifestAsBrain()
    {
        // 名册读同一份旧清单：发现 1 个目录、校验 1 份合法清单 —— 「没被静默忽略」的证据是
        // 这两个计数器，不是 records（kind=brain 的 Tier 1 js 入口按 PetSurfaceLoader.cs:153-158
        // 是合法 skip：无托管代码单元，不进 ABI 名册；那是清单合法性之外的事）。
        PetSurfaceRoster roster = PetSurfaceRoster.Discover(FixtureRoot);
        Assert.Equal(1, roster.DiscoveredDirs);
        Assert.Equal(1, roster.ValidManifests);
        Assert.Equal(0, roster.InvalidManifests);
    }

    [Fact]
    public void NegativeFixtureIsRejectedNotEmpty()
    {
        string broken = Path.Combine(FixtureRoot, "negative", "broken-manifest");
        Assert.True(Directory.Exists(broken), "负向夹具没被复制到输出目录（csproj Content 断链）");
        using var host = new PluginHost(FixtureRoot, autoLoad: false);
        var result = host.ValidateManifest(broken);
        Assert.False(result.IsOk, "D-11a 诚实性：坏清单被今天的校验器接受了 —— 校验器判据失效");
        Assert.NotEmpty(result.Error!);
        Assert.Contains(
            result.Error!,
            e => e.Field is "id" or "api" or "kind" or "tier" or "entry" or "version" or "capabilities" or "license" or "$");
    }

    [Fact]
    public void NegativeFixtureStaysOutOfDiscovery()
    {
        // 一层扫描的 Discover 不该把深两层的坏清单捡进正向名册。若哪天 Discover 改成递归，
        // 这份坏清单会污染上一条用例的计数器 —— 这条先替我们叫。
        using var host = new PluginHost(FixtureRoot, autoLoad: false);
        Assert.DoesNotContain(
            host.Discover(FixtureRoot),
            d => d.Contains("broken-manifest", StringComparison.Ordinal));
    }
}
