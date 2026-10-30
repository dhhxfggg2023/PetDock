// PetInstance 的自检输出面（2026-10-30 从 PetInstance.cs 拆出：那一半已经贴着 D-03c 的 400 行上限）。
// 只读，不改任何状态 —— 行数搬到这里是为了让主文件重新装得下真正的状态机。
using System.Collections.Generic;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

public sealed partial class PetInstance
{
    /// <summary>一行自检输出（--drivetest 的判据面）。</summary>
    public string Describe()
    {
        return $"instance={Id} state={State} tier={Display.Tier} frames={Frames} drops={FrameDrops}"
            + $" ticks={Ticks} brain-faults={BrainFaults}"
            + $" surfaces={Surfaces.Count} state-layer={(StateLayer is null ? "none" : "bound")}"
            + $" live-bitmaps={Bitmaps.LiveCount} leases={Arbiter.GrantedCapabilities}"
            + $" last={LastFailure}";
    }

    /// <summary>原规格 11 §10 的转移痕迹（自检打印用）。</summary>
    public string DescribeTransitions()
    {
        var parts = new List<string>();
        foreach (StateTransition step in _transitions)
        {
            parts.Add($"{step.From}->{step.To}");
        }

        return string.Join(" ", parts);
    }
}
