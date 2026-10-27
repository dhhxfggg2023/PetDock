// self-test 用的桩件实现（确定性、零 I/O、可注入故障）。全部落在 tests 之外是因为它们必须
// 被自检进程直接构造（判据要能精确制造「连续 8 次超时」「QueryFrame 抛异常」这类情形，
// 而不是靠运气把真机器跑爆）。桩件不实现任何素材读取 —— 它们本身就是「内容源真的装上了」的反例面。
//
// 桩件一律在 PetDock.Hosting 命名空间下（自检进程内的宿主侧代码），不当插件分发。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>可控的视图桩件：帧数可数、异常可注入、耗时可控、片段声明可挂。</summary>
internal sealed partial class StubView : IPetView, IClipCatalog
{
    private readonly List<AnimationClip> _clips = new();
    private readonly Dictionary<int, Bitmap> _frames = new();
    private RenderTarget? _target;
    private int _index;
    private string _clip = "idle";

    /// <summary>每次 QueryFrame 故意消耗的毫秒（忙等；用于制造超预算帧）。</summary>
    public double SpinMs { get; set; }

    /// <summary>true 时 QueryFrame 抛异常（制造 dropped 帧与降级链）。</summary>
    public bool ThrowOnQuery { get; set; }

    /// <summary>SetAnimation 是否认识传入的 clip（false = 如实拒绝）。</summary>
    public bool AcceptClips { get; set; } = true;

    public int QueryCalls { get; private set; }

    public int SetAnimationCalls { get; private set; }

    public bool Detached { get; private set; }

    public bool Disposed { get; private set; }

    public InterfaceMeta Meta => new("petdock.sample.stubview", "1.0.0");

    public IReadOnlyList<AnimationClip> Clips => _clips;

    public string CurrentClip => _clip;

    /// <summary>挂上一个片段声明（C1 的「声明值」一侧）。</summary>
    public void DeclareClip(string id, int frames, double fps, ClipLoop loop = ClipLoop.Loop) =>
        _clips.Add(new AnimationClip(id, frames, fps, loop));

    /// <summary>
    /// 最近一次 init 收到的上下文（自检读它断言宿主把素材根传到了实现方，
    /// RFC-15 §3.2 第 3 跳）。null = 还没 init 过。
    /// </summary>
    public PetViewContext? LastContext { get; private set; }

    /// <summary>
    /// 所有桩件视图收到的 init 上下文（静态记录）。
    /// 为什么静态：换宠物时宿主会造一份**新的**实现对象（名册里那一份正被旧实例用着），
    /// 拿不到新对象就观测不到它收到的素材根（RFC-15 §3.2 第 3 跳的判据要观测的就是它）。
    /// </summary>
    public static readonly List<PetViewContext> ReceivedContexts = new();

    /// <summary>清空静态记录（判据之间互不干扰）。</summary>
    public static void ClearReceivedContexts() => ReceivedContexts.Clear();

    public System.Threading.Tasks.Task InitAsync(PetViewContext ctx)
    {
        LastContext = ctx;
        ReceivedContexts.Add(ctx);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public PetDescription Describe() =>
        new(new PetSize(8, 8), 12, false, new[] { "frames" }, Array.Empty<string>());

    public void Attach(RenderTarget target) => _target = target;

    public void Detach() => Detached = true;

    public void OnEvent(PetEvent ev) => LastEvent = ev;

    /// <summary>最近一次收到的宿主事件（判据：宿主通知真的送到了实现方）。</summary>
    public PetEvent? LastEvent { get; private set; }

    public System.Threading.Tasks.Task DisposeAsync()
    {
        Disposed = true;
        foreach (Bitmap bitmap in _frames.Values)
        {
            bitmap.Dispose();
        }

        _frames.Clear();
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public bool SetAnimation(string clipId, bool restart = false)
    {
        SetAnimationCalls++;
        if (!AcceptClips)
        {
            return false;
        }

        _clip = clipId;
        if (restart)
        {
            _index = 0;
        }

        return true;
    }

    /// <summary>
    /// 取帧：帧索引由 now 决定（**同一 now 必返回同一帧**，原规格 11:120），
    /// 而像素对象按索引缓存 —— 空闲档判据（原规格 11:225）读的正是这个对象身份。
    /// </summary>
    public Frame QueryFrame(int nowMs)
    {
        QueryCalls++;
        if (ThrowOnQuery)
        {
            throw new InvalidOperationException("stub-view-query-failed");
        }

        if (SpinMs > 0)
        {
            double until = FrameBudgetGuard.DefaultClock() + SpinMs;
            while (FrameBudgetGuard.DefaultClock() < until)
            {
                System.Threading.Thread.SpinWait(64);
            }
        }

        if (_target is null)
        {
            throw new InvalidOperationException("stub-view-not-attached");
        }

        int frames = _clips.Count > 0 ? _clips[0].Frames : 1;
        double fps = _clips.Count > 0 && _clips[0].Fps > 0 ? _clips[0].Fps : 12;
        _index = frames <= 0 ? 0 : (int)((nowMs / (1000.0 / fps)) % frames);
        Bitmap bitmap = BitmapFor(_index);
        return new Frame(
            _target.Bitmaps.Acquire(bitmap),
            new PetSize(_target.PixelWidth, _target.PixelHeight),
            _target.Scale,
            ClearRect: null,
            Hitmap: HitmapFor(_target.PixelWidth, _target.PixelHeight));
    }

    /// <summary>桩件的命中图：中心 2x2 可命中，其余 0（用来验「hitmap &gt; alpha」与「alpha=0 放行」）。</summary>
    private static byte[] HitmapFor(int width, int height)
    {
        var map = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                map[(y * width) + x] =
                    (byte)(x >= (width / 2) - 1 && x <= width / 2 && y >= (height / 2) - 1 && y <= height / 2 ? 1 : 0);
            }
        }

        return map;
    }

