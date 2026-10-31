// tier-2 的进程边界：作业对象（Job Object）。
//
// 为什么加它：宿主终止 tier-2 客体的**所有**路径（StopProcess → Kill(entireProcessTree)、正常 Dispose）
// 都以「宿主还活着」为前提。宿主自己被强杀 —— 任务管理器、蓝屏前的崩溃、更新搬运工接管 —— 时，
// 客体变成孤儿：继续握着 IPC 管道与自己的资源，用户看不见它，它也不属于任何窗口。
// 作业对象把客体挂到宿主身上：句柄一关（进程死掉时由内核回收），作业里的进程由内核终止，
// 与宿主怎么死的无关。
//
// 边界必须说清：**这不是沙箱**。它不拦网络、不拦文件、不拦注册表。它保证三件事：
//   ① 宿主死 → 客体死（KILL_ON_JOB_CLOSE）；
//   ② 客体只许有一个进程（ACTIVE_PROCESS = 1，防止它悄悄繁殖）；
//   ③ 内存上限写在作业上（PROCESS_MEMORY，超了由分配失败体现，而不是把整机拖进交换）。
// 界面与文档都不许把这三条说成「隔离」。
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PetDock.Core.Sandbox;

internal sealed class Tier2Job : IDisposable
{
    /// <summary>单个客体的内存上限（含它的子进程）。tier-2 是图形/头脑插件，1.5 GiB 足够，且能挡住跑飞的分配。</summary>
    public const long DefaultMemoryLimitBytes = 1536L * 1024 * 1024;

    private const uint KillOnJobClose = 0x2000;
    private const uint ActiveProcess = 0x8;
    private const uint ProcessMemory = 0x100;
    private const int ExtendedLimitInformation = 9;

    private IntPtr _handle;

    private Tier2Job(IntPtr handle) => _handle = handle;

    /// <summary>建一个作业并写好三条限制。失败返回 null 并给出原因（调用方照常启动，只是少一层边界）。</summary>
    public static Tier2Job? Create(long memoryLimitBytes, out string? why)
    {
        why = null;
        IntPtr handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            why = "create-failed";
            return null;
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = KillOnJobClose | ActiveProcess | ProcessMemory,
                ActiveProcessLimit = 1,
            },
            ProcessMemoryLimit = (UIntPtr)(ulong)Math.Max(64L * 1024 * 1024, memoryLimitBytes),
        };

        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!SetInformationJobObject(handle, ExtendedLimitInformation, buffer, (uint)size))
            {
                CloseHandle(handle);
                why = "limits-rejected";
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new Tier2Job(handle);
    }

    /// <summary>把一个已启动的进程放进作业。返回 false = 这次边界没落到这个进程上。</summary>
    public bool Assign(Process process)
    {
        if (_handle == IntPtr.Zero || process.HasExited)
        {
            return false;
        }

        return AssignProcessToJobObject(_handle, process.Handle);
    }

    /// <summary>关掉句柄：最后一个句柄消失时，作业里的进程由内核终止。</summary>
    public void Dispose()
    {
        IntPtr handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
