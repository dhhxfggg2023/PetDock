// M2 契约 §1/§8：一只宠物实例的宿主侧载体（docs/11 §10 的六阶段 + §9 的降级链 + §7 的帧驱动）。
//
// 它在 core 里做四件事，全部只走 ABI 声明的那四个接口：
//   1. 状态机：§10 的 discovered → imported → instantiated → running ⇄ suspended → disposed
//      （转移合法性由 InstanceStateMachine 判定，乱序当场抛）；
//   2. 帧：调 §3 那个视图接口的 QueryFrame(now)，把上一帧的位图句柄交给 registry 释放（§8 所有权表）；
//   3. 行为：调 §4 的 Tick(dt, world)，把返回的指令数组交给 HostArbiter；
//   4. 错误：§9 的三条 —— QueryFrame 抛异常 → 本帧 dropped、连续 8 次 → 降级到占位图；
//      Tick 抛异常 → 本 tick 的指令全部作废 + 标记 degraded。
//
// 本类是 partial：状态机与诊断在这里，帧与仲裁在 PetInstance.Frames.cs，
// 下单与打点（§5）在 PetInstance.Pointer.cs —— 三块各自 <400 行（docs/40 D-03c）。
// 词汇约束（docs/40 D-03b / tools/lint-core.mjs）：需要指「§3 那个实现」时一律说 surface / view，
// 名字对照表在 docs/handoff/M2-name-map.md（core 之外）。
using System;
using System.Collections.Generic;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>
/// 一只宠物实例（docs/11 §10）。实例身份 = PetInstanceId（docs/11 §2）。
/// M2 由宿主按 manifest id + 素材引用派生（CAS 尚未落地，见任务 output 的未验证项）。
/// </summary>
public sealed partial class PetInstance : IDisposable
{
    private readonly List<StateTransition> _transitions = new();
    private BitmapHandle? _previousHandle;
    private int? _lastNowMs;
    private bool _disposed;

    internal PetInstance(
        string instanceId,
        IPetView view,
        IBrain? brain,
        IReadOnlyList<IPointerSurface> surfaces,
        IPetState? stateLayer,
        string pluginId,
        string sourceKind,
        IReadOnlyList<string>? assetRoots = null)
    {
        Id = instanceId;
        View = view;
        Brain = brain;
        Surfaces = surfaces;
        StateLayer = stateLayer;
        PluginId = pluginId;
        SourceKind = sourceKind;
        AssetRoots = assetRoots ?? Array.Empty<string>();
        Display = new DegradationChain();
        Arbiter = new HostArbiter(this);
        Bitmaps = new BitmapRegistry();
        SuspendPort = new NoopSuspendPort();
        Suspended += (instance, reason) => instance.SuspendPort.SuspendPlugin(instance.PluginId, reason);
    }

    /// <summary>实例 id（docs/11 §2 PetInstanceId）。</summary>
    public string Id { get; }

    /// <summary>提供本实例视图面的插件 id（宿主侧记账用；ABI 里没有这个概念）。</summary>
    public string PluginId { get; }

    /// <summary>视图面是怎么来的：声明式 dll 探测（"dll"）或宿主内注册的桩件（"stub"）。</summary>
    public string SourceKind { get; }

    /// <summary>docs/11 §3 的视图接口实现。</summary>
    public IPetView View { get; }

    /// <summary>docs/11 §4 的行为层（可为 null：宿主未装配 brain 插件）。
    /// 生产路径只在构造时注入一次，此后只读；测试要换装走 <see cref="InstallBrain"/>。</summary>
    public IBrain? Brain { get; private set; }

    /// <summary>
    /// 换装行为层（测试注入面，契约 §7.4 / §8.4 N4）。
    /// 为什么是一个具名方法而不是 public setter + public 构造器：
    ///   · 构造器保持 <c>internal</c> —— public 化等于给生产路径开一个「谁都能造一只不受 roster 约束的实例」的后门；
    ///   · 换装能力本身是**真实且必要**的：宿主在 M4 之后要支持热重载 brain（docs/13 §5），
    ///     而测试需要「一个 Tick 里抛异常的 brain」这类不可由插件目录构造的实现。
    /// 返回 false = 实例已进入不可换装的终态（Disposed），调用方据此判断而不是靠异常。
    /// 本方法不改变生产路径的任何行为：生产代码从不调用它。
    /// </summary>
    public bool InstallBrain(IBrain? brain)
    {
        if (State == InstanceState.Disposed)
        {
            return false;
        }

        Brain = brain;
        return true;
    }

