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
    private const double TargetFrameMs = 1000.0 / 30.0;
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
                break;
            }

            // 睡眠补偿：把这一帧多花的时间从下一帧的睡眠里扣掉，长期不漂移
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
