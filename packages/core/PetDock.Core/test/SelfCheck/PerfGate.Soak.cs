// --perftest 的长跑（--soak）半边（D-03c 拆分：PerfGate.cs 已 382 行、上限 400）。
//
// 这一片存在的理由：D-14d 问「常驻 8 小时私有工作集增长 ≤5%」，而**毛工作集不是答案**。
// 2026-10-22 三次实测（180s / 180s / 300s，同一份 Release 二进制）把增长拆开看：
//   ws 12.8→19.1MB（+49%）、13.0→18.6MB（+43%）、13.4→23.5MB（+76%）
//   托管堆（强制回收后仍活着）0.63→0.62MB、0.65→0.62MB、0.64→0.62MB  ← 平的
//   GC 已提交 2.45→10.27MB、1.34→15.28MB、GC 次数 gc0/gc2 全程只涨 2  ← 段在涨，垃圾没回收
//   GDI 27→27、USER 26→27、句柄 324→320                              ← 非托管资源平的
// 结论：毛工作集跟着「GC 已提交但还没回收的段」走 —— 那段是**可回收**的（强制回收后立刻掉到
// 0.6MB），把它判成泄漏等于把「GC 还没跑」误读成「内存回不来」（同族误判 2026-10-19 也记过）。
// 所以判据改判「回收后仍驻留」的量 + 非托管对象增量；毛工作集只作展示，另有 M-04 的绝对上限兜底。
using System;
using System.Diagnostics;
using System.Globalization;

namespace PetDock.Hosting;

internal static partial class PerfGate
{
    /// <summary>长跑读数：毛工作集 + 托管堆（回收后）+ 非托管对象，四类并排，缺一不可。</summary>
    private readonly record struct SoakReadings(
        long WsMid, long WsEnd,
        long HeapMid, long HeapEnd,
        long AllocatedBytes,
        int Gc0Mid, int Gc0End, int Gc2Mid, int Gc2End,
        int GdiMid, int GdiEnd, int UserMid, int UserEnd,
        int HandlesMid, int HandlesEnd)
    {
        public double GrowthPct => WsMid > 0 ? (WsEnd - WsMid) * 100.0 / WsMid : 0;
        public double RetainedMb => (HeapEnd - HeapMid) / (1024.0 * 1024.0);
    }

    /// <summary>跑一段长跑并把四类读数打成一行 INFO。</summary>
    private static SoakReadings RunSoak(CpuSampler cpu, Process self, int seconds, long wsMid, int handlesMid)
    {
        // 采样窗**之前**取读数：这样窗内的分配才全部记在这一段账上。
        var guiBefore = GuiResources(self);
        long heapMid = GC.GetTotalMemory(true);
        int gc0Mid = GC.CollectionCount(0);
        int gc2Mid = GC.CollectionCount(2);
        long allocatedMid = GC.GetTotalAllocatedBytes(false);
        cpu.Sample(seconds * 1000, out _);
        long wsEnd = RetainedWorkingSetBytes(self);
        long heapEnd = GC.GetTotalMemory(true);
        var guiEnd = GuiResources(self);
        int handlesEnd = self.HandleCount;
        var readings = new SoakReadings(
            wsMid, wsEnd, heapMid, heapEnd, GC.GetTotalAllocatedBytes(false) - allocatedMid,
            gc0Mid, GC.CollectionCount(0), gc2Mid, GC.CollectionCount(2),
            guiBefore.Gdi, guiEnd.Gdi, guiBefore.User, guiEnd.User,
            handlesMid, handlesEnd);
        Emit($"INFO perf-soak seconds={seconds} ws-mid={Mb(wsMid)} ws-end={Mb(wsEnd)}"
            + $" soak-growth={Fmt(readings.GrowthPct)}%"
            + $" retained-heap={Fmt(readings.HeapMid / (1024.0 * 1024.0))}->{Fmt(readings.HeapEnd / (1024.0 * 1024.0))}MB"
            + $" allocated={Fmt(readings.AllocatedBytes / (1024.0 * 1024.0))}MB"
            + $" gc0={readings.Gc0Mid}->{readings.Gc0End} gc2={readings.Gc2Mid}->{readings.Gc2End}"
            + $" gdi={readings.GdiMid}->{readings.GdiEnd} user={readings.UserMid}->{readings.UserEnd}"
            + " (毛工作集跟着 GC 已提交段走，判据看 retained-heap 与 gdi/user)");
        return readings;
    }

    /// <summary>
    /// 取内存读数**前**先强制一次完整回收，再读私有工作集。
    ///
    /// 只回收、**不清工作集**：试过 EmptyWorkingSet（2026-10-22），读到的 ws 直接掉到 0.12~0.96MB
    /// —— 那不是「应用需要多少内存」，是「我们把页全扔了之后的残余」，M-04 的 150MB 上限当场变成
    /// 永不红的假门禁，所以退回「强制回收后读数」。
    /// 毛工作集因此仍带 GC 提交段的噪声（同一命令 13.7~23.5MB），故 D-14d 判 retained-heap +
    /// GDI/USER（PerfGate.cs），基线比对也改看 retained（PerfGate.Regression）。
    /// </summary>
    private static long RetainedWorkingSetBytes(Process p)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        return PrivateWorkingSetBytes(p);
    }

    /// <summary>GDI/USER 对象数（0 = GDI，1 = USER）。读不到时返回 -1，**不编 0**。</summary>
    private static (int Gdi, int User) GuiResources(Process p)
    {
        try
        {
            return ((int)GetGuiResources(p.Handle, 0), (int)GetGuiResources(p.Handle, 1));
        }
        catch
        {
            return (-1, -1);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