    /// <summary>docs/11 §5 的交互层（0..n，可叠加）。</summary>
    public IReadOnlyList<IPointerSurface> Surfaces { get; }

    /// <summary>docs/11 §6 的状态层（可为 null）。</summary>
    public IPetState? StateLayer { get; }

    /// <summary>docs/11 §9 的降级链（只许向前）。</summary>
    public DegradationChain Display { get; }

    /// <summary>docs/11 §8 的位图 registry（句柄由宿主发放、在帧同步点后释放）。</summary>
    public BitmapRegistry Bitmaps { get; }

    /// <summary>docs/11:162 的指令仲裁器。</summary>
    public HostArbiter Arbiter { get; }

    /// <summary>docs/11 §10 的当前阶段。</summary>
    public InstanceState State { get; private set; } = InstanceState.Discovered;

    /// <summary>状态转移痕迹（自检与审计的可判定面）。</summary>
    public IReadOnlyList<StateTransition> Transitions => _transitions;

    /// <summary>docs/11 §3.1 的描述（attach 时取一次）。</summary>
    public PetDescription? Description { get; private set; }

    /// <summary>画板（attach 时由宿主给出）。</summary>
    public RenderTarget? Target { get; internal set; }

    /// <summary>帧计数 / 丢弃计数 / 连续丢弃计数。</summary>
    public long Frames { get; internal set; }

    public long FrameDrops { get; internal set; }

    public int ConsecutiveFrameDrops { get; internal set; }

    /// <summary>tick 计数与 brain 故障数（docs/11:259 的 degraded 标记）。</summary>
    public long Ticks { get; internal set; }

    public int BrainFaults { get; internal set; }

    /// <summary>brain 是否已进入 degraded（本 tick 的指令作废后置位，成功 tick 后清除）。</summary>
    public bool BrainDegraded { get; internal set; }

    /// <summary>挂起事件：宿主在此把「实例挂起」接到插件层（PluginHost.Disable），
    /// 使 docs/11 §7 的 8 连超时与 M1 的 8 连沙箱故障落到同一个 Suspended 终态（t1 契约 §8.2）。</summary>
    public event SuspendNotifyDelegate? Suspended;

    /// <summary>挂起转发的接缝（默认空实现；宿主接真实 PluginHost 时替换成 <see cref="HostSuspendPort"/>）。</summary>
    public IInstanceSuspendPort SuspendPort { get; set; }

    /// <summary>最近一次失败的原因文本（§9 的如实上报面）。</summary>
    public string LastFailure { get; internal set; } = "(none)";

    /// <summary>上一帧的位图对象（空闲判定：与本帧同一个对象 = 内容未变化，docs/11:225）。</summary>
    public System.Drawing.Bitmap? LastFrameBitmap { get; internal set; }

    /// <summary>
    /// emit 推翻标记（M4 契约 §5.2）：事件观察者在同调用栈里改了位姿/动画时由宿主置位，
    /// 下一帧 QueryFrame 前重放 SetAnimation 并清零 —— 已交付帧的像素绝不回改。
    /// </summary>
    public bool frameDirty { get; internal set; }

    /// <summary>重放落到视图的最后一次 setAnimation（frameDirty 时由帧源重放）。</summary>
    public (string Clip, bool Restart)? LastSetAnimation { get; internal set; }

