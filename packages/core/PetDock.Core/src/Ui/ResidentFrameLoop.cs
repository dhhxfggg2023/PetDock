// 常驻形态的「窗口可见性 ↔ 帧循环」绑定（RFC-14 §2.1）。
//
// 为什么要有这个类：RFC-14 的实证是「常驻进程里有一个 128x128 的分层窗，但它 visible=False，
// 而且常驻形态根本没有帧泵」。修这两件事要同时做 —— 只显示不启泵，屏幕上是一张静止的首帧；
// 只起泵不显示，帧在跑而用户看不见。把这两件事绑在一个类里，是为了**让它们不可能只做一半**：
// 显示窗口就一定有泵，隐藏窗口就一定没有泵。
//
// 为什么不住在 UiHost 里：UiHost 已经是 partial × 4，D-03c 的 400 行上限是硬门禁；
// 而更重要的是**属主清晰**：谁决定「现在该不该出帧」，答案只有这一个类。托盘的三条命令
// （显示/隐藏全部、隐藏宠物、移除宠物）都走它，不再直接摸窗口的 Show/Hide —— 直接摸就会绕过「隐藏即停泵」。
//
// 拆成两块（D-03c）：本文件管「可见性 ↔ 泵」的生命周期与读数；
// ResidentFrameLoop.Throttle.cs 管 RFC-21 的两个入口（Advance / Reevaluate）与档位执行。
// 两块共用同一个 _gate —— 锁纪律写在 Throttle 那一块的文件头。
//
// 帧泵为什么复用 FramePump：docs/12 §4 的帧预算按 8.3ms 计，UI 线程忙时帧率不可预期；
// FramePump 已有 30fps 目标 + 睡眠补偿 + 超预算计数（src/Compositor/FramePump.cs），
// 复用它而不是再造一个节流器。
using System;
using System.Diagnostics;
using System.Globalization;
using PetDock.Core.Compositor;

namespace PetDock.Core.Ui;

/// <summary>
/// 常驻帧循环：宠物窗口可见时跑帧，不可见时停帧；可见时按档位出帧（RFC-21）。
/// 幂等：<see cref="Start"/> 在已经跑着时什么都不做。
/// </summary>
internal sealed partial class ResidentFrameLoop : IDisposable
{
    private readonly LayeredFrameWindow _window;
    private readonly FrameThrottlePolicy _policy = new();
    private readonly Stopwatch _sinceLastSubmit = Stopwatch.StartNew();
    private readonly object _gate = new();

    private FramePump? _pump;
    private bool _pumping;
    private bool _disposed;

    /// <summary>已经停掉的泵留下的帧数（停泵后读数不许掉回 0 —— 判据要比「停之前」与「停之后」）。</summary>
    private long _retiredFrames;

    /// <summary>目标帧周期（毫秒）。泵还没起时先记着，起泵时套用（docs/18 §2.3 的空闲降帧阈值）。</summary>
    private double _targetFrameMs = 1000.0 / 30.0;

    /// <summary>用户设置的目标周期 —— 档位判定拿它当 Active 档的基准（档位不改这个值）。</summary>
    private double _userFrameMs = 1000.0 / 30.0;

    private FrameTier _tier = FrameTier.Active;
    private (int Left, int Top, int Width, int Height)? _lastSubmitRect;
    private uint? _lastSubmitDpi;
    private long _ticks;
    private long _composed;
    private long _submitted;
    private long _skipped;
    private long _tierChanges;
    private int _geometryFailures;

