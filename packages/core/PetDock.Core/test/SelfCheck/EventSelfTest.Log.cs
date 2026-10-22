// --eventtest 的记账窗口半边（D-03c 拆分：EventSelfTest.cs 已 394 行、上限 400）。
//
// 为什么需要这条判据：常驻路径每 tick 发一条 `core.clock.tick`（`PetFrameSource.cs:216`），
// 8 小时 ≈ 86 万条。`HostEventDispatch.Log` 若没有上界，`--soak` 的私有工作集就会线性上涨
// （2026-10-22 在 HEAD 上实测 180 秒 +49.08%：12.80MB → 19.09MB），M0 的「8 小时常驻」退出
// 标准永远过不了。上界本身在 `HostArbiter.cs` 里，这里只钉住三件**契约**：
//   1. `Log` 永远不超过 `LogCapacity` 条（有界，不是「差不多」）；
//   2. `TotalPublished` 数的是**全部**落地事件（窗口满了也要接着数）；
//   3. `CountOf` 覆盖全部落地事件（否则窗口一满，判据会把「还在发」误读成「不再发了」）。
using System;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

internal static partial class EventSelfTest
{
    /// <summary>契约 §2.1 记账窗口：有界窗口 + 全量计数，两条都不能退化。</summary>
    private static int EventLogBounds()
    {
        int failures = 0;
        var log = new HostEventDispatch();
        int over = HostEventDispatch.LogCapacity + 50;
        for (int i = 0; i < over; i++)
        {
            // At = 序号：FIFO 断言靠它（PetEvent 没有独立序号字段，At 是唯一可由本判据控制的数）。
            log.Publish(new PetEvent(EventCatalog.ClockTick, i));
        }

        // 窗口：恰好保留最后 capacity 条，多一条都不留。
        Check(ref failures, "log-window-bounded",
            log.Log.Count == HostEventDispatch.LogCapacity,
            $"published={over} kept={log.Log.Count} capacity={HostEventDispatch.LogCapacity}");

        // 总数：窗口满了之后仍然精确（这条是「不能再退化回 Log.Count」的守卫）。
        Check(ref failures, "log-total-published",
            log.TotalPublished == over && log.TotalPublished != log.Log.Count,
            $"published={log.TotalPublished} kept={log.Log.Count}");

        // 分类计数：覆盖窗口之外的条目。
        Check(ref failures, "log-count-of-total",
            log.CountOf(EventCatalog.ClockTick) == over,
            $"countOf={log.CountOf(EventCatalog.ClockTick)} expected={over}");

        // 顺序：保留下来的必须是**最近**的一段，且保持落地顺序（判据面按 FIFO 读它）。
        bool fifo = true;
        for (int i = 1; i < log.Log.Count; i++)
        {
            if (log.Log[i].At <= log.Log[i - 1].At)
            {
                fifo = false;
            }
        }

        // 窗口的起始位置也要对：留下的是第 (over - capacity) 条起的那一段，不是开头那一段。
        long firstKept = log.Log.Count > 0 ? log.Log[0].At : -1;
        Check(ref failures, "log-window-fifo",
            fifo && firstKept == over - HostEventDispatch.LogCapacity,
            $"kept={log.Log.Count} first-at={firstKept} fifo={Flag(fifo)}");

        // 拒绝的表外事件既不进窗口也不进计数（与 RFC-21 的 no-new-events 同向：目录守门不许被绕开）。
        int before = log.Log.Count;
        log.Publish(PetEvent.Named("input.outside.catalog"));
        Check(ref failures, "log-outside-catalog-not-kept",
            log.Log.Count == before && log.TotalPublished == over && log.OutsideCatalog == 1,
            $"kept={log.Log.Count} published={log.TotalPublished} outside={log.OutsideCatalog}");
        return failures;
    }
}
