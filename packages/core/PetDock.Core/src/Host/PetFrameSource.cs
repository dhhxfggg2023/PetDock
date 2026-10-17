// 把一只宠物实例接成分层窗的内容源（docs/11 §3 的视图接口 → M0 的 IFrameContentSource）。
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
public sealed class PetFrameSource : IFrameContentSource, IFrameSourceStats
{
    private readonly PetInstance _instance;
    private PetSize _lastSize = new(0, 0);
    private long _served;
    private WorldProbe _probe = new();
    private IntPtr _windowHandle;
    private int _lastNowMs;

    public PetFrameSource(PetInstance instance)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
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

    /// <summary>所属实例（宿主侧记账与挂起转发用）。</summary>
    public PetInstance Instance => _instance;

    /// <summary>最近一次取帧失败的原因（null = 成功；供 stderr 取证）。</summary>
    public string? LastFailure { get; private set; }

    /// <summary>最近一次交付的帧尺寸（诊断面）。</summary>
    public PetSize LastSize => _lastSize;

    /// <summary>
    /// 取当前帧内容。返回 null = 本帧无内容（宿主回退到不透明占位帧），
    /// 这与 docs/11 §9「QueryFrame 抛异常 → 该帧 dropped」是同一条语义的两种表达：
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
            _instance.TickBrain(dt < 0 ? 0 : dt, world);
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

    /// <summary>实例下线时释放它（帧源是实例的持有者）。</summary>
    public void Dispose()
    {
        _instance.Dispose();
    }

    /// <summary>首次取帧时把实例推到 running —— init/describe/attach 的顺序写在 PetInstance 里。</summary>
    private void StartIfNeeded(Size frameSize)
    {
        if (_instance.State == InstanceState.Running)
        {
            return;
        }

        double scale = _instance.Target?.Scale ?? 1.0;
        _instance.AttachAndStart(frameSize.Width, frameSize.Height, scale);
        _instance.Start();
    }

    /// <summary>传给 QueryFrame 的 now：实例自己的毫秒时钟（单调；不用墙钟日期，免得时间调整改帧号）。</summary>
    private static int NowMs() => (int)(Math.Abs(Environment.TickCount64) % int.MaxValue);
}
