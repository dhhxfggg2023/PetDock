// 事件分发口（D-03c 拆分：HostArbiter.cs 原本 400 行封顶，加记账窗口后 415 行）。
//
// 为什么窗口必须有界：常驻路径每 tick 发一条 `core.clock.tick`（`PetFrameSource.cs:216`，
// 带 JsonObject 载荷），30fps × 8h ≈ 86 万条。无界 `List<PetEvent>` 让 `--soak` 的私有工作集
// 线性上涨（2026-10-22 在 HEAD 上实测：180 秒 12.80MB → 19.09MB，+49.08%），
// M0 的「示例宠物常驻 8 小时」退出标准因此永远过不了。
// 判据面读的窗口 ≤ 14 条，256 条与 `PluginEventSink` 的环形缓冲同一量级，不影响任何断言。
using System;
using System.Collections.Generic;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>
/// 已落地事件与宿主自身补发事件的分发口（M2 的最小面）。
/// 总线路由（docs/16 的 emit / parallel / serial / bail / waterfall 五种分发模式）属 M3/M4；
/// M2 只提供「记账 + 可注入的观察者」两个能力，好让判据（t4/t5）能读到 emit 真的落地了。
/// 事件信封（docs/02 §1.4）不在此解释载荷语义 —— 总线不解释载荷（同条款）。
/// </summary>
public sealed class HostEventDispatch
{
    /// <summary>`Log` 保留的最近事件条数（有界，理由见文件头）。</summary>
    public const int LogCapacity = 256;

    private readonly List<PetEvent> _log = new();
    private readonly Dictionary<string, int> _typeCounts = new(StringComparer.Ordinal);

    /// <summary>最近落地的事件（窗口：最多 <see cref="LogCapacity"/> 条，顺序 = 落地顺序）。</summary>
    public IReadOnlyList<PetEvent> Log => _log;

    /// <summary>
    /// 本对象自构造以来**落地过**的事件总数。`Log` 只保留最近一段，所以「一共发了多少」
    /// 必须由这个计数回答 —— 否则窗口一满，判据就会把「还在发」误读成「不再发了」。
    /// </summary>
    public long TotalPublished { get; private set; }

    /// <summary>观察者：宿主补发事件（如 host.canceled）与插件 emit 都会到这里。</summary>
    public event EventNotifyDelegate? Published;

    /// <summary>记录并发布一条事件。表外事件 fail-closed：不入 Log、留审计行（契约 §2.2）。</summary>
    public void Publish(PetEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!EventCatalog.Contains(evt.Type))
        {
            OutsideCatalog++;
            KernelLog.Write($"event-rejected outside-catalog type={evt.Type}");
            return;
        }

        _log.Add(evt);
        if (_log.Count > LogCapacity)
        {
            _log.RemoveAt(0);
        }

        TotalPublished++;
        _typeCounts.TryGetValue(evt.Type, out int seen);
        _typeCounts[evt.Type] = seen + 1;
        try
        {
            Published?.Invoke(evt);
        }
        catch (Exception error)
        {
            KernelLog.Write($"event-observer-failed {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>被目录守门拒绝的表外事件数（判据面）。</summary>
    public int OutsideCatalog { get; private set; }

    /// <summary>按类型计数（判据面）。计数覆盖**全部**落地事件，不受 Log 窗口截断影响。</summary>
    public int CountOf(string type)
    {
        _typeCounts.TryGetValue(type ?? string.Empty, out int n);
        return n;
    }
}

/// <summary>事件观察者回调（自声明代替系统泛型委托，理由见 HostingTypes.cs 的先例）。</summary>
public delegate void EventNotifyDelegate(PetEvent evt);