    /// <summary>
    /// docs/11 §10 的 instantiated：分配画板、init()、describe()、attach() —— 顺序照 §10 表逐字。
    /// **偏离一处并在此写明**：画板尺寸要到帧缓冲就绪（窗口实测尺寸）才知道，所以本步发生在
    /// 首次取帧之前，而不是窗口创建之前（见 PetFrameSource 的注释）。
    /// InitAsync 用同步等待：M2 的视图实现都是进程内的 .NET 实现，Task 同步完成；
    /// 非同步实现需要宿主侧的超时泵，属 M5（已登记为未验证项）。
    /// </summary>
    public void AttachAndStart(int pixelWidth, int pixelHeight, double scale, long atMs = 0)
    {
        if (State == InstanceState.Discovered)
        {
            // 名册已经做过 Discover + ValidateManifest 这前两步（docs/11 §10 的 discovered → imported），
            // 所以到这里直接补记这两段痕迹；再往下才轮到 init/describe/attach。
            MarkImported("manifest-validated-by-roster", atMs);
        }

        if (State is not (InstanceState.Imported or InstanceState.Instantiated))
        {
            throw new InvalidOperationException($"attach 需要 Imported（当前 {State}）");
        }

        var budget = new ResourceBudget(
            FrameBudgetGuard.FrameBudgetMs, FrameBudgetGuard.IdleFrameBudgetMs, pixelWidth * pixelHeight);
        Target = new RenderTarget(pixelWidth, pixelHeight, scale, budget, Bitmaps);
        // 素材根由宿主显式给出（RFC-15 §3.2 第 3 跳）：Description?.Assets 在 init 之前必为空
        // （Description 要到 InitAsync 之后才被赋值），拿它当素材引用等于永远给不出素材。
        var ctx = new PetViewContext(
            Id,
            Description?.Assets ?? Array.Empty<string>(),
            budget,
            AssetRoots);
        View.InitAsync(ctx).GetAwaiter().GetResult();
        Description = View.Describe();
        View.Attach(Target);
        Bitmaps.ReleaseAll(); // init 之前的任何句柄都不该留在 registry 里
        InitBrain(budget);

        if (State == InstanceState.Imported)
        {
            Move(InstanceState.Instantiated, "init/describe/attach done", atMs);
        }
    }

    /// <summary>docs/11 §10：discovered → imported（manifest 已读且校验通过）。</summary>
    public void MarkImported(string reason, long atMs = 0) => Move(InstanceState.Imported, reason, atMs);

