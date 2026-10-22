// 实现 docs/12 §4「节流与功耗」：空闲 30fps 帧泵与单帧预算 SM-04 = 8.3ms 统计
using System.Diagnostics;

namespace PetDock.Core.Compositor;

/// <summary>
/// 固定周期帧泵：目标 30fps（约 33ms 周期），用 Stopwatch 累加睡眠补偿，
/// 睡眠不足 1ms 时用 Thread.Sleep(1) 收尾，绝不忙等。
/// 单帧预算 SM-04 = 8.3ms；超预算只计数，不阻塞、不打断后续帧。
/// </summary>
internal sealed class FramePump : IDisposable
{
    private const double FrameBudgetMs = 8.3;

    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();
    private Func<bool>? _tick;
    private Thread? _thread;
    private bool _stopping;
    private long _frames;
    private long _overBudgetFrames;
    private double _maxFrameMs;
    private double _totalFrameMs;
    private long _startTicks;

    /// <summary>
    /// 目标帧周期（毫秒）。默认 30fps（= 1000/30）。
    /// 可在运行期改（docs/18 §2.3 的「空闲降帧阈值」设置项）：改完立即生效，不用重启。
    /// 用 Volatile 读写：设置线程写、帧泵线程读。
    /// </summary>
    private double _targetFrameMs = 1000.0 / 30.0;

    /// <summary>当前目标帧周期（毫秒）。</summary>
    public double TargetFrameMs => Volatile.Read(ref _targetFrameMs);

    /// <summary>
    /// 改目标帧周期。越界会被拉回 [16.7, 200] 毫秒（60fps ~ 5fps）——
    /// 让用户把周期设成 0 毫秒等于让帧泵忙转，那是自伤。
    /// </summary>
    public void SetTargetFrameMs(double milliseconds)
    {
        double clamped = Math.Clamp(milliseconds, 1000.0 / 60.0, 1000.0 / 5.0);
        Volatile.Write(ref _targetFrameMs, clamped);
        KernelLog.Write($"frame-pump-target {clamped:0.0}ms");
    }

    /// <summary>已跑完的帧数。</summary>
    public int Frames => (int)Interlocked.Read(ref _frames);

    /// <summary>单帧最慢耗时（毫秒）。</summary>
    public double MaxFrameMs => Read(_maxFrameMs);

    /// <summary>单帧平均耗时（毫秒）。</summary>
    public double AvgFrameMs
    {
        get
        {
            long frames = Interlocked.Read(ref _frames);
            return frames == 0 ? 0 : Read(_totalFrameMs) / frames;
        }
    }

    /// <summary>帧泵已运行时长（毫秒）。</summary>
    public long ElapsedMs
    {
        get
        {
            long start = Interlocked.Read(ref _startTicks);
            if (start == 0)
            {
                return 0;
            }

            return _clock.ElapsedMilliseconds;
        }
    }

    /// <summary>超过 SM-04 预算的帧数（只计数，不阻塞）。</summary>
    public long OverBudgetFrames => Interlocked.Read(ref _overBudgetFrames);

