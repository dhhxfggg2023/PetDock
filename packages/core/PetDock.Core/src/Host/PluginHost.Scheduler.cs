// 插件宿主（原规格 01 P-01 内核六能力表第 2 项）：调度线程的**封送**半边（原规格 40 D-03c 的拆分）。
// 外部调用（Load/Disable/Reload/Snapshot）一律封送到调度线程执行，本文件只管「怎么送过去」；
// 三个半边（本文件 / PluginHost.cs 基础设施 / PluginHost.Lifecycle.cs）是同一个对象，
// 队列与线程句柄都在基础半边，这里只读不建。
using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace PetDock.Hosting;

public sealed partial class PluginHost
{
    /// <summary>入队一条调度线程命令（fire-and-forget；停机中静默丢弃）。</summary>
    private void PostCommand(HostCommand work)
    {
        try
        {
            _commands.Add(work);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding 之后：宿主正在停机
        }
    }

    /// <summary>同步封送：在调度线程上执行并等完成/等异常（Resume/自检用）。</summary>
    private void RunOnScheduler(HostCommand work)
    {
        if (Thread.CurrentThread == _thread)
        {
            work();
            return;
        }
        using var done = new ManualResetEventSlim(false);
        Exception? caught = null;
        PostCommand(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                // 改动落在 _handles 上，回执之前先把花名册发出去：等的人一醒就能读到新值
                // （PluginHost.Roster 的发布式读靠的就是这个顺序）。
                PublishRoster();
                done.Set();
            }
        });
        if (!done.Wait(SchedulerStallMs))
        {
            throw new TimeoutException("plugin host scheduler stalled");
        }
        if (caught is not null)
        {
            ExceptionDispatchInfo.Capture(caught).Throw();
        }
    }

    /// <summary>异步封送（接口方法用）：返回在调度线程上执行结果的 Task。</summary>
    private Task<T> RunOnSchedulerAsync<T>(Func<T> work)
    {
        if (Thread.CurrentThread == _thread)
        {
            return Task.FromResult(work());
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostCommand(() =>
        {
            try
            {
                T result = work();
                PublishRoster();
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>异步封送（无返回值版本；Task&lt;object?&gt; 协变到 Task）。</summary>
    private Task RunOnSchedulerVoid(HostCommand work)
    {
        if (Thread.CurrentThread == _thread)
        {
            work();
            return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostCommand(() =>
        {
            try
            {
                work();
                PublishRoster();
                tcs.SetResult(null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>一致性读取（自检用）：在调度线程上取值，避免对可变状态的撕裂读。
    /// **调度线程没在跑时直接读**：那时没有并发写者，而 post 进一个没人抽的队列只会干等
    /// SchedulerStallMs 再抛 TimeoutException（RFC-36 §1 的启动崩溃就是这么来的）。</summary>
    internal T Snapshot<T>(Func<T> read)
    {
        if (!_threadStarted || !_thread.IsAlive || Thread.CurrentThread == _thread)
        {
            return read();
        }
        using var done = new ManualResetEventSlim(false);
        T value = default!;
        PostCommand(() =>
        {
            try
            {
                value = read();
            }
            finally
            {
                PublishRoster();
                done.Set();
            }
        });
        if (!done.Wait(SchedulerStallMs))
        {
            throw new TimeoutException("plugin host scheduler stalled (Snapshot)");
        }
        return value;
    }
}