    // 帧的绘制在 PetContractStubs.Art.cs（同一个 partial 类，D-03c 单文件行数上限）。
}

/// <summary>可控的行为层桩件：一次 tick 交出一批故意冲突的指令（用于验仲裁顺序）。</summary>
internal sealed class StubBrain : IBrain
{
    private readonly List<BrainDirective> _queue = new();

    /// <summary>true 时 Tick 抛异常（验 原规格 11:259 的「指令全部作废 + degraded」）。</summary>
    public bool ThrowOnTick { get; set; }

    public int TickCalls { get; private set; }

    public int ReceivedDt { get; private set; }

    public InterfaceMeta Meta => new("petdock.sample.stubbrain", "1.0.0");

    public void Enqueue(params BrainDirective[] directives) => _queue.AddRange(directives);

    public void Clear() => _queue.Clear();

    public System.Threading.Tasks.Task InitAsync(BrainContext ctx) => System.Threading.Tasks.Task.CompletedTask;

    public System.Threading.Tasks.Task DisposeAsync() => System.Threading.Tasks.Task.CompletedTask;

    public IReadOnlyList<BrainDirective> Tick(int dtMs, WorldSnapshot world)
    {
        TickCalls++;
        ReceivedDt += dtMs;
        if (ThrowOnTick)
        {
            throw new InvalidOperationException("stub-brain-tick-failed");
        }

        var batch = new List<BrainDirective>(_queue);
        _queue.Clear();
        return batch;
    }

    public void OnEvent(PetEvent ev) => LastEvent = ev;

    public PetEvent? LastEvent { get; private set; }
}

/// <summary>交互层桩件：可设优先级、可拒绝、可命中。</summary>
internal sealed class StubSurface : IPointerSurface
{
    private readonly List<string> _log;

    public StubSurface(string id, int priority, bool hit, List<string> log)
    {
        Id = id;
        Priority = priority;
        Hits = hit;
        _log = log;
    }

    public string Id { get; }

    public int Priority { get; }

    public bool Hits { get; set; }

    /// <summary>true 时返回 null = 拒绝，宿主必须转给下一个 surface（原规格 11:176）。</summary>
    public bool Decline { get; set; }

    public bool HitTest(PetPoint local)
    {
        _log.Add($"{Id}:hit={(Hits ? "1" : "0")}");
        return Hits;
    }

    public IPointerSession? OnPointerDown(PetPoint local, PointerButton button)
    {
        _log.Add($"{Id}:down");
        return Decline ? null : new StubSession(this, _log);
    }

    private sealed class StubSession : IPointerSession
    {
        private readonly StubSurface _owner;
        private readonly List<string> _log;

        public StubSession(StubSurface owner, List<string> log)
        {
            _owner = owner;
            _log = log;
        }

        public string? Cursor => "hand";

        public void OnPointerMove(PetPoint local) => _log.Add($"{_owner.Id}:move");

        public void OnPointerUp(PetPoint local) => _log.Add($"{_owner.Id}:up");

        public void OnCancel() => _log.Add($"{_owner.Id}:cancel");
    }
}