    /// <summary>
    /// 行为层的初始化（docs/11 §4 的 init(ctx)）。**M4 修复前这条链上是空的**：
    /// core 只调了视图面的 InitAsync，行为层从构造到销毁一次都没被初始化过，
    /// 于是插件自己写在 init 里的可见日志永远不出现（M4-VERIFY-r2.md 的 E 号缺口）。
    /// 失败按 docs/11 §9 处理：记 degraded、不抛 —— 一只坏 brain 不该让整只宠物起不来。
    /// </summary>
    private void InitBrain(ResourceBudget budget)
    {
        if (Brain is null)
        {
            return;
        }

        try
        {
            Brain.InitAsync(new BrainContext(Id, StateLayer, budget)).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            BrainFaults++;
            BrainDegraded = true;
            LastFailure = $"brain-init-failed {error.GetType().Name}: {error.Message}";
            KernelLog.Write($"brain-init-failed instance={Id} {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>docs/11 §10：instantiated → running（帧循环 + tick + 事件投递）。</summary>
    public void Start(long atMs = 0) => Move(InstanceState.Running, "frame loop started", atMs);

    /// <summary>
    /// docs/11 §4 / §9：一次 brain tick。抛异常 → 本 tick 的指令全部作废 + 标记 degraded。
    /// dt 由调用方给出（可累加、可被零化），零化规则见 <see cref="NextDeltaMs"/>。
    /// </summary>
    public void TickBrain(int dtMs, WorldSnapshot world)
    {
        Ticks++;
        if (Brain is null)
        {
            return;
        }

        IReadOnlyList<BrainDirective> directives;
        try
        {
            directives = Brain.Tick(dtMs, world);
        }
        catch (Exception error)
        {
            BrainFaults++;
            BrainDegraded = true;
            LastFailure = $"brain-tick-failed {error.GetType().Name}: {error.Message}";
            Display.Advance(DisplayTier.ReducedRate, "brain tick failed", 0);
            return; // 指令全部作废：一条都不落地（docs/11:259）
        }

        BrainDegraded = false;
        Arbiter.Apply(directives);
    }

    /// <summary>
    /// 帧间 dt（docs/11:134「须可被累加、可被零化（挂起时）」）。
    /// 挂起期间不推进 _lastNowMs，所以恢复后的第一帧 dt = 0；时间倒退也一律钳到 0。
    /// </summary>
    public int NextDeltaMs(int nowMs)
    {
        if (State != InstanceState.Running)
        {
            _lastNowMs = null;
            return 0;
        }

        int dt = _lastNowMs is int previous ? nowMs - previous : 0;
        _lastNowMs = nowMs;
        return dt < 0 ? 0 : dt;
    }

    /// <summary>docs/11 §10：running → suspended（停帧、停 tick、保留窗口）。幂等。</summary>
    public bool Suspend(string reason, long atMs = 0)
    {
        if (State == InstanceState.Suspended)
        {
            return true;
        }

        Move(InstanceState.Suspended, reason, atMs);
        _lastNowMs = null;
        KernelLog.Write($"instance-suspended instance={Id} reason={reason}");
        Suspended?.Invoke(this, reason);
        return true;
    }

    /// <summary>docs/11 §10：suspended → running（回到帧循环）。</summary>
    public bool Resume(string reason, long atMs = 0)
    {
        if (State == InstanceState.Running)
        {
            return true;
        }

        Move(InstanceState.Running, reason, atMs);
        _lastNowMs = null; // 恢复后的第一帧 dt = 0
        return true;
    }

    /// <summary>docs/11 §10：→ disposed（detach() → dispose()）。不可逆。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            View.Detach();
        }
        catch (Exception error)
        {
            KernelLog.Write($"view-detach-failed instance={Id} {error.GetType().Name}: {error.Message}");
        }

        // docs/11 §9：插件 dispose 不返回时宿主在 1000ms 后强杀其执行上下文。
        // .NET 侧没有可强杀的托管执行上下文（进程内不可终止线程），所以这里用有界等待：
        // 超时不抛、如实记日志 —— M2 声明的边界（已登记为未验证项）。
        try
        {
            if (!View.DisposeAsync().Wait(1000))
            {
                KernelLog.Write($"dispose-over-budget instance={Id} budget=1000ms");
            }
        }
        catch (AggregateException error)
        {
            KernelLog.Write($"view-dispose-failed instance={Id} {error.InnerException?.GetType().Name}");
        }

        Bitmaps.ReleaseAll();
        _previousHandle = null;
        CurrentFrame = null;
        if (State != InstanceState.Disposed)
        {
            Move(InstanceState.Disposed, "instance disposed", 0);
        }
    }

    /// <summary>一行自检输出（--drivetest 的判据面）。</summary>
    public string Describe()
    {
        return $"instance={Id} state={State} tier={Display.Tier} frames={Frames} drops={FrameDrops}"
            + $" ticks={Ticks} brain-faults={BrainFaults}"
            + $" surfaces={Surfaces.Count} state-layer={(StateLayer is null ? "none" : "bound")}"
            + $" live-bitmaps={Bitmaps.LiveCount} leases={Arbiter.GrantedCapabilities}"
            + $" last={LastFailure}";
    }

    /// <summary>docs/11 §10 的转移痕迹（自检打印用）。</summary>
    public string DescribeTransitions()
    {
        var parts = new List<string>();
        foreach (StateTransition step in _transitions)
        {
            parts.Add($"{step.From}->{step.To}");
        }

        return string.Join(" ", parts);
    }

    /// <summary>§9 降级链与视听面的一致性检查：Hidden 档必须能回答「不可命中」，
    /// Placeholder 档必须还能回答 hitmap 判定。返回空串 = 一致。</summary>
    public string CheckDisplayConsistency()
    {
        if (Display.Tier >= DisplayTier.Hidden)
        {
            return "hidden-tier-answers-no-hit";
        }

        if (Display.Tier >= DisplayTier.Placeholder && (CurrentFrame is null || Target is null))
        {
            return "placeholder-tier-without-frame";
        }

        return string.Empty;
    }

    private void Move(InstanceState to, string reason, long atMs)
    {
        InstanceStateMachine.Require(State, to, reason);
        StateTransition step = new(State, to, reason, atMs);
        State = to;
        _transitions.Add(step);
    }
}

/// <summary>挂起的观察者回调（自声明代替系统泛型委托，理由见 HostingTypes.cs 的先例）。</summary>
public delegate void SuspendNotifyDelegate(PetInstance instance, string reason);
