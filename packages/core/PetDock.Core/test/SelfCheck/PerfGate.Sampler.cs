// --perftest 的采样器半边（D-03c 拆分）：CPU 采样 + psapi 私有工作集读数。
//
// 为什么单独一个文件：PerfGate.Throttle.cs 已经 366 行、PerfGate.cs 340 行，D-03c 的 400 行上限
// 是硬门禁（且它对 test/ 下的 .cs 同样生效 —— lint-core.mjs 的 D-03c 判定不经过 SKIP_TEST_DIRS）。
//
// **采样窗里的驱动源**：RFC-21 之后门禁测的是 ResidentFrameLoop 那条产品路径，而它的两个入口
// （Advance 由泵线程调、Reevaluate 由 UI 滴答调）在没有消息循环的 --perftest 里只有前者会跑。
// 全屏档停泵、泵自愈这两件事都只能靠 Reevaluate 驱动 —— 所以采样器每 250ms 调一次它，
// 与 UiHost 的 UI 滴答同频。这不是「给判据开后门」，是把产品里真实存在的那个驱动面补上。
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static partial class PerfGate
{
    /// <summary>UI 滴答周期（与 UiHost 的 250ms 同频）。</summary>
    private const int PollIntervalMs = 250;

    /// <summary>CPU 采样：TotalProcessorTime 的增量 / 墙钟 / 核数。冷启动噪声靠 Warmup 隔开。</summary>
    private sealed class CpuSampler
    {
        private readonly Process _p;
        private readonly ResidentFrameLoop _loop;

        public CpuSampler(Process p, ResidentFrameLoop loop)
        {
            _p = p;
            _loop = loop;
        }

        public void Warmup(int ms) => Sample(ms, out _);

        public double Sample(int ms, out TimeSpan span) => Sample(ms, out span, null);

        /// <summary>
        /// 采 <paramref name="ms"/> 毫秒的 CPU 占用。每 <see cref="PollIntervalMs"/> 调一次
        /// <see cref="ResidentFrameLoop.Reevaluate"/>（泵停着时唯一的驱动源），再调 <paramref name="poll"/>。
        /// </summary>
        public double Sample(int ms, out TimeSpan span, PollTick? poll)
        {
            _p.Refresh();
            TimeSpan before = _p.TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            long nextPoll = 0;
            while (wall.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                if (wall.ElapsedMilliseconds >= nextPoll)
                {
                    nextPoll = wall.ElapsedMilliseconds + PollIntervalMs;
                    // Reevaluate 只判定与起停泵，不提交帧（全屏恢复那一帧除外）—— 与产品同一条路。
                    _loop.Reevaluate();
                    poll?.Invoke();
                }

                Thread.Sleep(20);
            }

            wall.Stop();
            _p.Refresh();
            span = wall.Elapsed;
            double cpuMs = (_p.TotalProcessorTime - before).TotalMilliseconds;
            return cpuMs / span.TotalMilliseconds / Environment.ProcessorCount * 100.0;
        }
    }

    /// <summary>
    /// 整机负载的一次快照（RFC-40 §3.2.1）：<c>GetSystemTimes</c> 的 idle / kernel / user 三个累计值。
    /// 用法是**同一次运行内两快照相减**：<c>SystemAmbient.Of().BusyPct(start)</c> →
    /// <c>1 − idleΔ / (kernelΔ + userΔ)</c>，与 <c>\Processor(_Total)</c> 同口径。
    ///
    /// 为什么不用 <c>System.Diagnostics.PerformanceCounter</c>：它要额外的 NuGet 包与一次计数器注册，
    /// 而同一个数 GetSystemTimes 就能给（RFC-40 §3.2.1 已否决）。
    /// 为什么 kernelΔ 已经含 idle：GetSystemTimes 的 kernel 时间是「内核态全部时间」，idle 是它的子集，
    /// 所以分母 = kernelΔ + userΔ 就是「总时间」，不减 idle（减了就重复扣，读数会偏高）。
    /// </summary>
    internal readonly struct SystemAmbient
    {
        private readonly ulong _idle;
        private readonly ulong _kernel;
        private readonly ulong _user;

        private SystemAmbient(ulong idle, ulong kernel, ulong user)
        {
            _idle = idle;
            _kernel = kernel;
            _user = user;
        }

        internal static SystemAmbient Of()
        {
            return GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user)
                ? new SystemAmbient(ToUlong(idle), ToUlong(kernel), ToUlong(user))
                : default;
        }

        /// <summary>相对 <paramref name="earlier"/> 这一段的「整机忙」百分比（0~100）。取不到 → 0。</summary>
        internal double BusyPct(SystemAmbient earlier)
        {
            ulong idleDelta = _idle - earlier._idle;
            ulong totalDelta = (_kernel - earlier._kernel) + (_user - earlier._user);
            if (totalDelta == 0)
            {
                return 0;
            }

            double busy = (1.0 - (double)idleDelta / totalDelta) * 100.0;
            // 计时器回绕或跨 CPU 热插拔时这个比会跑出 [0,100]；钳制而不是报一个不可能是真的数。
            return Math.Clamp(busy, 0.0, 100.0);
        }

        private static ulong ToUlong(FILETIME t) => ((ulong)t.dwHighDateTime << 32) | t.dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    /// <summary>
    /// PROCESS_MEMORY_COUNTERS_EX2。`PrivateWorkingSetSize` 只在 EX2 里存在——早先按
    /// PROCESS_MEMORY_COUNTERS_EX 声明却多写了一个同名字段，API 按 EX 的 cb 填充，
    /// 那个字段永远读到 0，于是 D-14b 的读数恒为 0.00MB、判据退化成恒真（本机实录已修正）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public int cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
        public UIntPtr PrivateWorkingSetSize;
        public UIntPtr SharedWorkingSetSize;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX2 counters, int size);

    // GDI/USER 对象的读数与 P/Invoke 在 PerfGate.Soak.cs（长跑取证半边），此处不再重复声明。
}
