// docs/11 §7「垃圾回收、帧预算与背压」的 C# 落地（SM-04）。
// 三个常量与一条超时规则，出处逐条写在这里（数值不许在别处再写一遍）：
//   FrameBudget      = 8.3 ms   每宠物上限（docs/11:224）
//   IdleFrameBudget  = 33.3 ms  无内容变化时（docs/11:225；= 1000/30，与 Compositor/FramePump.cs:13 同一值）
//   GlobalFrameBudget= 25 ms    所有宠物合计（docs/11:226）
//   连续 8 次超时 → suspend（docs/11:230）
//
// **它与 M1 已落地的「沙箱 8 连故障挂起」是同一条路径，不是两条**（t1 契约 §8.2 定案）：
//   两个计数源各自独立可测，但汇到同一个终态 —— 宠物实例被挂起，且宿主随后调用
//   PluginHost.Disable 让插件层也落到 PluginLifecycleState.Suspended：
//     计数源 A（本文件）：帧循环里 QueryFrame+Tick 的墙钟耗时 > 8.3ms，连续 8 帧 → FrameTimeoutSuspend。
//     计数源 B（M1）：Sandbox/PluginHost.Loop.cs:81-98 的 sandbox trap 计数，连续 8 片 → auto-suspend。
//   为什么必须共用终态：docs/11:220 逐字「宿主的健康线是唯一裁决者」，再造第二条 Suspended
//   判定点就会出现第二个魔数 8 与第二个终态（t1 契约 §8.2 依据 1）。
//   两条路径各自可测：A 见 --drivetest（超时用注入时钟确定性制造）；B 见
//   --hosttest 的预算探针（HostBudgetProbe.RunConsecutiveFaultDrive，M1 已有判据）。
//
// GlobalFrameBudget 在 M2 只有一个宠物实例，无「合计」可测，故只计数不阻断（t1 契约 §8.2 留给 t4）。
using System;
using System.Diagnostics;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>一次宿主帧驱动的结果。</summary>
public enum FrameOutcome
{
    /// <summary>QueryFrame + Tick 都在预算内返回（超预算但未到 8 连时不改判据，只看计数）。</summary>
    Completed,

    /// <summary>本帧 QueryFrame 抛异常 → 该帧 dropped（docs/11:258），帧内容落在降级链上。</summary>
    Dropped,

    /// <summary>本帧没有调用插件：实例不处于 Running（挂起或已释放）。</summary>
    NotRunning,

    /// <summary>本帧超预算，记一次 skip（docs/11:230「超时一次：宿主 skip 该帧并记录」）。</summary>
    Skipped,

    /// <summary>连续 8 次超预算 → 本帧触发挂起（docs/11:230）。</summary>
    SuspendedByTimeout,
}

/// <summary>
/// 帧预算执行器（SM-04）。线程模型：宿主帧循环线程独占；不与其他线程共享。
/// 时钟可注入：自检必须能用确定性时钟制造「连续 8 次超时」，否则这条判据只能靠真的把
/// 机器跑爆来验（t1 契约 §8 的「两者各自可测」要求）。
/// </summary>
public sealed class FrameBudgetGuard
{
    /// <summary>docs/11:224 每宠物每帧上限（毫秒），= SM-04。</summary>
    public const double FrameBudgetMs = 8.3;

    /// <summary>docs/11:225 无内容变化时的周期（毫秒）。</summary>
    public const double IdleFrameBudgetMs = 1000.0 / 30.0;

    /// <summary>docs/11:226 所有宠物合计上限（毫秒）。</summary>
    public const double GlobalFrameBudgetMs = 25.0;

    /// <summary>docs/11:230 连续超时次数上限，达到即挂起。</summary>
    public const int ConsecutiveTimeoutLimit = 8;

    private readonly Func<double> _clock;
    private double _lastMs;
    private double _maxMs;
    private double _totalMs;
    private double _lastGlobalMs;
    private double _lastFrameStart = -1;

    /// <summary>clock 返回单调递增的毫秒（默认 = Stopwatch 时间戳换算）。</summary>
    public FrameBudgetGuard(Func<double>? clock = null)
    {
        _clock = clock ?? DefaultClock;
    }

    /// <summary>默认时钟：Stopwatch 的高精度时间戳，单调、不受系统时间调整影响。</summary>
    public static double DefaultClock() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>连续超预算帧数（归零条件：某一帧回到预算内）。</summary>
    public int ConsecutiveTimeouts { get; private set; }

    /// <summary>时钟读取契约（自检的脚本化时钟按它排布）：**每一帧恰好读两次**
    /// —— 帧起一次、帧末一次；全局跨度用「本帧起点 − 上一次帧起点 + 本帧耗时」算，
    /// 也就是从上一帧走完到本帧走完的总时间（宿主自身的循环开销含在里面）。
    /// 这条契约写在注释里是因为判据要靠它做确定性时钟：读几次是行为的一部分，不是实现细节。</summary>
    public const int ClockReadsPerFrame = 2;

    /// <summary>累计 skip 帧数（超预算的帧各记一次，docs/11:230）。</summary>
    public int FrameSkips { get; private set; }

