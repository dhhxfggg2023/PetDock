// t9（docs/41 D-10b）：四接口的进程内纯逻辑桩件 —— 依赖隔离，不是 mock 冒充已验。
// 只做可控行为的最小实现；测试断言的是 core 对这些接口的用法，不是桩件自身。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PetDock.Core.PetRuntime;

namespace PetDock.Runtime.Tests;

/// <summary>可控的 IPetView 桩：SetAnimation 可编程为认/拒/抛；Describe/QueryFrame 可编程。</summary>
public sealed class StubView : IPetView
{
    private readonly Func<string, bool>? _setAnimation;
    private readonly Func<int, Frame>? _queryFrame;

    public StubView(
        Func<string, bool>? setAnimation = null,
        Func<int, Frame>? queryFrame = null,
        PetDescription? description = null)
    {
        _setAnimation = setAnimation;
        _queryFrame = queryFrame;
        Description = description ?? new PetDescription(
            new PetSize(96, 96), 30, false, Array.Empty<string>(), Array.Empty<string>());
    }

    public InterfaceMeta Meta { get; } = new("petdock.view.stub", "1.0");

    /// <summary>SetAnimation 收到的 clip id 序列（顺序 = 调用顺序）。</summary>
    public List<string> SetAnimationCalls { get; } = new();

    public PetDescription Description { get; set; }

    public bool Attached { get; private set; }

    public RenderTarget? Target { get; private set; }

    public PetViewContext? LastContext { get; private set; }

    public List<PetEvent> ReceivedEvents { get; } = new();

    public Task InitAsync(PetViewContext ctx)
    {
        LastContext = ctx;
        return Task.CompletedTask;
    }

    public PetDescription Describe() => Description;

    public void Attach(RenderTarget target)
    {
        Attached = true;
        Target = target;
    }

    public void Detach() => Attached = false;

    public Task DisposeAsync() => Task.CompletedTask;

    public bool SetAnimation(string clipId, bool restart = false)
    {
        SetAnimationCalls.Add(clipId);
        return _setAnimation is null || _setAnimation(clipId);
    }

    public Frame QueryFrame(int nowMs)
        => _queryFrame is null
            ? throw new InvalidOperationException("stub view 未装配 QueryFrame")
            : _queryFrame(nowMs);

    public void OnEvent(PetEvent ev) => ReceivedEvents.Add(ev);
}

/// <summary>可控的 IBrain 桩：Tick 可编程为产指令/抛异常。</summary>
public sealed class StubBrain : IBrain
{
    private readonly Func<int, WorldSnapshot, IReadOnlyList<BrainDirective>>? _tick;

    public StubBrain(Func<int, WorldSnapshot, IReadOnlyList<BrainDirective>>? tick = null)
    {
        _tick = tick;
    }

    public InterfaceMeta Meta { get; } = new("petdock.brain.stub", "1.0");

    public int Ticks { get; private set; }

    public List<PetEvent> Events { get; } = new();

    public Task InitAsync(BrainContext ctx) => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    public IReadOnlyList<BrainDirective> Tick(int dtMs, WorldSnapshot world)
    {
        Ticks++;
        return _tick is null ? Array.Empty<BrainDirective>() : _tick(dtMs, world);
    }

    public void OnEvent(PetEvent ev) => Events.Add(ev);
}

/// <summary>可控的 IPetState 桩。</summary>
public sealed class StubState : IPetState
{
    public int SchemaVersion { get; set; } = 1;

    public Dictionary<string, JsonNode?> Storage { get; } = new();

    public int Commits { get; private set; }

    public IReadOnlyDictionary<string, JsonNode?> ReadAll() => Storage;

    public JsonNode? Read(string key) => Storage.TryGetValue(key, out JsonNode? value) ? value : null;

    public void Write(IReadOnlyDictionary<string, JsonNode?> patch)
    {
        foreach (KeyValuePair<string, JsonNode?> kv in patch)
        {
            Storage[kv.Key] = kv.Value;
        }
    }

    public Task CommitAsync()
    {
        Commits++;
        return Task.CompletedTask;
    }

    public void Rollback() => Storage.Clear();

    public IReadOnlyDictionary<string, JsonNode?> Migrate(
        int from, int to, IReadOnlyDictionary<string, JsonNode?> snapshot) => snapshot;
}

/// <summary>可控的 IPointerSurface 桩：HitTest/OnPointerDown 可编程；记录被问询次数。</summary>
public sealed class StubSurface : IPointerSurface
{
    private readonly Func<PetPoint, bool>? _hitTest;
    private readonly Func<PetPoint, PointerButton, IPointerSession?>? _onDown;

    public StubSurface(
        string id,
        int priority = 0,
        Func<PetPoint, bool>? hitTest = null,
        Func<PetPoint, PointerButton, IPointerSession?>? onDown = null)
    {
        Id = id;
        Priority = priority;
        _hitTest = hitTest;
        _onDown = onDown;
    }

    public string Id { get; }

    public int Priority { get; }

    public int HitTests { get; private set; }

    public bool HitTest(PetPoint local)
    {
        HitTests++;
        return _hitTest is null || _hitTest(local);
    }

    public IPointerSession? OnPointerDown(PetPoint local, PointerButton button)
        => _onDown is null ? null : _onDown(local, button);
}

/// <summary>最小 IPointerSession 桩（记录收到的调用）。</summary>
public sealed class StubSession : IPointerSession
{
    public string? Cursor { get; set; }

    public List<PetPoint> Moves { get; } = new();

    public int Ups { get; private set; }

    public int Cancels { get; private set; }

    public void OnPointerMove(PetPoint local) => Moves.Add(local);

    public void OnPointerUp(PetPoint local) => Ups++;

    public void OnCancel() => Cancels++;
}
