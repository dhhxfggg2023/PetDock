// tier-2 会话生命周期（RFC-33 §3）：本文件是「客体还在不在、还要不要驱动」唯一的状态所有者
// （RFC-29 §7.2 登记的欠账）——宿主、内容源、头脑桥只读 `Session`，不各自记。
// 四个非停止态：Running（可 Tick）、Paused（进程活着但不驱动）、Crashed（进程没了、可救）、
// Faulted（进程没了且不再救）。Stopped 只在从未启动或显式 Dispose 之后出现。
using System;
using System.Text.Json.Nodes;

namespace PetDock.Core.Sandbox;

/// <summary>tier-2 会话状态。宿主只读；状态迁移只由本类内的四个方法做。</summary>
public enum Tier2SessionState
{
    /// <summary>从未启动，或已经显式停止。</summary>
    Stopped = 0,

    /// <summary>进程活着、握手过、可以驱动。</summary>
    Running = 1,

    /// <summary>进程活着但不驱动（用户暂停 / 宿主隐藏）。恢复不重建进程。</summary>
    Paused = 2,

    /// <summary>进程没了，但在重启配额内——调用方可以 TryRecover。</summary>
    Crashed = 3,

    /// <summary>进程没了且不再救（重启配额用完）。宿主必须把它当成不可用。</summary>
    Faulted = 4,
}

public sealed partial class Tier2Sandbox
{
    /// <summary>一个窗口内最多允许的重启次数（RFC-33 §3.3）。</summary>
    public const int MaxRestarts = 2;

    /// <summary>重启配额的记账窗口（毫秒）。窗口外的崩溃重新开始计数。</summary>
    public const int RestartWindowMs = 60_000;

    private Tier2SessionState _session = Tier2SessionState.Stopped;
    private long _restartWindowStartMs;
    private int _restartsInWindow;

    /// <summary>当前会话状态。宿主与内容源据此决定「还要不要驱动它」。</summary>
    public Tier2SessionState Session => _session;

    /// <summary>累计重启次数（跨窗口累计，仅用于观测）。</summary>
    public long Restarts { get; private set; }

    /// <summary>驱动一帧；返回子进程是否仍在服务。暂停期间不下发 RPC —— 进程留着（重握手比省下的帧贵），
    /// 但不再让客体为它看不见的帧干活；返回值仍表示「进程还在」。</summary>
    public bool Tick(int dtMs)
    {
        if (_session == Tier2SessionState.Paused)
        {
            return IsLoaded;
        }

        if (!IsLoaded)
        {
            return false;
        }

        // 等待上限用 _requestTimeoutMs（Call 的默认），不是渲染预算：慢一帧不等于客体死了。
        Tier2Reply reply = Call("tick", new JsonObject { ["dt"] = dtMs });
        if (!reply.Ok)
        {
            Emit(SandboxEventKind.BudgetExceeded, ("stage", "tick"), ("err", reply.Error ?? "<none>"));
            if (ProcessExited())
            {
                NoteCrash("tick");
            }

            return false;
        }

        return IsLoaded;
    }

    /// <summary>暂停：进程留着，后续 Tick 不再向下发 RPC。重复暂停是幂等的。</summary>
    public void Pause()
    {
        if (_session != Tier2SessionState.Running)
        {
            return;
        }

        _session = Tier2SessionState.Paused;
        Emit(SandboxEventKind.Audit, ("tier2", "paused"), ("pid", Pid));
    }

    /// <summary>恢复：只回到 Running，不重建进程（重建走 TryRecover）。</summary>
    public void Resume()
    {
        if (_session != Tier2SessionState.Paused)
        {
            return;
        }

        _session = Tier2SessionState.Running;
        Emit(SandboxEventKind.Audit, ("tier2", "resumed"), ("pid", Pid));
    }

    /// <summary>救一次崩掉的会话：配额内重新 spawn + 握手，否则转 Faulted。返回 true 只在**这次真的
    /// 可用**时（进程在跑，或重 spawn 成功）；Running 而进程已不在是自相矛盾的状态（旧版「超时即杀、
    /// 不记崩溃」会造出来），按崩溃补记后照常救。</summary>
    public bool TryRecover()
    {
        if (_session == Tier2SessionState.Running)
        {
            if (IsLoaded)
            {
                return true;
            }
            NoteCrash("recover");
        }
        else if (_session is not (Tier2SessionState.Crashed or Tier2SessionState.Paused))
        {
            return false; // Stopped / Faulted：没有可救的东西
        }

        long now = Environment.TickCount64;
        if (now - _restartWindowStartMs > RestartWindowMs)
        {
            _restartWindowStartMs = now;
            _restartsInWindow = 0;
        }

        if (_restartsInWindow >= MaxRestarts)
        {
            _session = Tier2SessionState.Faulted;
            Emit(SandboxEventKind.GuestFault, ("stage", "recover"), ("why", "restart-quota-exhausted"),
                ("restarts", _restartsInWindow));
            return false;
        }

        StopProcess();
        // Start() 的复用判据是「_process 非空就复用它」，而 StopProcess 只停进程、不清引用。
        // 崩溃后不把它清掉，重 spawn 会在最开始那行直接返回 false（Pid 还是死掉那个），
        // 于是第一次恢复就被误判成「救不回来」。
        _process = null;
        _restartsInWindow++;
        Restarts++;
        if (Start())
        {
            Emit(SandboxEventKind.Audit, ("tier2", "restarted"), ("pid", Pid), ("restarts", Restarts),
                ("inWindow", _restartsInWindow));
            return true;
        }

        _session = Tier2SessionState.Faulted;
        Emit(SandboxEventKind.GuestFault, ("stage", "recover"), ("why", "start-failed"), ("restarts", Restarts));
        return false;
    }

    /// <summary>记一次崩溃：只由「进程真的没了」的路径调用（超时但进程活着是 BudgetExceeded，不是
    /// 崩溃，否则慢一帧就烧掉一次重启配额）。幂等：同一次死亡经多路被看到只落一次状态与一条事件；
    /// 崩溃计数由调用方维护，这里只改会话状态。</summary>
    private void NoteCrash(string stage)
    {
        if (_session is Tier2SessionState.Crashed or Tier2SessionState.Faulted)
        {
            return;
        }

        _session = Tier2SessionState.Crashed;
        Emit(SandboxEventKind.GuestFault, ("stage", stage), ("why", "process-exited"), ("crashes", Crashes),
            ("tail", Tail()));
    }

    /// <summary>进程是否已经退出。`_process` 可能已被 StopProcess 处置过（处置后读 HasExited 会抛），
    /// 所以这里吞掉异常并按「已退出」处理——本方法只在失败路径上被问「它是不是死了」。</summary>
    private bool ProcessExited()
    {
        try
        {
            return _process is null || _process.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
