// 陪伴层（companion）的宿主半边（2026-10-29，P4 插件线）。
//
// 职责只有五件：初始化 / 每帧 tick / 事件 / 桌面动作 / 面板命令。
// 它不解释任何玩法语义 —— 「饱食」「心情」「等级」都只存在于插件的 Report 文本里，
// 宿主只负责把 Report 摆到面板、把命令转回插件。这样下一步接音频或 agent 状态时，
// 内核不需要再改一次。
//
// 线程：tick / 事件 / 动作来自帧泵线程与窗口消息线程；Report 会被 UI 线程读。
// 所以状态层访问统一走 <see cref="LockedPetState"/>（与宿主写位置共用一道门），
// 插件的 Report 必须自己保证是快照（见 IPetCompanion 的注释）。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

public sealed partial class PetInstance
{
    private readonly List<IPetCompanion> _companions = new();
    private ICompanionHost? _companionHost;

    /// <summary>本实例的陪伴层（0..n；顺序 = 名册登记顺序）。</summary>
    public IReadOnlyList<IPetCompanion> Companions => _companions;

    /// <summary>陪伴层累计故障数（初始化 / tick / 命令抛异常都记一次）。</summary>
    public int CompanionFaults { get; private set; }

    /// <summary>说一句话的出口（由窗口层接到 <see cref="LayeredFrameWindow"/> 的气泡上）。</summary>
    public event CompanionSpeechDelegate? Speech;

    /// <summary>
    /// 装上陪伴层（装配期由名册调用一次）。每次实例化都用**新的**实现对象：
    /// 名册里登记的是类型，不是共享单例 —— 两只宠物共用一个陪伴对象会互相改对方的数值。
    /// </summary>
    internal void InstallCompanions(IReadOnlyList<IPetCompanion> companions)
    {
        _companions.Clear();
        _companions.AddRange(companions);
    }

    /// <summary>
    /// 初始化全部陪伴层（与 brain 同时机：画板与状态层就绪之后）。
    /// 失败按 brain 同一条降级链处理：记 fault、继续跑其余角色 —— 一只坏插件不该让宠物起不来。
    /// </summary>
    private void InitCompanions(ResourceBudget budget)
    {
        if (_companions.Count == 0)
        {
            return;
        }

        _companionHost = new CompanionHost(this);
        var context = new CompanionContext(
            Id, DisplayName, new LockedPetState(StateLayer, StateGate), budget, _companionHost);
        foreach (IPetCompanion companion in _companions)
        {
            try
            {
                using (PluginLog.Scope(companion.Meta.Id))
                {
                    companion.InitAsync(context).GetAwaiter().GetResult();
                }
            }
            catch (Exception error)
            {
                CompanionFaults++;
                LastFailure = $"companion-init-failed {companion.Meta.Id} {error.GetType().Name}: {error.Message}";
                KernelLog.Write($"companion-init-failed instance={Id} id={companion.Meta.Id}"
                    + $" {error.GetType().Name}: {error.Message}");
            }
        }
    }

    /// <summary>
    /// 一次陪伴层 tick。抛异常的那一个本 tick 结果作废（不影响其他角色），并记 fault。
    /// 指令在 brain 之后落地：同一项属性上陪伴层覆盖 brain（见 CompanionTick 的注释）。
    /// </summary>
    public void TickCompanions(int dtMs, WorldSnapshot world)
    {
        if (_companions.Count == 0)
        {
            return;
        }

        foreach (IPetCompanion companion in _companions)
        {
            CompanionTick tick;
            try
            {
                using (PluginLog.Scope(companion.Meta.Id))
                {
                    tick = companion.Tick(dtMs, world);
                }
            }
            catch (Exception error)
            {
                CompanionFaults++;
                LastFailure = $"companion-tick-failed {companion.Meta.Id} {error.GetType().Name}: {error.Message}";
                KernelLog.Write($"companion-tick-failed instance={Id} id={companion.Meta.Id}"
                    + $" {error.GetType().Name}: {error.Message}");
                continue;
            }

            if (tick.Directives is { Count: > 0 } directives)
            {
                Arbiter.Apply(directives);
            }

            if (!string.IsNullOrWhiteSpace(tick.Say))
            {
                SayText(tick.Say!);
            }
        }
    }

    /// <summary>总线事件（与 brain 收到的是同一串；目录守门已在事件总线里做过）。</summary>
    public void NotifyCompanions(PetEvent evt)
    {
        if (_companions.Count == 0)
        {
            return;
        }

        foreach (IPetCompanion companion in _companions)
        {
            try
            {
                using (PluginLog.Scope(companion.Meta.Id))
                {
                    companion.OnEvent(evt);
                }
            }
            catch (Exception error)
            {
                CompanionFaults++;
                KernelLog.Write($"companion-event-failed instance={Id} id={companion.Meta.Id}"
                    + $" type={evt.Type} {error.GetType().Name}: {error.Message}");
            }
        }
    }

    /// <summary>桌面动作（点击 / 拖拽 / 休息 / 抛掷 / 落地）。</summary>
    public void NotifyCompanions(CompanionGesture gesture)
    {
        if (_companions.Count == 0)
        {
            return;
        }

        foreach (IPetCompanion companion in _companions)
        {
            try
            {
                using (PluginLog.Scope(companion.Meta.Id))
                {
                    companion.OnGesture(gesture);
                }
            }
            catch (Exception error)
            {
                CompanionFaults++;
                KernelLog.Write($"companion-gesture-failed instance={Id} id={companion.Meta.Id}"
                    + $" gesture={gesture} {error.GetType().Name}: {error.Message}");
            }
        }
    }

