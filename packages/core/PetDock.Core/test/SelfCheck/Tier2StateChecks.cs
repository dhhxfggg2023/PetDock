// tier-2 超时语义的两条常驻判据（--hosttest，不建窗口）。
//
// ① tier2-frame-budget-does-not-kill：客体 20ms 才答一帧、宿主按**默认**渲染预算（8ms）构造时，
//    30 帧必须全部读到、Crashes 仍为 0、进程仍在（IsLoaded）。渲染预算只描述「一帧该多快」，
//    它**不**裁决进程存亡——2026-10-25 的线上缺陷正是把 8ms 当成了跨进程 stdio 往返的上限。
// ② tier2-crash-recorded：客体应答后自杀，宿主下一次 Call 必须把会话落地成 Crashed 并让 Crashes
//    自增（RFC-33 §3：崩溃必须有一个状态，停在 Running 的「假活」会让 TryRecover 以为无事可救）。
// 两条都不靠 sleep 猜时序：① 看返回值与计数，② 先等 IsLoaded 转 false（真事件）再发请求。
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

internal static class Tier2StateChecks
{
    /// <summary>判据出口（与 PluginHostSelfTest 的记账同形）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    private const int Frames = 30;

    private const string Capability = "ipc.tier2:petdock-selfcheck";

    public static void Run(Check check)
    {
        var sink = new SilentSink();
        var gate = new CapabilityGate(new[] { Capability }, null, sink);
        FrameBudgetDoesNotKill(check, gate, sink);
        CrashRecorded(check, gate, sink);

        // 这一条要有自己的 sink/gate：它会真的杀进程，别影响上面两条的计数。
        var jobSink = new SilentSink();
        var jobGate = new CapabilityGate(new[] { Capability }, null, jobSink);
        OrphanKilledByJob(check, jobGate, jobSink);
    }

    /// <summary>
    /// tier2-orphan-killed-by-job：**宿主被强杀**时客体不许活下来。
    ///
    /// StopProcess 的 Kill(entireProcessTree) 只能证明「宿主还活着时会杀」，证不了「宿主死了以后」——
    /// 宿主被任务管理器结束、或在更新交接时被杀，进程里的清理代码一行都不会跑。
    /// 判据做法：正常启动 → 关掉作业句柄（等价于进程死掉、内核回收句柄）→ 客体必须在几秒内消失。
    /// </summary>
    private static void OrphanKilledByJob(Check check, CapabilityGate gate, ISandboxEventSink sink)
    {
        var sandbox = new Tier2Sandbox(gate, sink, GuestCommand(), GuestArgs(Tier2Guest.SlowMode));
        bool up = false;
        bool bound = false;
        bool closed = false;
        bool gone = false;
        int pid = 0;
        try
        {
            up = sandbox.Start();
            pid = sandbox.Pid;
            bound = sandbox.JobBound;
            closed = sandbox.CloseJobForTest();
            gone = pid != 0 && WaitGone(pid, 5000);
        }
        finally
        {
            sandbox.Dispose();
        }

        check("tier2-orphan-killed-by-job", up && bound && closed && gone,
            $"up={up} pid={pid} job={bound} closed={closed} gone={gone}");
    }

    /// <summary>等一个 pid 消失（进程不在了 = 已经没了）。不用 sleep 猜时序，只轮询事实。</summary>
    private static bool WaitGone(int pid, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            try
            {
                using Process probe = Process.GetProcessById(pid);
                if (probe.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true; // 已经不在进程表里
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(50);
        }
    }

    private static void FrameBudgetDoesNotKill(Check check, CapabilityGate gate, ISandboxEventSink sink)
    {
        // 刻意**不传** requestTimeoutMs/frameBudgetMs：判的就是两个默认值各自的语义。
        var sandbox = new Tier2Sandbox(gate, sink, GuestCommand(), GuestArgs(Tier2Guest.SlowMode));
        bool up = false;
        int served = 0;
        long lastFrameId = 0;
        string note = string.Empty;
        try
        {
            up = sandbox.Start();
            for (int i = 0; i < Frames; i++)
            {
                if (!sandbox.TryQueryFrame(i * 33, out Tier2Frame? frame, out string? error) || frame is null)
                {
                    note = $"at={i} err={error ?? "<none>"}";
                    break;
                }

                if (frame.FrameId <= lastFrameId)
                {
                    note = $"at={i} frame-id-not-advancing id={frame.FrameId}";
                    break;
                }

                lastFrameId = frame.FrameId;
                served++;
            }
        }
        catch (Exception ex)
        {
            note = $"threw={ex.GetType().Name}";
        }

        bool ok = up && served == Frames && sandbox.Crashes == 0 && sandbox.IsLoaded;
        check(
            "tier2-frame-budget-does-not-kill",
            ok,
            $"up={up} served={served}/{Frames} crashes={sandbox.Crashes} timeouts={sandbox.Timeouts} loaded={sandbox.IsLoaded} {note}".TrimEnd());
        sandbox.Dispose();
    }

    private static void CrashRecorded(Check check, CapabilityGate gate, ISandboxEventSink sink)
    {
        var sandbox = new Tier2Sandbox(gate, sink, GuestCommand(), GuestArgs(Tier2Guest.SuicideMode));
        bool up = false;
        bool answered = false;
        bool dead = false;
        long crashesBefore = 0;
        Tier2Reply? after = null;
        try
        {
            up = sandbox.Start();
            answered = sandbox.Call("probe", new JsonObject()).Ok;
            crashesBefore = sandbox.Crashes;
            var clock = Stopwatch.StartNew();
            while (sandbox.IsLoaded && clock.ElapsedMilliseconds < 5000)
            {
                Thread.Yield(); // 等的是真事件（子进程退出），不是一段时间
            }

            dead = !sandbox.IsLoaded;
            after = sandbox.Call("probe", new JsonObject());
        }
        catch (Exception ex)
        {
            after = null;
            answered = false;
            check("tier2-crash-recorded", false, $"threw={ex.GetType().Name}");
            sandbox.Dispose();
            return;
        }

        bool ok = up && answered && dead && after is { Ok: false }
            && sandbox.Session == Tier2SessionState.Crashed && sandbox.Crashes == crashesBefore + 1;
        check(
            "tier2-crash-recorded",
            ok,
            $"up={up} answered={answered} dead={dead} after={after?.Error ?? "<none>"} session={sandbox.Session} crashes={crashesBefore}->{sandbox.Crashes}");
        sandbox.Dispose();
    }

    /// <summary>客体就是本 exe：单文件形态直接跑 exe，`dotnet exec` 形态补上 dll。</summary>
    private static string GuestCommand()
    {
        return File.Exists(SelfExe()) ? SelfExe() : "dotnet";
    }

    private static string GuestArgs(string mode)
    {
        return File.Exists(SelfExe())
            ? $"{Tier2Guest.Flag} {mode}"
            : $"exec \"{SelfDll()}\" {Tier2Guest.Flag} {mode}";
    }

    private static string SelfExe()
    {
        return Path.Combine(AppContext.BaseDirectory, "PetDock.Core.exe");
    }

    private static string SelfDll()
    {
        return Path.Combine(AppContext.BaseDirectory, "PetDock.Core.dll");
    }

    /// <summary>判据自用事件落点：不关心内容，只要 sink 不抛。</summary>
    private sealed class SilentSink : ISandboxEventSink
    {
        public void Emit(SandboxEvent evt)
        {
            _ = evt;
        }
    }
}
