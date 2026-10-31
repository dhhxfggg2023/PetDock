using System;
using System.Threading;
using System.Threading.Tasks;

namespace PetDock.App;

/// <summary>
/// 一次长任务（导入素材、装插件、下更新）的三种结局。三态必须分开：
/// 取消是用户自己按的，界面不能把它画成错误条；并成一个 bool 就会让「我按了取消」显示成故障。
/// </summary>
public enum WorkOutcome
{
    /// <summary>做完了。</summary>
    Done = 0,

    /// <summary>没做成；Message 是给用户看的原因。</summary>
    Failed = 1,

    /// <summary>用户撤了；Message 也是给人看的话（例如「已取消导入」）。</summary>
    Cancelled = 2,
}

/// <summary>一次长任务的结局：三态 + 一句话。</summary>
public sealed record WorkResult(WorkOutcome Outcome, string Message)
{
    /// <summary>做完了（取消与失败都不算）。</summary>
    public bool Ok => Outcome == WorkOutcome.Done;

    public static WorkResult Success(string message = "") => new(WorkOutcome.Done, message);

    public static WorkResult Failure(string message) => new(WorkOutcome.Failed, message);

    public static WorkResult Cancel(string message) => new(WorkOutcome.Cancelled, message);
}

/// <summary>
/// 长任务此刻的可见状态。界面据此画进度条与取消按钮；Stage 已经翻译好，
/// 界面不再查表（与 AppResult.Message 同一口径）。
/// </summary>
public sealed record WorkState(bool Busy, string Stage, double Ratio)
{
    /// <summary>没有任务在跑。</summary>
    public static WorkState Idle { get; } = new(false, string.Empty, 0.0);
}

/// <summary>后台线程报上来的一次进度。Ratio 落在 [0,1]；Stage 为 null 表示沿用上一次的文案。</summary>
public readonly record struct WorkProgress(double Ratio, string? Stage);

/// <summary>
/// 长任务：同一时刻只跑一件（不排队、不并发写同一份数据区），跑的时候界面能看进度、能喊停。
///
/// 为什么要单独一个服务：这套形状（后台线程 + 节流进度 + 取消 + 三态结局）与「点一下就好」的
/// 同步命令完全不同；把它塞进 IPetsService/IPluginsService 会先长出一个四不像。
///
/// 线程口径：body 在后台线程跑，界面线程只 await；body 里要碰界面的步骤必须自己回 UI 线程。
/// </summary>
public interface IWorkService
{
    /// <summary>当前状态（没有任务时是 <see cref="WorkState.Idle"/>）。</summary>
    WorkState State { get; }

    /// <summary>状态变了（开始、进度、结束）。订阅方必须自己切回 UI 线程。</summary>
    event EventHandler? Changed;

    /// <summary>
    /// 跑一件长任务。stage 是已经翻译好的「正在做什么」。
    /// 已有任务在跑时不排队，直接返回失败（屯着的命令会一起改同一份数据）。
    /// </summary>
    Task<WorkResult> RunAsync(
        string stage,
        Func<CancellationToken, IProgress<WorkProgress>, Task<WorkResult>> body);

    /// <summary>请求取消：只是请求，任务在下一个安全点停下。</summary>
    void Cancel();
}
