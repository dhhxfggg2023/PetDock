// PluginHost 的调度循环半边（partial 的第三块）：
// 单线程调度器：命令队列 → 逐插件 tick → 应用待重载。tick 与重载在同一根线程上
// 串行执行 —— 重载只能发生在切片之间，这就是「绝不在绘制中途替换」的结构保证。
using System;
using System.Linq;
using System.Threading;
using PetDock.Core.Compositor;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

public sealed partial class PluginHost
{
    private void Loop()
    {
        // 首轮之前先发一次：构造与线程启动之间可能已经有装载过的句柄，
        // 展示层在这一次发布之前读到的是空表（不是错，但没必要让它多等一个命令）。
        PublishRoster();

        while (!_stopping)
        {
            try
            {
                if (_commands.TryTake(out HostCommand? command, _tickIntervalMs))
                {
                    RunGuarded(command);
                    DrainCommands();

                    // 一批命令跑完：把花名册发出去，展示层的下一次读就是新的（零等待的发布式读，
                    // 见 PluginHost.Roster）。放在这里而不是每条命令里，是因为批内的中间态没人要看。
                    PublishRoster();
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            if (_stopping)
            {
                break;
            }
            if (!ManualTickOnly)
            {
                TickAll();
                ApplyPendingReloads();

                // tick 也会改状态（自动挂起 / 待重载落地）：切片之后同样要重发一次。
                PublishRoster();
            }
        }
        DrainCommands(); // 停机前把已入队命令跑完（尽力而为）
    }

    private void DrainCommands()
    {
        while (_commands.TryTake(out HostCommand? command))
        {
            RunGuarded(command);
        }
    }

    /// <summary>手动驱动一个切片（ManualTickOnly=true 时唯一的 tick 来源）：
    /// 同步封送到调度线程执行 TickAll —— 本调用返回即该切片已结束，计数可读。</summary>
    internal void TickNow() => RunOnScheduler(TickAll);

    private void RunGuarded(HostCommand command)
    {
        try
        {
            command();
        }
        catch (Exception ex)
        {
            // 单条命令失败绝不杀调度线程（原规格 13 §4：回调不得拖垮宿主）
            KernelLog.Write($"command-failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TickAll()
    {
        foreach (PluginHandle handle in _handles.ToArray())
        {
            if (handle.State != PluginLifecycleState.Running)
            {
                continue;
            }
            PluginSandbox? sandbox = handle.Sandbox;
            if (sandbox is null)
            {
                continue; // tier 0：声明式资源包，无执行体（原规格 13 §2）
            }
            int faultsBefore = handle.Sink.GuestFaultCount + handle.Sink.BudgetCount;
            sandbox.Tick(); // t3 契约 P-05：沙箱对外不抛
            handle.Ticks++;
            handle.Deliver(HostSliceEventName);
            int faultsAfter = handle.Sink.GuestFaultCount + handle.Sink.BudgetCount;
            handle.ConsecutiveFaults = faultsAfter > faultsBefore ? handle.ConsecutiveFaults + 1 : 0;
            if (handle.ConsecutiveFaults >= ConsecutiveFaultLimit)
            {
                KernelLog.Write($"auto-suspend plugin={handle.Manifest.Id} consecutive-faults={handle.ConsecutiveFaults}");
                try
                {
                    DisableCore(handle);
                }
                catch (Exception ex)
                {
                    KernelLog.Write($"auto-suspend-failed plugin={handle.Manifest.Id} {ex.Message}");
                }
            }
        }
    }

    private void ApplyPendingReloads()
    {
        foreach (PluginHandle handle in _handles.ToArray())
        {
            if (!handle.ReloadPending)
            {
                continue;
            }
            try
            {
                ReloadCore(handle, CancellationToken.None);
            }
            catch (Exception ex)
            {
                KernelLog.Write($"reload-failed plugin={handle.Manifest.Id} {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
