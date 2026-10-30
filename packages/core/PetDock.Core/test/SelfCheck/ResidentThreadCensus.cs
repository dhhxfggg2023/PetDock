// --residenttest 的线程归因工具箱（RFC-26 §3）：采样窗内「哪个线程在烧 CPU」。
//
// 为什么单独成文件：D-03c 规定单文件 ≤400 行，而归因面（每线程 CPU 差分、线程角色、
// 以及 --threadcensus 的全量差分与 RIP/模块采样）与判据面互不依赖，拆开后各自都在预算内。
// 这些成员属于同一个 partial 类，可见性不变。
// 本机 2026-10-23 的收获就是它给出的：常驻空闲烧 ~1 核的是 petdock-audit-writer 线程。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PetDock.Hosting;

internal static partial class ResidentPerfProbe
{
    /// <summary>当前线程的 OS 线程号（ProcessThread.Id 也是 OS 号，可用来对账）。</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>每线程 CPU 累计（线程退出后读不到就跳过，**不编 0**）。</summary>
    private static Dictionary<int, TimeSpan> ThreadCpu(Process p)
    {
        var map = new Dictionary<int, TimeSpan>();
        try
        {
            foreach (ProcessThread thread in p.Threads)
            {
                try
                {
                    map[thread.Id] = thread.TotalProcessorTime;
                }
                catch (Exception)
                {
                    // 线程在枚举与读取之间退出了：这一条不计入，也不当作 0 参与比较。
                }
            }
        }
        catch (Exception)
        {
            // 读线程表失败（权限/进程已退出）：返回空表，判据侧只打 count=0。
        }

        return map;
    }

    private static string TopThreads(Dictionary<int, TimeSpan> before, Dictionary<int, TimeSpan> after)
    {
        return string.Join(",", after
            .Select(kv => (Id: kv.Key, Ms: (kv.Value - (before.TryGetValue(kv.Key, out TimeSpan b) ? b : TimeSpan.Zero))
                .TotalMilliseconds))
            .OrderByDescending(row => row.Ms)
            .Take(3)
            .Select(row => row.Id.ToString(CultureInfo.InvariantCulture) + ":"
                + row.Ms.ToString("0", CultureInfo.InvariantCulture) + "ms"));
    }

    // ===== 临时诊断（2026-10-23，不属产品；定位「常驻形态烧 ~1 核」的线程）=====
    // 为什么需要它：TopThreads 只打前 3 个，而「哪个线程在烧」需要的是**全量**差分 +
    // 线程创建时刻（CreateTime）+ 该线程是否就是采样线程本身，否则只能靠猜。
    // 用法：dotnet exec ... --residenttest --threadcensus [--tcensus-interval 250]
    private static bool ThreadCensusWanted(string[] args)
        => args.Any(a => string.Equals(a, "--threadcensus", StringComparison.Ordinal));

    private static int CensusIntervalMs(string[] args)
        => Math.Max(50, ArgInt(args, "--tcensus-interval", 250));

    private static string DumpThreads(Process self, Dictionary<int, TimeSpan> reference, int selfTid)
    {
        var sb = new StringBuilder();
        int rows = 0;
        foreach (ProcessThread t in self.Threads)
        {
            try
            {
                TimeSpan cpu = t.TotalProcessorTime;
                TimeSpan delta = cpu - (reference.TryGetValue(t.Id, out TimeSpan b) ? b : TimeSpan.Zero);
                string started;
                try
                {
                    started = t.StartTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    started = "?";
                }

                if (rows > 0)
                {
                    sb.Append(' ');
                }

                sb.Append("t").Append(t.Id.ToString(CultureInfo.InvariantCulture))
                    .Append(":+").Append(delta.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)).Append("ms")
                    .Append("/start=").Append(started)
                    .Append(t.Id == selfTid ? "/SELF" : string.Empty);
                rows++;
            }
            catch (Exception)
            {
                // 线程在枚举与读取之间退出：跳过，不编 0。
            }
        }