    /// <summary>
    /// 面板命令入口。返回 null = 成功；非空 = 给用户看的原因（找不到角色 / 插件抛异常）。
    /// 命令自己返回的话由这里说出去（走同一个气泡），这样面板与桌面看到的是同一句话。
    /// </summary>
    public string? InvokeCompanion(string companionId, string commandId)
    {
        IPetCompanion? target = null;
        foreach (IPetCompanion companion in _companions)
        {
            if (string.Equals(companion.Meta.Id, companionId, StringComparison.Ordinal))
            {
                target = companion;
                break;
            }
        }

        if (target is null)
        {
            return I18n.Get("ui.companion.missing");
        }

        try
        {
            string? say;
            using (PluginLog.Scope(target.Meta.Id))
            {
                say = target.Invoke(commandId);
            }

            if (!string.IsNullOrWhiteSpace(say))
            {
                SayText(say!);
            }

            return null;
        }
        catch (Exception error)
        {
            CompanionFaults++;
            LastFailure = $"companion-command-failed {target.Meta.Id} {error.GetType().Name}: {error.Message}";
            KernelLog.Write($"companion-command-failed instance={Id} id={target.Meta.Id}"
                + $" command={commandId} {error.GetType().Name}: {error.Message}");
            return I18n.Get("ui.companion.failed");
        }
    }

    /// <summary>某个陪伴层的只读状态；没有这个 id 时返回 null。</summary>
    public CompanionReport? CompanionReport(string companionId)
    {
        foreach (IPetCompanion companion in _companions)
        {
            if (string.Equals(companion.Meta.Id, companionId, StringComparison.Ordinal))
            {
                return companion.Report;
            }
        }

        return null;
    }

    /// <summary>下线全部陪伴层（有界等待，超时只记日志），然后由调用方提交状态。</summary>
    private void DisposeCompanions()
    {
        foreach (IPetCompanion companion in _companions)
        {
            try
            {
                if (!companion.DisposeAsync().Wait(1000))
                {
                    KernelLog.Write($"companion-dispose-over-budget instance={Id} id={companion.Meta.Id} budget=1000ms");
                }
            }
            catch (AggregateException error)
            {
                KernelLog.Write($"companion-dispose-failed instance={Id} id={companion.Meta.Id}"
                    + $" {error.InnerException?.GetType().Name}");
            }
        }
    }

    private void SayText(string text)
    {
        try
        {
            Speech?.Invoke(text);
        }
        catch (Exception error)
        {
            KernelLog.Write($"companion-speech-failed instance={Id} {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>宿主口子：只暴露语言 / 落盘 / 说话三项（见 ICompanionHost）。</summary>
    private sealed class CompanionHost : ICompanionHost
    {
        private readonly PetInstance _owner;

        public CompanionHost(PetInstance owner) => _owner = owner;

        public string Language => I18n.Language;

        public bool CommitState() => _owner.CommitState();

        public void Say(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                _owner.SayText(text);
            }
        }
    }

    /// <summary>
    /// 给陪伴层用的状态代理：与宿主写位置共用 PetInstance 的那道门。
    /// 为什么不直接把 PersistentPetState 交出去：它的缓冲区是一个普通字典，
    /// 帧泵线程的 tick 与 UI 线程的窗口移动会同时写它（这是内核已有的并发面，
    /// 陪伴层只是第一个真的会从两条线程写状态的调用方）。
    /// Commit 不在这里加锁：落盘走 <see cref="ICompanionHost.CommitState"/>，那条路已经进同一道门。
    /// </summary>
    private sealed class LockedPetState : IPetState
    {
        private readonly IPetState? _inner;
        private readonly object _gate;

        public LockedPetState(IPetState? inner, object gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public int SchemaVersion => _inner?.SchemaVersion ?? 1;

        public IReadOnlyDictionary<string, JsonNode?> ReadAll()
        {
            if (_inner is null)
            {
                return new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            }

            lock (_gate)
            {
                return _inner.ReadAll();
            }
        }

        public JsonNode? Read(string key)
        {
            if (_inner is null)
            {
                return null;
            }

            lock (_gate)
            {
                return _inner.Read(key);
            }
        }

        public void Write(IReadOnlyDictionary<string, JsonNode?> patch)
        {
            if (_inner is null)
            {
                return;
            }

            lock (_gate)
            {
                _inner.Write(patch);
            }
        }

        public Task CommitAsync() => _inner?.CommitAsync() ?? Task.CompletedTask;

        public void Rollback()
        {
            if (_inner is null)
            {
                return;
            }

            lock (_gate)
            {
                _inner.Rollback();
            }
        }

        public IReadOnlyDictionary<string, JsonNode?> Migrate(
            int from, int to, IReadOnlyDictionary<string, JsonNode?> snapshot)
        {
            if (_inner is null)
            {
                return snapshot;
            }

            lock (_gate)
            {
                return _inner.Migrate(from, to, snapshot);
            }
        }
    }
}

/// <summary>陪伴层说一句话的回调（自声明代替系统泛型委托，理由见 HostingTypes.cs 的先例）。</summary>
public delegate void CompanionSpeechDelegate(string text);