    public ResidentFrameLoop(LayeredFrameWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    /// <summary>
    /// 设置目标帧周期（毫秒）。立即生效：泵在跑就改泵，泵没起就记下来等它起。
    /// 越界由 FramePump.SetTargetFrameMs 拉回 [16.7, 200]。
    /// RFC-21 §8 第 7 条：这是**用户设置**，档位把它当 Active 档的基准，不改它本身。
    /// </summary>
    public void SetTargetFrameMs(double milliseconds)
    {
        lock (_gate)
        {
            _userFrameMs = milliseconds;
            _targetFrameMs = milliseconds;
            _pump?.SetTargetFrameMs(milliseconds);
        }
    }

    /// <summary>当前目标帧周期（毫秒）。</summary>
    public double TargetFrameMs
    {
        get
        {
            lock (_gate)
            {
                return _pump?.TargetFrameMs ?? _targetFrameMs;
            }
        }
    }

    /// <summary>宠物窗口当前是否可见。</summary>
    public bool Visible => _window.Visible;

    /// <summary>
    /// 帧泵是否在跑。**泵已自行退出时这里必须是 false**（RFC-21 t13）：
    /// 泵线程会因 tick 返回 false / tick 抛异常而自己走完 `Loop()` 退出，而它不会清 `_thread`
    /// 也不通知任何人 —— 只看 `_pumping` 会把一只早就停帧的宠物报成「在跑」。
    /// 锁序：`_pump` 的引用在 `_gate` 内取，`HasExited` 在**锁外**读（那是另一个对象的锁，
    /// 在 `_gate` 内调会形成 `_gate` → `FramePump._gate` 的嵌套，而 Stop 的路径方向相反）。
    /// </summary>
    public bool Pumping
    {
        get
        {
            FramePump? pump;
            bool pumping;
            lock (_gate)
            {
                pumping = _pumping;
                pump = _pump;
            }

            return pumping && pump?.HasExited != true;
        }
    }

    /// <summary>当前档位（判定结果，不是猜的）。可见度 internal（t12）：本类自身是 internal，
    /// 而 src/Ui 在本片之前零个 public 类型 —— 判据与诊断同属一个 assembly，internal 读得到。</summary>
    internal FrameTier Tier
    {
        get
        {
            lock (_gate)
            {
                return _tier;
            }
        }
    }

    /// <summary>泵 tick 次数（= Advance 的调用次数）。与 Composed / Submitted 是三个独立计数器：
    /// 泵每 tick 必然 Ticks++；每 tick 必然 Composed++（合成本身不被档位跳过）；只有真的上屏才 Submitted++。
    /// 静止档的可测面就是「Composed 跟着 Ticks 涨，Submitted 远小于它」（RFC-21 t11）。</summary>
    internal long Ticks => Count(_ticks);

    /// <summary>
    /// 合成次数：每 tick 都做（<c>ComposeFrame</c>：向内容源要帧 + 写进 DIB）。
    /// **这一项不被任何档位跳过** —— 它是 <c>LastFrameReused</c> 与动画帧号的更新点，
    /// 跳过它就等于把宠物冻住（RFC-21 t11 的修正）。
    /// </summary>
    internal long Composed => Count(_composed);

    /// <summary>真实提交次数：只有真的调了 <c>PresentFrame</c>（UpdateLayeredWindow）才 +1。</summary>
    internal long Submitted => Count(_submitted);

    /// <summary>Submit == false 而跳过的次数（静止档专属）。</summary>
    internal long Skipped => Count(_skipped);

    /// <summary>档位切换次数（只在变化时 +1）。</summary>
    internal long TierChanges => Count(_tierChanges);

    /// <summary>判定 / 探测 / 几何读数抛异常的次数（fail-open 的读数面）。</summary>
    internal int ProbeFailures
    {
        get
        {
            lock (_gate)
            {
                return _geometryFailures + _policy.ProbeFailures;
            }
        }
    }

    /// <summary>已出帧数（停泵后保留最后一次读数，便于判据比较「停之前」与「停之后」）。</summary>
    public int Frames => (int)(_retiredFrames + (_pump?.Frames ?? 0));

    /// <summary>单帧最慢耗时（毫秒）。诊断包读它（RFC-17 §3.2）。</summary>
    public double MaxFrameMs => _pump?.MaxFrameMs ?? 0;

    /// <summary>单帧平均耗时（毫秒，累计平均 —— 诊断包必须标明这一点）。</summary>
    public double AvgFrameMs => _pump?.AvgFrameMs ?? 0;

    /// <summary>超过 SM-04 预算的帧数。</summary>
    public long OverBudgetFrames => _pump?.OverBudgetFrames ?? 0;

    /// <summary>帧泵已运行时长（毫秒）。</summary>
    public long ElapsedMs => _pump?.ElapsedMs ?? 0;

    /// <summary>
    /// 显示窗口并起帧泵。幂等：窗口已可见且泵在跑时直接返回。
    /// 顺序有意如此：**先显示再起泵** —— 反过来的话，头几帧会被画进一个还看不见的窗口。
    /// </summary>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        if (!_window.Visible)
        {
            _window.Show();
            _window.BringToFrontOfTopmost();
            // Show 之后再present一次：分层窗的内容是靠 UpdateLayeredWindow 挂在窗口上的，
            // 窗体从隐藏到可见这一次状态切换可能让 OS 丢掉那一层，先重画一遍最省事。
            _window.PresentFirstFrame();
        }

        StartPump("start");
    }

