// 启动期的几个独立小工具（2026-10-31 从 Program.cs 拆出：D-03c 单文件 400 行上限）。
//
// 它们都不属于「装配内核」那件事：
//   WaitForPredecessor  重启时等上一个实例退出（见 UiHost.Restart.cs）
//   CollectManifestDirs 清单静态校验用的目录发现
//   Flag                自检输出里统一的 true/false
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;
using PetDock.Hosting;

namespace PetDock.Core;

internal static partial class Program
{
    /// <summary>
    /// 常驻形态的运行期日志落盘 + 「装不装已经下载好的新版本」这个决定（2026-10-31）。
    ///
    /// 为什么合成一处：日志必须在那个决定**之前**接上（它失败时唯一的现场就是日志），
    /// 而那个决定在交接成功时直接结束本进程 —— 两段拆开就要靠调用方保证顺序，
    /// 而顺序错的表现是「更新失败了，但日志里什么都没有」。
    ///
    /// 返回写好了日志落盘口的 ResidentLog（调用方负责在退出时收尾）。
    /// updateReport 交给面板显示一次（上次装没装上）。
    /// </summary>
    private static ResidentLog StartResidentLog(
        PetDataStore dataStore,
        ResidentPerfProbe.Options? probe,
        out string? updateReport,
        out bool handoff)
    {
        handoff = false;

        // 常驻形态的运行期日志落盘（RFC-17 §3.1）**在这里**就接上，而不是等到窗口装完：
        // 「装不装已经下载好的新版本」这个决定排在装载之前（见下），它失败时唯一的现场就在日志里。
        // 日志目录不存在就建一个空的（空目录也是成功）。
        var residentLog = new ResidentLog(
            Path.Combine(dataStore.Root, DiagnosticService.LogsFolder),
            RedactRules.ForCurrentMachine(dataStore.Root, PluginHost.ProbePluginsRoot()));
        residentLog.Start();
        KernelLog.Sink = residentLog;
        KernelLog.Write("resident-log-started");

        // 启动时先处理「下载好等着装」的新版本（2026-10-31 用户要求：
        // 「稍后更新就是退出程序下次启动时更新」）。必须排在**任何窗口之前**：
        // 交接成功意味着本进程马上要退出，窗口建了又拆只会让用户看见一次闪屏。
        // 探针形态一律不参与（门禁里没有人点更新，装了反而让判据跑在一个会变的程序上）。
        // 「上一次启动装没装上」在这里翻成人话：ConsumeResult 只给结构（谁成谁败 + 版本或原因码），
        // 文案属于界面层。
        UpdateResult? lastUpdate = UpdateStartup.ConsumeResult(dataStore.Root);
        updateReport = lastUpdate is { } last ? DescribeUpdate(last) : null;
        UpdateStartPlan updatePlan = UpdateStartup.Decide(dataStore.Root, probe is not null);
        if (updatePlan.Step == UpdateStartStep.Cancel)
        {
            // 上一趟交接没跑完（搬运工被结束，或用户退出后又自己点开了程序）。
            // 这一次不装 —— 正在运行的就是要被覆盖的那份文件 —— 并如实告诉用户。
            // 「意图读不出来」是同一个处置、另一种说法：那份文件写的哪个版本我们并不知道，
            // 拿待装版本去冒充它，等于替用户编了一句话。
            updateReport = I18n.Get(
                updatePlan.IntentUnreadable ? "ui.update.intentUnreadable" : "ui.update.cancelled",
                updatePlan.Pending?.Version ?? "?");
            KernelLog.Write($"update-start-cancelled detail={updatePlan.Detail}");
        }
        else if (updatePlan.Step == UpdateStartStep.RollBack)
        {
            // 上一趟装到一半被打断：盘上是新旧混杂的树，先把备份换回去再谈别的。
            // 回滚和「装」用同一个搬运工、同一条「等宿主退出」的协议（见 UpdateStartup.TryLaunchRollBack）。
            string bootRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (UpdateStartup.TryLaunchRollBack(dataStore.Root, bootRoot, out string? why))
            {
                KernelLog.Write($"update-start-rollback detail={updatePlan.Detail} leaving-before-windows");
                KernelLog.Sink = null;
                residentLog.Complete();
                residentLog.Dispose();
                handoff = true;
                return residentLog;
            }

            // 连回滚都交不出去：不删覆盖日志、不动备份，把话说清楚让用户拿 zip 重新覆盖一次。
            updateReport = I18n.Get("ui.update.rollbackFailed", why ?? "?");
            KernelLog.Write($"update-start-rollback-failed why={why}");
        }
        else if (updatePlan.Step == UpdateStartStep.HandOff)
        {
            string bootRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (UpdateStartup.TryHandOff(updatePlan, dataStore.Root, bootRoot))
            {
                // 交接完成：搬运工已经在等本进程退出。现在就把日志落尽再走 ——
                // 「谁在什么时候装了什么」是这条链上唯一能事后复核的东西。
                // 退出码由调用方给（HandoffDone 是这一趟的结论），这里只把日志收干净。
                KernelLog.Write(
                    $"update-start-handoff version={updatePlan.Pending!.Version} leaving-before-windows");
                KernelLog.Sink = null;
                residentLog.Complete();
                residentLog.Dispose();
                handoff = true;
                return residentLog;
            }

            updateReport = I18n.Get("ui.update.handoffFailed");
        }

        return residentLog;
    }

