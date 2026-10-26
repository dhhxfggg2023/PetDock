// RFC-21 §3.4 的执行者半边：两个入口、一个判定。
//
// 为什么与 ResidentFrameLoop.cs 分开（D-03c）：那个文件管「可见性 ↔ 泵」的生命周期（180 行），
// 这一块管「档位下落什么」—— 两个入口 + 判定调用点 + 提交记账。合成一个文件会破 400 行上限。
// 两块共用同名的 partial 类与同一个 _gate。
//
// **档位不在这里判定**：全仓唯一的判定是 FrameThrottlePolicy.Decide（RFC-21 §3.3）。
// 本块只调它、只消费结果 —— 这是「单一属主」的落地方式（判据 ui-throttle-single-decision-owner）。
//
// 锁纪律（RFC-21 §3.4 写死的两条，两处最容易出事）：
//   · **绝不持 _gate 调 Stop()**：FramePump.Stop() 内部 Join(2000)，而泵线程每轮要进 _gate ——
//     持锁停泵会先把泵线程堵在锁上、再等它两秒，那是自造的假死。
//   · **绝不持 _gate 调 RenderFrame()**：它要采样世界 + tick 行为 + UpdateLayeredWindow，
//     夹在锁里会让 UI 线程上的 Reevaluate 最长等一帧。
//   所以两个入口的形状都是「取锁 → 判档 → 改状态」+ 锁外「提交 / 停泵 / 起泵」。
using System;
using System.Drawing;
using System.Globalization;
using PetDock.Core.Compositor;

namespace PetDock.Core.Ui;

internal sealed partial class ResidentFrameLoop
{
    /// <summary>
    /// 静止档连续跳过多久后必须强制提交一次（RFC-21 §3.4）。
    /// 理由：分层窗的内容靠 UpdateLayeredWindow 挂在窗口上，正常情况下 DWM 不会丢它；但会话锁定 /
    /// 分辨率变化 / 显示驱动重启这类事件会让 surface 失效，而**失效没有任何回调**。
    /// 2 秒一次的兜底把「宠物凭空消失」这类无法自愈的故障压到 0.03% 的帧量。
    /// </summary>
    private const long MaxIdleSkipMs = 2000;