/// <summary>状态层桩件：按 原规格 11 §6 的语义把缓冲区与落盘分开，越限抛 StateLimitException。</summary>
internal sealed class StubState : IPetState
{
    private readonly Dictionary<string, System.Text.Json.Nodes.JsonNode?> _buffer = new();
    private readonly Dictionary<string, System.Text.Json.Nodes.JsonNode?> _disk = new();

    public int SchemaVersion => 1;

    /// <summary>commit 次数（判据：write 之后没有 commit 时落盘必须没有变化）。</summary>
    public int Commits { get; private set; }

    public int Rollbacks { get; private set; }

    /// <summary>单键上限（原规格 11:212 的 8 KiB；桩件按它判）。</summary>
    public int KeyLimitBytes { get; set; } = 8 * 1024;

    public IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> ReadAll() =>
        new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(_buffer);

    public System.Text.Json.Nodes.JsonNode? Read(string key) =>
        _buffer.TryGetValue(key, out System.Text.Json.Nodes.JsonNode? value) ? value : null;

    public void Write(IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> patch)
    {
        foreach (KeyValuePair<string, System.Text.Json.Nodes.JsonNode?> pair in patch)
        {
            string text = pair.Value?.ToJsonString() ?? string.Empty;
            if (text.Length > KeyLimitBytes)
            {
                throw new StateLimitException(pair.Key, text.Length, KeyLimitBytes);
            }

            _buffer[pair.Key] = pair.Value;
        }
    }

    public System.Threading.Tasks.Task CommitAsync()
    {
        Commits++;
        _disk.Clear();
        foreach (KeyValuePair<string, System.Text.Json.Nodes.JsonNode?> pair in _buffer)
        {
            _disk[pair.Key] = pair.Value;
        }

        return System.Threading.Tasks.Task.CompletedTask;
    }

    public void Rollback()
    {
        Rollbacks++;
        // 原规格 11:213 逐字：「write 之后到 commit 之间是内存中的修改。**崩溃：缓冲区全丢**，磁盘无影响」。
        // 所以 rollback = 缓冲清零（未落盘的修改全丢），磁盘内容不动；
        // 它不是「回到上次 commit 的快照」—— 那会把「丢掉的修改」重新装回来。
        _buffer.Clear();
        foreach (KeyValuePair<string, System.Text.Json.Nodes.JsonNode?> pair in _disk)
        {
            _buffer[pair.Key] = pair.Value;
        }
    }

    public IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> Migrate(
        int from, int to, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> snapshot)
    {
        // 纯函数：建副本、加一个迁移标记、绝不 mutate 入参。
        var migrated = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(snapshot);
        migrated["schemaVersion"] = System.Text.Json.Nodes.JsonValue.Create(to);
        return migrated;
    }

    /// <summary>落盘内容（判据读它证明 commit 之外没有任何写入）。</summary>
    public IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> Disk =>
        new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(_disk);
}

/// <summary>
/// 脚本化时钟：按脚本逐次返回时间戳，让「连续 8 次超预算」成为确定性事件。
/// 读取契约见 FrameBudgetGuard.ClockReadsPerFrame：**每一帧恰好读两次**（帧起、帧末），
/// 所以脚本按 2 个一组给出；全局跨度由 FrameBudgetGuard 自己从帧起点之差算出来。
/// </summary>
internal sealed class ScriptedClock
{
    private readonly List<double> _values = new();
    private int _cursor;
    private double _base;
    private double _globalSpan;

    public double Now() => _cursor < _values.Count ? _values[_cursor++] : _values.Count > 0 ? _values[^1] : 0;

    /// <summary>累计声明的全局跨度（判据用它佐证 global 预算那一档的来源）。</summary>
    public double GlobalSpan => _globalSpan;

    /// <summary>
    /// 追加一帧的两个读数。语义（与 FrameBudgetGuard 的「每帧读两次」契约配套）：
    /// 本帧**起点 = 当前基准**、**终点 = 基准 + elapsedMs**，然后基准前进 globalMs
    /// —— globalMs 就是「下一帧什么时候才开始」，它包含宿主自身的循环开销。
    /// 所以全局跨度 = 帧起点的差 + 本帧耗时；只给 elapsed 而基准不动，全局跨度会恒等于单帧耗时，
    /// 「宿主开销把全局预算撑爆」这件事就永远测不出来（本次实测：global-over 恒为 0）。
    /// </summary>
    public void AppendFrame(double elapsedMs, double globalMs)
    {
        _values.Add(_base);
        _values.Add(_base + elapsedMs);
        _base += globalMs;
        _globalSpan += globalMs;
    }

    /// <summary>已追加的帧数。</summary>
    public int Frames => _values.Count / FrameBudgetGuard.ClockReadsPerFrame;
}