    /// <summary>启动帧泵；tick 返回 false 时帧泵自行停止。</summary>
    public void Start(Func<bool> tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        lock (_gate)
        {
            if (_thread is not null)
            {
                throw new InvalidOperationException("帧泵已在运行");
            }

            _tick = tick;
            _stopping = false;
            _hasExited = false;
            _exitReason = null;
            _frames = 0;
            _overBudgetFrames = 0;
            _maxFrameMs = 0;
            _totalFrameMs = 0;
            _clock.Restart();
            Interlocked.Exchange(ref _startTicks, 1);
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "petdock-frame-pump",
            };
            _thread.Start();
        }
    }

    /// <summary>
    /// 泵线程是否已自行退出（RFC-21 t13）。
    /// `Loop()` 有三条自行 break 的路（tick 为 null / tick 返回 false / tick 抛异常），
    /// 而它**不会**清 `_thread`，也不会通知任何人 —— 于是外面看到的「泵在跑」可能早已不成立。
    /// 本标志是那个事实的唯一读数面；`Start` 置 false、`Loop` 尾部置 true，都在 `_gate` 内。
    /// </summary>
    public bool HasExited
    {
        get
        {
            lock (_gate)
            {
                return _hasExited;
            }
        }
    }

    /// <summary>台行退出原因（"tick-returned-false" / "tick-failed" / "stopped"；null = 没退出过）。</summary>
    public string? ExitReason
    {
        get
        {
            lock (_gate)
            {
                return _exitReason;
            }
        }
    }

    private bool _hasExited;
    private string? _exitReason;

    /// <summary>停止帧泵并等待线程退出。</summary>
    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_thread is null)
            {
                return;
            }

            _stopping = true;
            thread = _thread;
        }

        thread.Join(2000);

        lock (_gate)
        {
            _thread = null;
            _tick = null;
        }
    }

    private void Loop()
    {
        long next = _clock.ElapsedMilliseconds;
        while (true)
        {
            long frameStart = _clock.ElapsedMilliseconds;

            Func<bool>? tick;
            lock (_gate)
            {
                if (_stopping)
                {
                    break;
                }

                tick = _tick;
            }

            if (tick is null)
            {
                SetExitReason("no-tick");
                break;
            }

            bool keepGoing;
            try
            {
                keepGoing = tick();
            }
            catch (Exception error)
            {
                KernelLog.Write($"frame-tick-failed {error.Message}");
                SetExitReason("tick-failed");
                keepGoing = false;
            }

            long frameEnd = _clock.ElapsedMilliseconds;
            double frameMs = frameEnd - frameStart;

            Interlocked.Increment(ref _frames);
            lock (_gate)
            {
                if (frameMs > _maxFrameMs)
                {
                    _maxFrameMs = frameMs;
                }

                _totalFrameMs += frameMs;
                if (frameMs > FrameBudgetMs)
                {
                    Interlocked.Increment(ref _overBudgetFrames);
                }
            }

            if (!keepGoing)
            {
                // tick 回调自己说不干了（例如 Res..Loop 判成全屏档）：这是**正常的**自退，
                // 与异常自退区分开 —— 自愈方要据此决定是重启还是接受停帧。
                if (ExitReason is null)
                {
                    SetExitReason("tick-returned-false");
                }

                break;
            }

            // 睡眠补偿：把这一帧多花的时间从下一帧的睡眠里扣掉，长期不漂移
            // （周期在运行期可改，所以每轮都重新读一次 TargetFrameMs）。
            next += (long)Math.Round(TargetFrameMs);
            long now = _clock.ElapsedMilliseconds;
            long wait = next - now;
            while (wait > 0)
            {
                if (wait > 2)
                {
                    Thread.Sleep((int)wait - 1);
                }
                else
                {
                    Thread.Sleep(1);
                }

                lock (_gate)
                {
                    if (_stopping)
                    {
                        break;
                    }
                }

                wait = next - _clock.ElapsedMilliseconds;
            }
        }

        lock (_gate)
        {
            _stopping = false;
            // 泵线程走到这里就意味着「已经没有泵在出帧了」—— 无论哪条路 break 出来。
            // 这个标志是外面唯一能发现这件事的地方（RFC-21 t13）；`_thread` 故意不清：
            // `Stop()` 靠它判断是否 Join，改它会与 Stop 的既有语义打架。
            _hasExited = true;
            _exitReason ??= "stopped";
        }
    }

    /// <summary>记录退出原因（锁内写；先到先得，不覆盖已经记下的那条）。</summary>
    private void SetExitReason(string reason)
    {
        lock (_gate)
        {
            _exitReason ??= reason;
        }
    }

    private static double Read(double value)
    {
        return Interlocked.CompareExchange(ref value, 0, 0);
    }

    public void Dispose()
    {
        Stop();
        _clock.Stop();
    }
}