    /// <summary>
    /// 帧泵每一 tick 调它（RFC-21 §3.4，t11 修正）：合成 → 判档 → 应用周期 → 提交或跳过 → 返回是否继续。
    /// 返回 false 只表示「这个泵该散了」（全屏档 / 已释放）—— 泵线程据此自己走完 Loop 退出。
    /// </summary>
    /// <remarks>
    /// **顺序有意如此：先合成，再判档。** 这不是风格问题，是「动画能不能回来」的唯一保证：
    /// 精灵动画的帧号推进与 <c>LastFrameReused</c> 的唯一更新点都在
    /// <c>_content.GetFrameContent</c> → <c>PetInstance.QueryFrame</c> → <c>PetInstance.Swap</c> 这条链上。
    /// 旧实现把整条 <c>RenderFrame()</c> 跳掉（连合成都跳），于是 <c>LastFrameReused</c> 永远停在
    /// 进入静止档那一刻的 true 上 ⇒ 档位永远判 Idle ⇒ 永远不再合成 ⇒ **宠物永久冻结**。
    /// 先合成再判档，判档拿到的就是本 tick 的新鲜读数：内容一变，Decide 立刻回 Active 并提交。
    /// </remarks>
    public bool Advance()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }
        }

        // ① 锁外合成。**每一 tick 都要合成**，与档位无关 —— 它就是「内容有没有变」的取样点。
        ComposeOnce();

        // ② 本 tick 合成后内容是否变了（LastFrameReused 已在上面那一步被刷新）。
        //    true = 换了新位图对象 = 该把新画面推上屏。
        bool contentChanged = !_window.LastFrameReused;

        bool submit;
        bool paused;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            // tick、合成、静止判定是**同一个事实**的三个计数，必须在同一次锁内写完：
            // 旧写法把 _ticks/_composed 写在合成前的独立锁块里、_idleDecisions 写在判定处，
            // 读数就能停在两次写之间，拼出 `ticks=12 idleTicks=13` 这种 Advance 从未产出过的组合
            // （2026-10-26 实测 `ui-throttle-idle-decided-idle` 红）。Reading 与本块共用 _gate，
            // 所以三个计数要么全在窗内、要么全在窗外。
            _ticks++;
            _composed++;

            ThrottleDecision decision = DecideLocked();

            // 进入全屏档：**停泵，但不碰窗口**（所有者裁定「全屏档 = 只停泵，不藏窗」）。
            // 停法很关键：本方法跑在**帧泵自己的线程**上，这里若调 FramePump.Stop()（Join(2000)）
            // 就是自己 Join 自己。正确姿势是返回 false —— 泵线程看到 false 就自己走完 Loop 退出，
            // 不需要任何 Join。_pump / _pumping 在这里一并清掉，Reevaluate 才知道泵已经停了。
            if (decision.Tier == FrameTier.Fullscreen)
            {
                paused = _pumping;
                submit = false;
                RetirePumpLocked();
            }
            else
            {
                paused = false;
                _targetFrameMs = decision.TargetFrameMs;
                // 周期变化幂等；越界由 FramePump 钳到 [16.7, 200]ms。
                // 100ms（遮挡档）与 33.3ms（静止档）都在钳制区间内 —— 本片不需要放宽钳制。
                _pump?.SetTargetFrameMs(decision.TargetFrameMs);

                submit = decision.Submit;

                // 判成静止档的次数只在判定这一处记账：「这一窗每 tick 都判成静止」因此可判定。
                if (decision.Tier == FrameTier.Idle)
                {
                    _idleDecisions++;
                }

                // 静止档（Submit=false）的提交条件：内容变了，或者距上次提交超过 2 秒
                // （后者是 surface 失效的兜底 —— 会话锁定 / 分辨率变化 / 显示驱动重启都没有回调）。
                if (!submit && (contentChanged || _sinceLastSubmit.ElapsedMilliseconds >= MaxIdleSkipMs))
                {
                    submit = true;
                }

                if (!submit)
                {
                    _skipped++;
                }
            }
        }

        // ③ 锁外提交与日志：PresentFrame 是整窗上屏，夹在 _gate 里会让 UI 线程上的
        //    Reevaluate 最长等一次上屏。
        if (paused)
        {
            KernelLog.Write("throttle-pump-paused reason=fullscreen visible-kept=true");
            return false;
        }

        return submit ? PresentOnce() : true;
    }

    /// <summary>
    /// UI 滴答（250ms）调它（RFC-21 §3.4）：只判定与应用周期/泵状态，**绝不提交帧**
    /// （唯一的例外是离开全屏 / 泵自愈的那一帧 —— 屏幕上那一帧必须是新的）。
    /// 它是泵停着时**唯一**的驱动源：全屏档停泵、以及泵自己退出（RFC-21 t13），
    /// 都只能靠它重新起泵 —— 没有泵 tick 就没有人重新判定。
    /// </summary>
    public void Reevaluate()
    {
        if (_disposed)
        {
            return;
        }

        // ① 锁内只取引用与标记；**HasExited 一律锁外读**（那是 FramePump 自己的锁）。
        //    锁序论证：本类的 _gate 与 FramePump 的 _gate 是两把锁。若在 _gate 内读 HasExited，
        //    就形成 `ResidentFrameLoop._gate → FramePump._gate` 的嵌套；而 `FramePump.Stop()`
        //    走的是「取自己的 _gate → 放锁 → Join 泵线程」，泵线程的 tick 又要进本类的 _gate
        //    ⇒ 存在反向顺序的两个线程互相等待的可能。锁外读把这条边彻底断开。
        FramePump? current;
        bool wasPumping;
        lock (_gate)
        {
            wasPumping = _pumping;
            current = _pump;
        }

        bool pumpAlive = current is not null && !current.HasExited;

        // ② 泵活着 ⇒ 不重判：Advance 每 tick 都在判，而且它拿得到 alpha 与复用读数。
        //    这条也保证「两个入口不会都提交」—— 泵活着时 Reevaluate 完全不提交。
        if (wasPumping && pumpAlive)
        {
            return;
        }

        // ③ 泵自己退出了（t13）：`_pumping` 还停在 true，而泵线程已经走完 Loop 退出。
        //    注意**全屏档不会走到这里**：Advance 判全屏时已在锁内 RetirePumpLocked（清 _pump/_pumping），
        //    所以下一滴答 wasPumping=false、current=null ⇒ 不会被误报成「意外退出」。
        bool selfHealed = wasPumping && !pumpAlive;
        if (selfHealed)
        {
            // 诊断只写一次（不刷屏）：退出原因取自泵自己的自述，不猜。
            KernelLog.Write($"throttle-pump-exited reason={current?.ExitReason ?? "unknown"}"
                + " self-heal-pending=true");

            lock (_gate)
            {
                // 只有「还是同一个泵」时才退役：这段期间可能有别的路径换过泵。
                if (ReferenceEquals(_pump, current))
                {
                    RetirePumpLocked();
                }
            }
        }

        double resumeFrameMs;
        string reason;
        lock (_gate)
        {
            // ④ 再确认一次泵状态（不调 HasExited）：这期间可能已有别的路径起了新泵。
            if (_pumping && _pump is not null)
            {
                return;
            }

            // ⑤ 泵停着有三种可能：窗口被藏了 / 全屏档把泵停了 / 泵自己退出了。
            //    第一种**不许**起泵 —— 可见性归 Start()/Hide() 独占，这里碰它就是第二个可见性属主
            //    （docs/14 的「隐藏即停泵」会失效：用户按了「隐藏宠物」，宠物自己又出来了）。
            if (!_window.Visible)
            {
                return;
            }

            ThrottleDecision decision = DecideLocked();
            if (decision.Tier == FrameTier.Fullscreen)
            {
                return;
            }

            _targetFrameMs = decision.TargetFrameMs;
            resumeFrameMs = decision.TargetFrameMs;
            reason = selfHealed ? "self-heal" : "fullscreen-exit";
        }

        // ⑥ 先提交一帧（泵停着的这段时间屏幕上那一帧是旧的），再起一个**新**泵。
        //    反过来的话，新泵的头一帧还没落地，中间会有一次可见的空窗。
        //    用 RenderOnce（合成 + 提交）：旧画面必须连内容一起刷新。
        //    起泵一律 new FramePump()（在 StartPump 内）：复用已启动过的实例会抛
        //    InvalidOperationException("帧泵已在运行")（FramePump.Start）。
        RenderOnce();
        StartPump(reason);
        KernelLog.Write($"throttle-pump-resumed target={resumeFrameMs.ToString("0.0", CultureInfo.InvariantCulture)}ms"
            + $" reason={reason}");
    }

    /// <summary>档位判定的唯一调用点（锁内）。**只有这里**把读数折成档位。</summary>
    private ThrottleDecision DecideLocked()
    {
        (int, int, int, int) rect;
        uint dpi;
        try
        {
            (int X, int Y) pos = _window.CurrentPosition;
            Size size = _window.WindowSize;
            rect = (pos.X, pos.Y, size.Width, size.Height);
            dpi = _window.CurrentDpi;
        }
        catch (Exception error)
        {
            // 几何读数失败不是「该暂停」的理由：退回正常档，照常出帧（fail-open）。
            _geometryFailures++;
            KernelLog.Write($"throttle-geometry-failed {error.GetType().Name}: {error.Message}");
            return new ThrottleDecision(FrameTier.Active, _userFrameMs, Submit: true, "active");
        }

        var inputs = new ThrottleInputs(
            _window.WindowHandle,
            _alphaAt ??= SampleAlpha,
            _window.LastFrameReused,
            rect,
            dpi,
            _userFrameMs);

        ThrottleDecision decision = _policy.Decide(inputs, _lastSubmitRect, _lastSubmitDpi);
        if (decision.Tier != _tier)
        {
            FrameTier from = _tier;
            _tier = decision.Tier;
            _tierChanges++;
            // 只在变化时记一行（30fps × 8h 的日志会把落盘层打爆）。
            KernelLog.Write($"throttle-tier-changed {Token(from)}->{Token(decision.Tier)}"
                + $" reason={decision.Reason}"
                + $" target={decision.TargetFrameMs.ToString("0.0", CultureInfo.InvariantCulture)}ms");
        }

        return decision;
    }

    /// <summary>按窗口局部坐标问当前帧缓冲的 alpha（0 = 透明）。null = 此刻没有帧缓冲。</summary>
    private byte? SampleAlpha(int x, int y)
    {
        (byte B, byte G, byte R, byte A)? pixel = _window.SampleFramePixel(x, y);
        return pixel?.A;
    }

    /// <summary>
    /// <see cref="SampleAlpha"/> 的**唯一** delegate 实例。
    /// 为什么必须缓存：方法组转委托每次都会 new 一个闭包对象，而 `DecideLocked` 每 tick 都跑
    /// （30fps）—— 每帧一个纯垃圾，D-14d 的稳态判据会被它顶成 FAIL（实测过 300 秒浸泡 +92%）。
    /// 委托的 target 是同一个 `this`，语义与每次新建完全相同。
    /// </summary>
    private Func<int, int, byte?>? _alphaAt;

    /// <summary>
    /// 合成一帧（锁外）：向内容源要帧 + 写进 DIB，**不上屏**。返回 true 表示帧缓冲仍可用。
    /// 每 tick 都调 —— 它是 <c>LastFrameReused</c> 与动画帧号的唯一更新路径（RFC-21 t11）。
    /// 计数由调用方记账：tick 那一路与 Ticks 同一次写内前进，泵停着的那一路只由 Submitted 记账。
    /// </summary>
    private bool ComposeOnce()
    {
        return _window.ComposeFrame();
    }

    /// <summary>提交一帧并记账（锁外上屏）。返回 true 表示帧缓冲仍可用（泵可以继续）。</summary>
    private bool PresentOnce()
    {
        lock (_gate)
        {
            _submitted++;
            _sinceLastSubmit.Restart();
            // 记的是**这一帧提交时**的几何：静止档的「几何未变」比的就是它。
            _lastSubmitRect = (_window.CurrentPosition.X, _window.CurrentPosition.Y,
                _window.WindowSize.Width, _window.WindowSize.Height);
            _lastSubmitDpi = _window.CurrentDpi;
        }

        return _window.PresentFrame();
    }

    /// <summary>合成 + 提交（既有语义，供 Reevaluate 的「离开全屏首帧」用）。</summary>
    private bool RenderOnce()
    {
        bool composed = ComposeOnce();
        bool presented = PresentOnce();
        return composed && presented;
    }
}
