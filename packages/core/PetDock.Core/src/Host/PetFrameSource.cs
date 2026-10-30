// 把一只宠物实例接成分层窗的内容源（原规格 11 §3 的视图接口 → M0 的 IFrameContentSource）。
//
// 这是 t1 契约 §4.2 要求的那一步的落点：**内容源的发现统一到 PluginHost**，
// 而 M0 的两个接口（IFrameContentProvider / IFrameContentSource）**保留、不改名**。
// 本类就是两者之间的桥：LayeredFrameWindow 仍然只认 IFrameContentSource（M0 语义不动），
// 而它背后现在是「PluginHost 名册里那个视图实现产出的帧」（M2 语义）。
//
// 帧尺寸的获取顺序（**偏离 §10 表一处并写明**）：画板尺寸要到窗口实测尺寸才知道，
// 而窗口的构造要求先有一个 IFrameContentSource。所以本类先挂着一个待定尺寸，
// 由 LayeredFrameWindow 的第一次 GetFrameContent(实际尺寸) 触发实例的 init/describe/attach，
// 并在返回 null 之前把实例启动起来 —— 顺序仍然是 init → describe → attach → running，
// 只是触发点从「窗口创建前」挪到「首次取帧时」。
using System;
using System.Drawing;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>把 PetInstance 的当前帧交给分层窗的内容源适配器。</summary>
public sealed class PetFrameSource
    : IFrameContentSource, IFrameSourceStats, IFrameContentInspector, IFrameReuseInspector, IFrameHitSurface
{
    private readonly PetInstance _instance;
    private readonly EventBus _bus;
    private PetSize _lastSize = new(0, 0);
    private long _served;
    private WorldProbe _probe = new();
    private IntPtr _windowHandle;
    private int _lastNowMs;
    private PetPoint? _lastCursor;

    public PetFrameSource(PetInstance instance)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        // 总线挂在实例自己的事件口上：仲裁器记账、事件目录守门、观察者投递共用同一个 Log，
        // 于是「发出去的事件」与「自检读到的 Log」是同一份事实，不是两套。
        _bus = new EventBus(instance.Arbiter.Events);
        // 行为层的事件入口（原规格 11:137 总线顺序 = 到达顺序）。M4 修复前这条订阅不存在，
        // 插件实现的 OnEvent 在整个进程生命周期里一次都没被调用过。
        instance.Arbiter.Events.Published += OnBusEvent;
    }

    /// <summary>
    /// 世界采样与 Brain 调度的宿主窗口句柄（M4 契约 §7.2）。
    /// 由窗口持有者在创建 LayeredFrameWindow 后注入；0 = 尚无窗口（WorldSnapshot 全兜底值）。
    /// </summary>
    public IntPtr WindowHandle { get => _windowHandle; set { _windowHandle = value; _probe = Probe ?? new WorldProbe(); } }

    /// <summary>宿主注入的探针（自检面用它注入 TimeOfDayMinutes；null = 墙钟）。</summary>
    public WorldProbe? Probe { get; set; }

    /// <summary>累计交付的帧数（自检判据：帧循环真的在从这条链上取帧）。</summary>
    public long Served => _served;

    /// <summary>IFrameSourceStats 的视图：把「交付过几帧」摆到接口上，判据读得到。</summary>
    long IFrameSourceStats.FramesServed => _served;

    /// <summary>
    /// 现在交付的是不是真内容。降级链走到占位图档之后，本源交出的就是**内核占位帧**，
    /// 那不是视图实现画出来的 —— 这时必须自述为「不是真内容」，
    /// 否则外面会把占位帧当成「宠物还在动」（原规格 11 §9 的降级语义）。
    /// </summary>
    public bool DeliversRealContent => _instance.Display.Tier < DisplayTier.Placeholder;

    /// <summary>
    /// RFC-21 §3.5：外表帧是否与上一次是同一个位图对象。零成本 —— 判据本来就在
    /// <see cref="PetInstance.LastFrameReused"/>（对象身份比较，不是像素比较）。
    /// 静态节流的静止档靠它判「内容没变」；判定属主是 FrameThrottlePolicy，本类只自述事实。
    /// </summary>
    public bool LastFrameReused => _instance.LastFrameReused;

    /// <summary>
    /// RFC-41 §3.1(b)：把命中面申报出去，常驻窗口才问得到内容源。
    /// 不自己比 alpha —— 阀值的唯一实现在 <see cref="Frame.HitsAt"/>，这里转发
    /// <see cref="PetInstance.HitsAt"/>（隐藏档与无帧都 fail-closed 为穿透）。
    /// </summary>
    public bool Hits(int x, int y) => _instance.HitsAt(x, y);

    /// <summary>所属实例（宿主侧记账与挂起转发用）。</summary>
    public PetInstance Instance => _instance;

    /// <summary>最近一次取帧失败的原因（null = 成功；供 stderr 取证）。</summary>
    public string? LastFailure { get; private set; }

    /// <summary>最近一次交付的帧尺寸（诊断面）。</summary>
    public PetSize LastSize => _lastSize;

    /// <summary>
    /// 取当前帧内容。返回 null = 本帧无内容（宿主回退到不透明占位帧），
    /// 这与 原规格 11 §9「QueryFrame 抛异常 → 该帧 dropped」是同一条语义的两种表达：
    /// dropped 的帧在这里就是 null，合成器据此不写像素。
    /// </summary>
    public Bitmap? GetFrameContent(Size frameSize)
    {
        if (frameSize.Width <= 0 || frameSize.Height <= 0)
        {
            return null;
        }

        try
        {
            StartIfNeeded(frameSize);
            // M4 契约 §7.2：StartIfNeeded 后 QueryFrame 前插 WorldProbe.Sample + TickBrain。
            // 采样失败绝不打断取帧（KernelLog 留痕，快照用兜底值）。
            WorldSnapshot world;
            int nowMs = NowMs();
            try
            {
                world = _probe.Sample(_windowHandle);
            }
            catch (Exception probeError)
            {
                KernelLog.Write($"world-probe-failed instance={_instance.Id} {probeError.GetType().Name}: {probeError.Message}");
                world = WorldSnapshot.Empty(new PetRect(0, 0, 0, 0));
            }

            if (_instance.frameDirty && _instance.LastSetAnimation is (string clip, bool restart))
            {
                // 契约 §5.2：事件观察者推翻了位姿 —— 下一帧前重放 setAnimation 并清零。
                _instance.View.SetAnimation(clip, restart);
                _instance.frameDirty = false;
            }

            int dt = nowMs - _lastNowMs;
            _lastNowMs = nowMs;
            int frameDt = dt < 0 ? 0 : dt;

            // 契约 §1.3 第 3 步：帧循环每帧发一条 core.clock.tick（serial，{dt}）。
            // M4 修复前这一行不存在 —— 事件目录里唯一「每帧必有」的事件在生产链上没有发布点，
            // 于是任何订阅时钟的行为层都永远收不到它（M4-VERIFY-r2.md 的 A 号缺口）。
            PublishTick(frameDt);

            // 契约 §1.3 第 4 步：输入事件（固定类型序 pointer.move → down → up → key.*）。
            // M4 里 down/up 由指针会话那条链发（PetInstance.PointerDown/Up），key.* 因未授权无源；
            // 这里补的是 pointer.move —— 它的源是帧泵对光标的采样，不是 OS 钩子（原规格 16 §3）。
            // 用 LastScreenCursor（原始屏幕坐标）而不是 world.Cursor：后者按契约 §7.1 在窗口外是 null，
            // 用它会让「鼠标还没移到宠物身上」这段最需要反应的时间窗里一条事件都发不出去。
            PublishPointerMove(_probe.LastScreenCursor);

            _instance.TickBrain(frameDt, world);
            _instance.TickCompanions(frameDt, world);
            // RFC-35 §3.3：原规格 11:260 的 10 秒会话预算要有人每帧问一次 —— 此前 EnforceSessionBudget
            // 在全仓零调用方，卡住的会话永远不会被宿主取消。
            _instance.EnforceSessionBudget(nowMs);
            Frame frame = _instance.QueryFrame(nowMs);
            _lastSize = new PetSize(frame.BitmapHandle.PixelWidth, frame.BitmapHandle.PixelHeight);
            _served++;
            LastFailure = null;
            return frame.BitmapHandle.Bitmap;
        }
        catch (Exception error)
        {
            LastFailure = $"{error.GetType().Name}: {error.Message}";
            KernelLog.Write($"frame-source-failed instance={_instance.Id} {LastFailure}");
            return null; // 无内容 = 该帧 dropped，合成器回退占位
        }
    }

    /// <summary>
    /// 发一条 input.key.down / up（emit，`{vkey, modifiers}`，原规格 16 §3）。
    /// 与 pointer.move 的区别：那条由帧泵采样光标得到（host-generated），这条必须来自**真实的
    /// 设备钩子**——原规格 16 §3 把 key 事件的 Source 定为 `capability only`，轮询或伪造都不成立。
    /// 钩子本身在 UiHost 里，只有插件持 `input.keyboardHook` 时才装（原规格 16:56）。
    /// </summary>
    public void PublishKeyStroke(int vkey, int modifiers, bool down)
    {
        try
        {
            _bus.Publish(new PetEvent(
                down ? EventCatalog.KeyDown : EventCatalog.KeyUp,
                PetEvent.NowMs(),
                new System.Text.Json.Nodes.JsonObject
                {
                    ["vkey"] = vkey,
                    ["modifiers"] = modifiers,
                }));
        }
        catch (Exception error)
        {
            KernelLog.Write($"key-event-failed instance={_instance.Id} {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>窗口事件（core.window.focus / moved / dpiChanged）的生产入口。
    /// 由 <c>Program.ComposeWindow</c> 把 <c>LayeredFrameWindow.WindowEvent</c> 接到这里：
    /// M4 修复前那条 event 全仓零订阅者，三条窗口事件发了也无人消费（G 号缺口）。
    /// 类型必须在事件目录内，否则 HostEventDispatch 会 fail-closed 拒收并留审计行 —— 那正是期望行为。
    /// </summary>
    public void PublishWindowEvent(string type, System.Text.Json.Nodes.JsonObject? payload)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        try
        {
            // 同 PublishTick：Publish 才写 Log + 触发 Published；Parallel 只喂订阅表。
            _bus.Publish(new PetEvent(type, PetEvent.NowMs(), payload));
        }
        catch (Exception error)
        {
            KernelLog.Write($"window-event-failed instance={_instance.Id} type={type}"
                + $" {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>实例下线时释放它（帧源是实例的持有者）。</summary>
    public void Dispose()
    {
        _instance.Arbiter.Events.Published -= OnBusEvent;
        _instance.Dispose();
    }

    /// <summary>
    /// 发一条 core.clock.tick。
    /// **必须走 Publish 而不是 Serial**：`EventBus.Serial`/`Parallel`/`Bail`/`Waterfall` 只把事件
    /// 分发给 `_subs` 里注册过的监听者，**不写 Log、也不触发 `Published`**；而生产链上没有任何
    /// 监听者注册过（行为层是经 `Published` → `IBrain.OnEvent` 收事件的）。用 Serial 的结果是
    /// 事件既不进 Log 也不到 brain，静默蒸发 —— 这正是第一次修复后「时钟在走、brain 没反应」的原因。
    /// `Publish` 负责两件生产链真正需要的事：目录守门 + 记 Log + 触发 `Published`。
    /// </summary>
    private void PublishTick(int dtMs)
    {
        try
        {
            _bus.Publish(new PetEvent(EventCatalog.ClockTick, PetEvent.NowMs(), new System.Text.Json.Nodes.JsonObject
            {
                ["dt"] = dtMs,
            }));
        }
        catch (Exception error)
        {
            KernelLog.Write($"clock-tick-failed instance={_instance.Id} {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// 发一条 input.pointer.move（serial，`{pos, velocity?}`）。**只在光标真的动了才发**：
    /// 每帧都发会把一条「指针移动」事件变成「帧心跳」的别名，行为层就没法区分
    /// 「鼠标在动」与「时间在走」——而 M4 的判据正是要区分这两类。
    /// velocity 只在两次采样都有值时给（原规格 16 §3 的 `velocity?: f64` 可选语义）。
    /// </summary>
    private void PublishPointerMove(PetPoint? cursor)
    {
        if (cursor is not PetPoint now)
        {
            _lastCursor = null;
            return;
        }

        if (_lastCursor is PetPoint previous
            && Math.Abs(now.X - previous.X) < 0.5 && Math.Abs(now.Y - previous.Y) < 0.5)
        {
            return; // 没动：不发，也不清 _lastCursor（下次一动就是真实位移）
        }

        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["pos"] = new System.Text.Json.Nodes.JsonArray(now.X, now.Y),
        };
        if (_lastCursor is PetPoint from)
        {
            double dx = now.X - from.X;
            double dy = now.Y - from.Y;
            payload["velocity"] = Math.Sqrt((dx * dx) + (dy * dy));
        }

        _lastCursor = now;
        try
        {
            // 同 PublishTick：必须走 Publish（写 Log + 触发 Published），不能走 Serial（只喂订阅表）。
            _bus.Publish(new PetEvent(EventCatalog.PointerMove, PetEvent.NowMs(), payload));
        }
        catch (Exception error)
        {
            KernelLog.Write($"pointer-move-failed instance={_instance.Id} {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// 事件观察者：把落地的每条事件交给行为层（原规格 11:137）。
    /// 目录守门已在 HostEventDispatch.Publish 里做过，到这里的一定是表内事件。
    /// 行为层抛异常不许打穿宿主循环 —— 记一行日志后继续。
    /// </summary>
    private void OnBusEvent(PetEvent evt)
    {
        IBrain? brain = _instance.Brain;
        if (brain is not null)
        {
            try
            {
                brain.OnEvent(evt);
            }
            catch (Exception error)
            {
                KernelLog.Write($"brain-on-event-failed instance={_instance.Id} type={evt.Type}"
                    + $" {error.GetType().Name}: {error.Message}");
            }
        }

        // 陪伴层与 brain 收同一串事件；没有 brain 的实例也必须收到（否则「只有陪伴层」的装配看不到事件）。
        _instance.NotifyCompanions(evt);
    }

    /// <summary>首次取帧时把实例推到 running —— init/describe/attach 的顺序写在 PetInstance 里。</summary>
    private void StartIfNeeded(Size frameSize)
    {
        if (_instance.State == InstanceState.Running)
        {
            // 预挂接（导入/恢复要在换源前验素材）用的是 128 逻辑尺寸；窗口实测尺寸在 150% DPI
            // 下是 192。画板与帧缓冲必须严格同尺寸，否则 DrawBitmap 拒帧——按实测值重挂一次。
            if (_instance.Target is { } target
                && target.PixelWidth == frameSize.Width
                && target.PixelHeight == frameSize.Height)
            {
                return;
            }

            _instance.Retarget(frameSize.Width, frameSize.Height, _instance.Target?.Scale ?? 1.0);
            return;
        }

        double scale = _instance.Target?.Scale ?? 1.0;
        _instance.AttachAndStart(frameSize.Width, frameSize.Height, scale);
        _instance.Start();
    }

    /// <summary>传给 QueryFrame 的 now：实例自己的毫秒时钟（单调；不用墙钟日期，免得时间调整改帧号）。</summary>
    private static int NowMs() => (int)(Math.Abs(Environment.TickCount64) % int.MaxValue);

    /// <summary>
    /// 帧时钟的对外形态（int 域）。**交互会话的开始时刻与 10 秒预算判定必须用同一个时钟**：
    /// `(int)Environment.TickCount64` 在机器空转超过 24.8 天后会与 `NowMs()` 相差
    /// int.MaxValue 的整数倍，两个时钟混用会让「刚按下去」被判成「已经超时」，会话当场被强制取消。
    /// </summary>
    internal static int ClockMs() => NowMs();
}