    /// <summary>累计因连续超时而挂起的次数。</summary>
    public int TimeoutSuspends { get; private set; }

    /// <summary>累计 dropped 帧数（QueryFrame 抛异常，docs/11:258）。</summary>
    public int DroppedFrames { get; private set; }

    /// <summary>累计全局超预算帧数（docs/11:226；M2 单实例，只计数不阻断）。</summary>
    public int GlobalOverBudgetFrames { get; private set; }

    /// <summary>上一帧耗时（毫秒）。</summary>
    public double LastMs => _lastMs;

    /// <summary>单帧最慢耗时（毫秒）。</summary>
    public double MaxMs => _maxMs;

    /// <summary>上一帧的全局跨度（毫秒）：本实例帧 + 宿主自身开销。</summary>
    public double LastGlobalMs => _lastGlobalMs;

    /// <summary>上一次驱动的帧是否被判为「无内容变化」（docs/11:225 的空闲档）。</summary>
    public bool LastFrameIdle { get; private set; }

    /// <summary>当前档位对应的预算（毫秒）：空闲 33.3，否则 8.3。</summary>
    public double BudgetFor(bool idle) => idle ? IdleFrameBudgetMs : FrameBudgetMs;

    /// <summary>平均每帧耗时（毫秒）。</summary>
    public double AverageMs(int frames) => frames <= 0 ? 0 : _totalMs / frames;

    /// <summary>
    /// 驱动一只宠物实例的一帧：
    /// 1. 非 Running → 不调用插件（挂起时 dt 可被零化，docs/11:134）；
    /// 2. QueryFrame(now) —— 抛异常即 dropped，本帧内容走降级链；
    /// 3. Brain.Tick(dt, world) —— 抛异常即本 tick 的 directives 全部作废 + degraded；
    /// 4. 交给 HostArbiter 按 docs/11:162 的优先级落地；
    /// 5. 计墙钟：超预算记一次 skip，连续 8 次 → 挂起。
    /// </summary>
    public FrameOutcome Drive(PetInstance instance, int nowMs, WorldSnapshot world)
    {
        ArgumentNullException.ThrowIfNull(instance);

        double frameStart = _clock();

        if (instance.State != InstanceState.Running)
        {
            LastFrameIdle = true;
            _lastMs = _clock() - frameStart;
            _lastGlobalMs = _lastMs;
            return FrameOutcome.NotRunning;
        }

        bool dropped = false;
        try
        {
            instance.QueryFrame(nowMs);
        }
        catch (Exception error)
        {
            // docs/11:258：当前帧标记 dropped；连续 8 次 → 降级到占位图。
            dropped = true;
            DroppedFrames++;
            instance.NoteFrameDropped(error);
        }

        // 空闲档判据（docs/11:225「无内容变化时」）：本帧与上一帧是同一个位图对象。
        LastFrameIdle = instance.LastFrameBitmap is not null && instance.LastFrameReused;

        // dt：以 now 与上一帧 now 之差累加；处于挂起态的帧 dt 被零化（docs/11:134）。
        instance.TickBrain(instance.NextDeltaMs(nowMs), world);
        instance.EnforceSessionBudget(nowMs);

        double elapsed = _clock() - frameStart;
        _lastMs = elapsed;
        // 全局跨度 = 上一次帧起点 → 本帧终点（含宿主循环自身的开销）。
        double frameEnd = frameStart + elapsed;
        _lastGlobalMs = _lastFrameStart >= 0 ? frameEnd - _lastFrameStart : elapsed;
        _lastFrameStart = frameStart;
        _totalMs += elapsed;
        if (elapsed > _maxMs)
        {
            _maxMs = elapsed;
        }

        if (_lastGlobalMs > GlobalFrameBudgetMs)
        {
            GlobalOverBudgetFrames++;
        }

        double budget = BudgetFor(LastFrameIdle);
        if (elapsed > budget)
        {
            FrameSkips++;
            ConsecutiveTimeouts++;
            if (ConsecutiveTimeouts >= ConsecutiveTimeoutLimit)
            {
                TimeoutSuspends++;
                instance.Suspend(
                    $"frame-budget {ConsecutiveTimeoutLimit} consecutive frames over"
                    + $" {PetPoint.Fmt(budget)}ms");
                return FrameOutcome.SuspendedByTimeout;
            }

            return FrameOutcome.Skipped;
        }

        ConsecutiveTimeouts = 0;
        return dropped ? FrameOutcome.Dropped : FrameOutcome.Completed;
    }

    /// <summary>一行自检输出：预算值与计数（判据的可判定面）。</summary>
    public string Describe()
    {
        return $"frame={PetPoint.Fmt(FrameBudgetMs)}ms idle={PetPoint.Fmt(IdleFrameBudgetMs)}ms"
            + $" global={PetPoint.Fmt(GlobalFrameBudgetMs)}ms limit={ConsecutiveTimeoutLimit}"
            + $" skips={FrameSkips} consecutive={ConsecutiveTimeouts}"
            + $" suspends={TimeoutSuspends} dropped={DroppedFrames}"
            + $" global-over={GlobalOverBudgetFrames}";
    }
}
