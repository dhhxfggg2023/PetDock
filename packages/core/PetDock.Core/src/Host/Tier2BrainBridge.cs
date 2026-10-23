// RFC-28 §3.4：把一个 tier-2 句柄的头脑面变成宿主仲裁器能吃的 IBrain。
//
// 桥接而非重写：指令回读成内核已有的 BrainDirective（RFC-27 §3.3），所以仲裁器
// （docs/11:162 的 suspend > setAnimation > moveTo > emit > requestCapability）不需要第二套实现。
//
// P-05（docs/01 §5）：Tick/OnEvent 不外抛 —— 失败返回空列表（本 tick 的指令作废，Brain.cs:182-184 的同一语义）。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PetDock.Core.PetRuntime;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

internal sealed class Tier2BrainBridge : IBrain
{
    private readonly Tier2Sandbox _sandbox;

    public Tier2BrainBridge(Tier2Sandbox sandbox)
    {
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
    }

    /// <summary>客体自报的身份（`abi.hello`）；没握手成功时给一个可辨识的占位（见 §3.4 的说明）。</summary>
    public InterfaceMeta Meta => new(
        _sandbox.Hello?.Id ?? "<tier2>",
        _sandbox.Hello?.Version ?? "0");

    /// <summary>累计成功的 tick 次数（诊断用）。</summary>
    public long TicksServed { get; private set; }

    /// <summary>作废的 tick 次数（客体没回、回了坏包、或进程已不在）。</summary>
    public long TicksDropped { get; private set; }

    /// <summary>生命周期归宿主（PluginHandle.Tier2），桥接层不拥有进程。</summary>
    public Task InitAsync(BrainContext ctx) => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    public IReadOnlyList<BrainDirective> Tick(int dtMs, WorldSnapshot world)
    {
        if (_sandbox.TryTickBrain(dtMs, world, out IReadOnlyList<BrainDirective>? directives, out _)
            && directives is not null)
        {
            TicksServed++;
            return directives;
        }

        TicksDropped++;
        return Array.Empty<BrainDirective>();
    }

    public void OnEvent(PetEvent evt) => _sandbox.SendEvent(evt);
}