    /// <summary>
    /// 把搬运工留下的结局翻成人话。五种结局分开说（见 UpdateResult 的注释）：
    /// 装好且自己回来了 / 装好了但没回来 / 装失败了 / 回滚成功 / 回滚只成功了一部分。
    /// </summary>
    private static string DescribeUpdate(UpdateResult result)
    {
        if (result.Ok)
        {
            string version = string.IsNullOrEmpty(result.Text) ? "?" : result.Text;
            return I18n.Get(result.Restarted ? "ui.update.applied" : "ui.update.appliedNoRelaunch", version);
        }

        if (result.RollBack)
        {
            return I18n.Get(
                result.Complete ? "ui.update.rolledBack" : "ui.update.rollbackFailed",
                result.Text);
        }

        return I18n.Get("ui.update.failed", result.Text);
    }

    /// <summary>
    /// 重启路径：等上一个实例退出（最多 30 秒）。
    ///
    /// 为什么在这里等而不是让新进程自己去重试抢锁：单实例锁在 Main 最前面、在任何窗口之前，
    /// 抢不到就直接「已经在运行」然后退出 0 —— 用户点了「重启」，看到的是桌宠消失之后
    /// 又没回来。等待放在抢锁之前，是这条路上唯一还来得及的位置。
    ///
    /// 等不到也不拦着启动：往下走会让单实例那条如实说「已经在运行」，比无限等更像人话。
    /// </summary>
    private static void WaitForPredecessor(int pid)
    {
        try
        {
            using Process predecessor = Process.GetProcessById(pid);
            bool exited = predecessor.WaitForExit(30_000);
            KernelLog.Write($"restart-wait pid={pid} exited={Flag(exited)}");
        }
        catch (ArgumentException)
        {
            KernelLog.Write($"restart-wait pid={pid} exited=true reason=gone");
        }
        catch (InvalidOperationException)
        {
            KernelLog.Write($"restart-wait pid={pid} exited=true reason=gone");
        }
    }

    /// <summary>递归找带清单的目录（跳过构建产物与 node_modules —— 那不是发行内容）。</summary>
    private static void CollectManifestDirs(string dir, List<string> found)
    {
        if (File.Exists(Path.Combine(dir, ManifestValidator.ManifestFileName)))
        {
            found.Add(dir);
            return;
        }

        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        foreach (string sub in subdirs)
        {
            string name = Path.GetFileName(sub);
            if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            CollectManifestDirs(sub, found);
        }
    }


    /// <summary>统一把自检里的布尔值写成 true / false，别处一律复用。</summary>
    private static string Flag(bool value) => value ? "true" : "false";
}