        return "rows=" + rows.ToString(CultureInfo.InvariantCulture) + " " + sb;
    }

    /// <summary>按线程节拍采样：每个节拍打一行全量差分（找出持续 vs 突发）。</summary>
    private static void CensusLoop(Process self, int intervalMs, int seconds)
    {
        int selfTid = (int)GetCurrentThreadId();
        Emit($"CHECK-DISPLAY resident-census-start interval={intervalMs}ms seconds={seconds}s"
            + $" processorCount={Environment.ProcessorCount} selfTid={selfTid}");
        for (int i = 0; i < seconds; i++)
        {
            Dictionary<int, TimeSpan> before = ThreadCpu(self);
            Thread.Sleep(intervalMs);
            Emit("CHECK-DISPLAY resident-census-sample " + DumpThreads(self, before, selfTid));
            Emit("CHECK-DISPLAY resident-census-native " + NativeStackWatch(self, before, selfTid));
        }
    }

    // ===== 临时诊断 2：原生上下文采样（找出「哪个模块在烧」）=====
    // 没有 dotnet-stack，所以自己来：SuspendThread + GetThreadContext 读 RIP/RSP/RBP 与
    // [RSP] 处的返回地址，再用 GetModuleHandleEx(FROM_ADDRESS) 反查模块名。**只对最热的那个
    // 线程**做（挂起别人哪怕几微秒也是侵入），每次采样只挂起/恢复一次，异常一律吞掉。
    private static string NativeStackWatch(Process self, Dictionary<int, TimeSpan> before, int selfTid)
    {
        try
        {
            int hot = 0;
            double bestMs = 0;
            foreach (ProcessThread t in self.Threads)
            {
                try
                {
                    double ms = (t.TotalProcessorTime - (before.TryGetValue(t.Id, out TimeSpan b) ? b : TimeSpan.Zero))
                        .TotalMilliseconds;
                    if (ms > bestMs)
                    {
                        bestMs = ms;
                        hot = t.Id;
                    }
                }
                catch (Exception)
                {
                }
            }

            if (hot == 0 || hot == selfTid)
            {
                return "hot=none";
            }

            IntPtr handle = OpenThread(ThreadSuspendResume | ThreadGetContext | ThreadQueryLimitedInformation, false, hot);
            if (handle == IntPtr.Zero)
            {
                return $"hot=t{hot} open-failed err={Marshal.GetLastWin32Error()}";
            }

            string role = ThreadRole(handle);

            try
            {
                int suspended = SuspendThread(handle);
                if (suspended < 0)
                {
                    return $"hot=t{hot} suspend-failed err={Marshal.GetLastWin32Error()}";
                }

                try
                {
                    // CONTEXT 必须 16 字节对齐：交给 VirtualAlloc 保证，自己填 ContextFlags。
                    IntPtr raw = VirtualAlloc(IntPtr.Zero, ContextSize + 64, MemCommit | MemReserve, PageReadWrite);
                    if (raw == IntPtr.Zero)
                    {
                        return $"hot=t{hot} valloc-failed err={Marshal.GetLastWin32Error()}";
                    }

                    try
                    {
                        long aligned = ((long)raw + 15L) & ~15L;
                        IntPtr buf = (IntPtr)aligned;
                        const uint flags = ContextAmd64 | ContextControl | ContextInteger | ContextSegments
                            | ContextFloatingPoint | ContextDebugRegisters;
                        Marshal.WriteInt32(buf, OffsetContextFlags, (int)flags);
                        if (!GetThreadContext(handle, buf))
                        {
                            return $"hot=t{hot} context-failed err={Marshal.GetLastWin32Error()}";
                        }

                        long rip = Marshal.ReadInt64(buf, OffsetRip);
                        long rsp = Marshal.ReadInt64(buf, OffsetRsp);
                        long rbp = Marshal.ReadInt64(buf, OffsetRbp);
                        long rcx = Marshal.ReadInt64(buf, OffsetRcx);
                        long rdx = Marshal.ReadInt64(buf, OffsetRdx);
                        long r8 = Marshal.ReadInt64(buf, OffsetR8);
                        long r9 = Marshal.ReadInt64(buf, OffsetR9);
                        long atRsp = 0;
                        var stack = new StringBuilder(96);
                        for (int d = 0; d < 8; d++)
                        {
                            try
                            {
                                long w = Marshal.ReadInt64((IntPtr)(rsp + (8 * d)));
                                if (d == 0)
                                {
                                    atRsp = w;
                                }

                                stack.Append(' ').Append(Module((nint)w));
                            }
                            catch (Exception)
                            {
                                break;
                            }
                        }

                        return $"hot=t{hot} role={role} cpu=+{bestMs:0}ms"
                            + $" rip={Module((nint)rip)}"
                            + $" rbp={Module((nint)rbp)}"
                            + $" rcx={Module((nint)rcx)} rdx={Module((nint)rdx)}"
                            + $" r8={Module((nint)r8)} r9={Module((nint)r9)}"
                            + $" stack[0..7]={stack}";
                    }
                    finally
                    {
                        VirtualFree(raw, UIntPtr.Zero, MemRelease);
                    }
                }
                finally
                {
                    ResumeThread(handle);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch (Exception error)
        {
            return "native-watch-failed " + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>线程角色：CLR/我们自己在 SetThreadDescription 里写的名字（.NET 6+ 的 Thread.Name 会落到这里）。</summary>
    private static string ThreadRole(IntPtr handle)
    {
        try
        {
            if (GetThreadDescription(handle, out IntPtr ptr) >= 0 && ptr != IntPtr.Zero)
            {
                string? text = Marshal.PtrToStringUni(ptr);
                Marshal.FreeHGlobal(ptr);
                return string.IsNullOrEmpty(text) ? "<empty>" : text.Replace(' ', '_');
            }
        }
        catch (Exception)
        {
        }

        return "<unnamed>";
    }

    /// <summary>地址落在哪个模块里（FROM_ADDRESS 反查；失败给 hex）。</summary>
    private static string Module(IntPtr address)
    {
        if (address == IntPtr.Zero)
        {
            return "null";
        }

        var sb = new StringBuilder(512);
        IntPtr module;
        if (GetModuleHandleExW(FromAddress | UnchangedRefCount, address, out module) && module != IntPtr.Zero)
        {
            uint len = GetModuleFileNameW(module, sb, sb.Capacity);
            if (len > 0)
            {
                string full = sb.ToString();
                int cut = full.LastIndexOf('\\');
                return (cut >= 0 ? full.Substring(cut + 1) : full) + "+0x" + (address.ToInt64() - module.ToInt64()).ToString("x", CultureInfo.InvariantCulture);
            }
        }

        return "0x" + address.ToInt64().ToString("x", CultureInfo.InvariantCulture);
    }

    private const uint ThreadSuspendResume = 0x0002;
    private const uint ThreadGetContext = 0x0008;
    private const uint ThreadQueryLimitedInformation = 0x0800;
    private const uint ContextAmd64 = 0x00100000;
    private const uint ContextControl = 0x00100001;
    private const uint ContextInteger = 0x00100002;
    private const uint ContextSegments = 0x00100004;
    private const uint ContextFloatingPoint = 0x00100008;
    private const uint ContextDebugRegisters = 0x00100010;
    private const uint FromAddress = 0x00000004;
    private const uint UnchangedRefCount = 0x00000001;
    private const uint MemCommit = 0x00001000;
    private const uint MemReserve = 0x00002000;
    private const uint MemRelease = 0x00008000;
    private const uint PageReadWrite = 0x04;

    // x64 CONTEXT 的固定偏移（winnt.h + 官方 offsetof 表）：见下方 ThreadContext64 的注释。
    private const int ContextSize = 1232;
    private const int OffsetContextFlags = 48;
    private const int OffsetRcx = 128;
    private const int OffsetRdx = 136;
    private const int OffsetRsp = 152;
    private const int OffsetRbp = 160;
    private const int OffsetR8 = 184;
    private const int OffsetR9 = 192;
    private const int OffsetRip = 248;

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadContext64
    {
        public ulong P1Home, P2Home, P3Home, P4Home, P5Home, P6Home;
        public uint ContextFlags, MxCsr;
        public ushort Cs, Ds, Es, Fs, Gs, Ss;
        public uint EFlags;
        public ulong Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;
        public ulong Rax, Rcx, Rdx, Rbx, Rsp, Rbp, Rsi, Rdi, R8, R9, R10, R11, R12, R13, R14, R15, Rip;
        // 后面还有 XMM/浮点保存区，本探针不读它，但结构体大小必须留够（GetThreadContext 按 sizeof 写入）。
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1000)]
        public byte[] Tail;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inheritHandle, int threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int SuspendThread(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResumeThread(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadContext(IntPtr handle, IntPtr context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetThreadDescription(IntPtr handle, out IntPtr description);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder buffer, int size);
}
