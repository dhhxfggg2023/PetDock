// 装配期的一致性读取不得等一个没起来的调度器（RFC-36 §4）。命令形态 `--uitest`，判据行 `CHECK ui-compose-*`。
//
// 为什么判这一条：`PluginHost.Snapshot` 的实现是「post 一条命令给调度线程，等它 ack」。
// 常驻装配在 `PluginHost.Start()` **之前**就查 tier-2 句柄（RFC-13 §2.4 把装载推迟到 UiHost
// 的第一个滴答，而 Compose 发生在更早的 ComposeWindow 里），于是那条命令投进了一个没人抽的
// 队列 —— 2026-10-24 实测：装好之后**第一次启动就崩**，stderr 是
// `System.TimeoutException: plugin host scheduler stalled (Snapshot)`，
// 调用链 Compose → Tier2Content → HandlesSnapshot，而这条路径本该退回内核纯色兜底。
//
// 判据只测**这一次读取**：空插件根 + 未 Start 的宿主，读句柄必须 0 条、必须在 SchedulerStallMs
// （10s）之前就返回。它不测「插件真的能装载」——那是 --hosttest 的事。
using System;
using System.Diagnostics;

namespace PetDock.Hosting;

internal static class HostComposeChecks
{
    /// <summary>判据出口（与 SettingsChecks/InstanceChecks 同形）。</summary>
    internal delegate void Report(string name, bool ok, string detail);

    public static void Run(Report check)
    {
        // 空根目录：不装载任何东西，只验「调度器没跑时的一次读取」。
        using PluginHost host = new(string.Empty);
        var watch = Stopwatch.StartNew();
        try
        {
            int handles = host.HandlesSnapshot().Count;
            watch.Stop();
            check(
                "ui-compose-read-before-start",
                handles == 0 && watch.ElapsedMilliseconds < 1000,
                $"handles={handles} elapsed={watch.ElapsedMilliseconds}ms");
        }
        catch (Exception error)
        {
            watch.Stop();
            check(
                "ui-compose-read-before-start",
                false,
                $"{error.GetType().Name}: {error.Message} elapsed={watch.ElapsedMilliseconds}ms");
        }
    }
}
