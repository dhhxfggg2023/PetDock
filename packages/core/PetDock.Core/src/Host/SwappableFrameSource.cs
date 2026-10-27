// 可替换的内容源代理（RFC-15 §3.3）。
//
// 为什么要有这个类：换宠物需要「窗口还在、内容源换掉」，而 LayeredFrameWindow 的内容源
// 是构造期定死的 readonly 字段。两种改法：
//   (a) 给窗口加一个 ReplaceContent —— 那就把「现在该显示哪只」这个**宿主层**的决定
//       塞进合成器层。原规格 12 §2.1 说合成器只做「一种窗 + 像素」，
//       RFC-14 也因此否决过同样的形状（把渲染节奏的决定权挪进合成器层）。
//   (b) 在宿主层放一个代理，内容源由代理持有、代理可换 —— 合成器层**一行不改**，
//       它仍然只认 IFrameContentSource，仍然只管像素。
// 本类是 (b)。
//
// 为什么代理不缓存帧：每帧都问当前源要内容，切换是**下一帧**生效，
// 不会出现「新旧各出一帧」的撕裂。
using System;
using System.Drawing;
using System.Text.Json.Nodes;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

/// <summary>
/// 内容源代理：持有「当前内容源」，可随时替换。
/// 窗口持有的是它，替换的是它里面的那一份。
/// </summary>
internal sealed class SwappableFrameSource
    : IFrameContentSource, IFrameSourceStats, IFrameContentInspector, IFrameReuseInspector, IFrameHitSurface
{
    private IFrameContentSource _current;
    private bool _disposed;

    public SwappableFrameSource(IFrameContentSource current, PetSurfaceRoster? roster = null)
    {
        _current = current ?? throw new ArgumentNullException(nameof(current));
        Roster = roster;
    }

    /// <summary>
    /// 装配时用的名册（换宠物要靠它再造一个视图实现）。
    /// null = 装配时没有名册（自检与退化路径）。
    /// </summary>
    public PetSurfaceRoster? Roster { get; }

    /// <summary>
    /// 当前内容源（可能是内核兜底）。
    /// 读用 Volatile：换源发生在 UI 线程上，而帧泵在**后台线程**上读它 ——
    /// 不加屏障，帧泵可能永远看不到新源（换宠物「没反应」但没有任何报错）。
    /// </summary>
    public IFrameContentSource Current => Volatile.Read(ref _current);

    /// <summary>当前内容源是不是「真的视图帧源」（不是内核纯色兜底）。</summary>
    public bool HasRealSource => Current is PetFrameSource;

    /// <summary>
    /// 窗口句柄（世界探针要它）。代理持有它，替换内容源时**转交**给新源 ——
    /// 新源拿不到句柄就永远采样不到真实鼠标位置（原规格 16 §3 的指针事件源）。
    /// </summary>
    public IntPtr WindowHandle
    {
        get => _windowHandle;
        set
        {
            _windowHandle = value;
            if (Current is PetFrameSource source)
            {
                source.WindowHandle = value;
            }
        }
    }

    private IntPtr _windowHandle;

    /// <summary>
    /// 转发一次按键事件给当前的那只宠物。键盘钩子装一次就用很久，
    /// 而宠物会换 —— 钩子必须每次问「现在这只」是谁，不能在装的时候把旧源抓住不放。
    /// </summary>
    public void PublishKeyStroke(int vkey, int modifiers, bool down)
    {
        if (Current is PetFrameSource source)
        {
            source.PublishKeyStroke(vkey, modifiers, down);
        }
    }

    /// <summary>
    /// 当前内容源交付的是不是真内容。代理必须**问里面那一份**，不能自己答 ——
    /// 代理自己答就等于「装上了代理 = 真内容」，兜底帧会被算成真内容（M0 的老坑）。
    /// </summary>
    public bool DeliversRealContent =>
        Current is IFrameContentInspector inspector ? inspector.DeliversRealContent : HasRealSource;

    /// <summary>
    /// RFC-21 §3.5：外表帧是否与上一次是同一个位图对象。与 <see cref="DeliversRealContent"/>
    /// 同一个纪律 —— **问里面那一份**，不自己答：换宠物后判据必须跟着新源走，
    /// 代理若自己答，换源后「内容没变」会被判在旧宠物身上。
    /// </summary>
    public bool LastFrameReused =>
        Current is IFrameReuseInspector reuse && reuse.LastFrameReused;

    /// <summary>
    /// RFC-41 §3.1(b)：转发命中面。与 <see cref="DeliversRealContent"/> 同一个纪律 ——
    /// **问里面那一份**，代理自己答会让窗口在换宠物后仍按旧宠物的命中图裁决。
    /// 里面那份没申报命中面时一律穿透（fail-closed）。
    /// </summary>
    public bool Hits(int x, int y) => Current is IFrameHitSurface surface && surface.Hits(x, y);

    /// <summary>
    /// 换成新的内容源。旧源**不**在这里释放 —— 换源与释放是两件事，
    /// 顺序由调用方决定（先换、后释放；先释放会让窗口在切换窗口里露出空白帧）。
    /// </summary>
    public void Replace(IFrameContentSource next)
    {
        if (_disposed)
        {
            return;
        }

        Volatile.Write(ref _current, next ?? throw new ArgumentNullException(nameof(next)));
        if (next is PetFrameSource source)
        {
            source.WindowHandle = _windowHandle;
        }
    }

    /// <summary>
    /// 转发窗口事件给当前源。窗口事件是**按窗口**发生的，但接收者是**当前那只宠物** ——
    /// 换宠物后事件必须跟着走，否则新宠物收不到窗口移动，旧的又还挂着订阅。
    /// </summary>
    public void PublishWindowEvent(string type, JsonObject? payload)
    {
        if (Current is PetFrameSource source)
        {
            source.PublishWindowEvent(type, payload);
        }
    }

    /// <summary>取当前帧内容（转发）。当前源为 null 时回退到不透明占位（返回 null）。</summary>
    public Bitmap? GetFrameContent(Size frameSize) => Current.GetFrameContent(frameSize);

    /// <summary>累计交付的帧数：只数真的视图帧源交付的帧。</summary>
    public long FramesServed => Current is IFrameSourceStats stats ? stats.FramesServed : 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _current.Dispose();
    }
}