    /// <summary>
    /// 隐藏窗口并**停泵**。隐藏的宠物不该继续烧 CPU —— 这是 docs/12 §4 的节流精神，
    /// 也是托盘「隐藏宠物」的真实含义（不是「只是看不见了，CPU 照烧」）。
    /// </summary>
    public void Hide()
    {
        _window.Hide();
        StopPump("hidden");
    }

    /// <summary>显示/隐藏切换（托盘的「显示/隐藏全部」）。</summary>
    public void Toggle()
    {
        if (_window.Visible)
        {
            Hide();
            return;
        }

        Start();
    }

    /// <summary>一行读数（判据与诊断读它）。</summary>
    public string Describe()
    {
        lock (_gate)
        {
            return "tier=" + Token(_tier)
                + " target=" + (_pump?.TargetFrameMs ?? _targetFrameMs).ToString("0.0", CultureInfo.InvariantCulture) + "ms"
                + " ticks=" + _ticks
                + " composed=" + _composed
                + " submitted=" + _submitted
                + " skipped=" + _skipped
                + " changes=" + _tierChanges
                + " probeFails=" + (_geometryFailures + _policy.ProbeFailures);
        }
    }

    private void StartPump(string reason)
    {
        double targetMs;
        lock (_gate)
        {
            if (_pumping || _disposed)
            {
                return;
            }

            targetMs = _targetFrameMs;
            var pump = new FramePump();
            pump.SetTargetFrameMs(targetMs);
            // tick 回调是 Advance —— 「每一 tick 都无条件合成并提交」从这里被档位接管。
            pump.Start(Advance);
            _pump = pump;
            _pumping = true;
        }

        KernelLog.Write($"resident-loop started visible={_window.Visible.ToString().ToLowerInvariant()}"
            + $" pumping=true reason={reason} target={targetMs.ToString("0.0", CultureInfo.InvariantCulture)}ms");
    }

    /// <summary>
    /// 停泵（**不碰窗口可见性**）。RFC-21 §3.4：全屏档走这条路 —— 窗口不动，只停帧。
    /// 锁纪律：先在锁内摘掉引用与标记，**锁外**才 Stop。
    /// </summary>
    private void StopPump(string reason)
    {
        FramePump? pump;
        lock (_gate)
        {
            if (!_pumping)
            {
                return;
            }

            pump = RetirePumpLocked();
        }

        pump?.Stop();
        KernelLog.Write($"resident-loop stopped visible={_window.Visible.ToString().ToLowerInvariant()}"
            + $" pumping=false reason={reason}");
    }

    /// <summary>
    /// 把当前泵「退役」：清标记、把已出帧数记进 _retiredFrames（停泵后读数不回零）。
    /// **不** Stop —— 调用方在锁外决定是 Join 还是让泵线程自己退出。锁内调用。
    /// </summary>
    private FramePump? RetirePumpLocked()
    {
        FramePump? pump = _pump;
        if (pump is not null)
        {
            _retiredFrames += pump.Frames;
        }

        _pumping = false;
        _pump = null;
        return pump;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPump("dispose");
    }

    private long Count(long value)
    {
        lock (_gate)
        {
            return value;
        }
    }

    private static string Token(FrameTier tier) => tier.ToString().ToLowerInvariant();
}
