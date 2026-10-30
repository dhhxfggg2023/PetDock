// PetInstance 的行为半边（partial 的第四块，RFC-23 从 PetInstance.cs 挪出）：
// 行为层初始化与每 tick 调用。挪出来的理由是 D-03c 的单文件 400 行上限 —— PetInstance.cs
// 在加 RFC-23 的日志归属作用域时已经贴到上限，而这两段正是「调用进插件代码」的地方，
// 与日志归属同属一件事（作用域必须包住它们）。
using System;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>一只宠物实例的行为面。</summary>
public sealed partial class PetInstance
{
    /// <summary>
    /// 行为层的初始化（原规格 11 §4 的 init(ctx)）。**M4 修复前这条链上是空的**：
    /// core 只调了视图面的 InitAsync，行为层从构造到销毁一次都没被初始化过，
    /// 于是插件自己写在 init 里的可见日志永远不出现（M4-VERIFY-r2.md 的 E 号缺口）。
    /// 失败按 原规格 11 §9 处理：记 degraded、不抛 —— 一只坏 brain 不该让整只宠物起不来。
    /// RFC-23 §3.3 第 3 点：调用进插件之前开日志归属作用域。
    /// </summary>
    private void InitBrain(ResourceBudget budget)
    {
        if (Brain is null)
        {
            return;
        }

        try
        {
            using (PluginLog.Scope(BrainPluginId))
            {
                Brain.InitAsync(new BrainContext(Id, StateLayer, budget)).GetAwaiter().GetResult();
            }
        }
        catch (Exception error)
        {
            BrainFaults++;
            BrainDegraded = true;
            LastFailure = $"brain-init-failed {error.GetType().Name}: {error.Message}";
            KernelLog.Write($"brain-init-failed instance={Id} {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// 告诉行为层「宿主刚把你挪到别处了」（2026-10-30）。只发给实现了可选能力的行为层。
    /// 不抛、不记 degraded：这是一次通知，不是一次 tick —— 通知失败不该让宠物进入降级档，
    /// 但必须在日志里留下，否则「通知没送到」会表现成下一次的「它又自己走回去了」。
    /// </summary>
    internal void NotifyRelocated(PetPoint at)
    {
        if (Brain is not IRelocationAware aware)
        {
            return;
        }

        try
        {
            using (PluginLog.Scope(BrainPluginId))
            {
                aware.OnRelocated(at);
            }
        }
        catch (Exception error)
        {
            LastFailure = $"relocated-notify-failed {error.GetType().Name}: {error.Message}";
            KernelLog.Write($"relocated-notify-failed instance={Id} {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// 下线行为层（原规格 11 §9 / §4 的 dispose）。
    /// 以前这里没有这一步：brain 是名册里那个**共用**对象，实例释放时销毁它等于替别人关灯，
    /// 所以谁也不动它 —— 插件的 DisposeAsync 于是从未被调用过。
    /// 现在每实例一份（ownsBrain），释放责任才落得下来。
    /// 与 View 同一条纪律：有界等待 1000ms，超时不抛、如实记日志。
    /// </summary>
    private void DisposeBrain()
    {
        if (!_ownsBrain || Brain is null)
        {
            return;
        }

        try
        {
            if (!Brain.DisposeAsync().Wait(1000))
            {
                KernelLog.Write($"brain-dispose-over-budget instance={Id} budget=1000ms");
            }
        }
        catch (AggregateException error)
        {
            KernelLog.Write($"brain-dispose-failed instance={Id} {error.InnerException?.GetType().Name}");
        }
    }

    /// <summary>
    /// 原规格 11 §4 / §9：一次 brain tick。抛异常 → 本 tick 的指令全部作废 + 标记 degraded。
    /// dt 由调用方给出（可累加、可被零化），零化规则见 <see cref="NextDeltaMs"/>。
    /// RFC-23 §3.3 第 3 点：`Brain.Tick` 是插件代码，归属作用域包住它。
    /// </summary>
    public void TickBrain(int dtMs, WorldSnapshot world)
    {
        Ticks++;
        if (Brain is null || ExternalMotion)
        {
            return;
        }

        IReadOnlyList<BrainDirective> directives;
        try
        {
            using (PluginLog.Scope(BrainPluginId))
            {
                directives = Brain.Tick(dtMs, world);
            }
        }
        catch (Exception error)
        {
            BrainFaults++;
            BrainDegraded = true;
            LastFailure = $"brain-tick-failed {error.GetType().Name}: {error.Message}";
            Display.Advance(DisplayTier.ReducedRate, "brain tick failed", 0);
            return; // 指令全部作废：一条都不落地（原规格 11:259）
        }

        BrainDegraded = false;
        Arbiter.Apply(directives);
    }
}
