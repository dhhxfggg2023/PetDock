// 装配顺序探针（RFC-29 §3.6）：`--composetest [pluginsRoot]`。
//
// 为什么需要它：`FrameComposition.Compose` 与 `PluginHandle` 都是 internal，而 tests/m7 是
// 独立程序集、core 也没有 InternalsVisibleTo —— 判据无法直接调那个装配点。这里给它一个
// 与 `--hosttest` / `--perftest` 同族的命令行入口，跑的是**产品那条 Compose**（不是平行实现）：
// 建宿主 → 发现并装载 manifest → 调 Compose → 打 `COMPOSE …` 与 `COMPOSE-OK|FAIL`。
//
// 它不猜插件 id（docs/01 P-02 第 4 条）：期望的 source 由调用方看 `COMPOSE source=` 自己判。
using System;
using System.Collections.Generic;
using System.Linq;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>装配顺序探针：把「宿主里已装载的 tier-2 客体」这条路走一遍并如实报告。</summary>
internal static class ComposeProbe
{
    public static int Run(string[] args)
    {
        string root = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0]
            : PluginHost.ProbePluginsRoot() ?? string.Empty;

        IReadOnlyList<string> dirs;
        ComposeOutcome outcome;
        using (var host = new PluginHost(root, 60, autoLoad: true))
        {
            dirs = host.Discover(root);
            host.Start();

            // 装载要等宿主自己的调度器走完 —— 不能用 `Load(...).GetAwaiter().GetResult()`
            // 堵在主线程上（调度器与主线程同一条，会死等）。这是 `--hosttest` 已经验证过的形态
            // （PluginHostSelfTest.cs:51-54）：Start + 轮询，而不是同步等待每个 Load。
            WaitForRunning(host, 8000);
            outcome = FrameComposition.Compose("composetest", attachInstance: false, host);
        }

        Console.Out.WriteLine($"COMPOSE source={(outcome.SourceType.Length > 0 ? outcome.SourceType : "(none)")}"
            + $" installed={(outcome.Content is null ? "false" : "true")} dirs={dirs.Count}"
            + $" {outcome.Note}");

        if (outcome.Content is null)
        {
            Console.Out.WriteLine($"COMPOSE-FAIL reason={outcome.Note}");
            return 1;
        }

        Console.Out.WriteLine("COMPOSE-OK");
        return 0;
    }

    /// <summary>等宿主把插件装起来（最多 maxMs），装载失败也算「等到了」——探针只关心结果。</summary>
    private static void WaitForRunning(PluginHost host, int maxMs)
    {
        int waited = 0;
        while (waited < maxMs)
        {
            IReadOnlyList<PluginHandle> handles = host.HandlesSnapshot();
            if (handles.Any(h => h.State == PluginLifecycleState.Running))
            {
                return;
            }

            Thread.Sleep(50);
            waited += 50;
        }

        KernelLog.Write($"composetest-wait-timeout ms={maxMs}");
    }
}
