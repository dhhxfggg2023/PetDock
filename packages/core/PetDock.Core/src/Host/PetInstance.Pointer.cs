// PetInstance 的指针半边（partial 的第三块）：docs/11 §5 的交互层仲裁与 §9:260 的会话超时。
// 两条语义（docs/11:189-190 逐字）：
//   1. 多个 surface 叠加时 priority 高的先收到；同优先级按注册顺序（先注册先问）；
//   2. 命中测试总是先过 hitmap / alpha，再过 surface —— 任何一个 surface 都不能让已经穿透的
//      区域变成可点。所以本文件的 PointerDown **第一件事**是调 HitsAt，不是问 surface。
// 以及 docs/11:260：一次交互会话持续 >10s 无 up / cancel → 宿主强制 cancel + 发 host.canceled。
using System;
using System.Collections.Generic;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>一只宠物实例的指针面（docs/11 §5）。</summary>
public sealed partial class PetInstance
{
    /// <summary>docs/11:260 会话无 up / cancel 的强制取消预算（毫秒）。</summary>
    public const int SessionCancelBudgetMs = 10000;

    /// <summary>宿主强制取消时发的事件类型（docs/11:260 逐字）。</summary>
    public const string HostCanceledEventName = "host.canceled";

    private IPointerSession? _session;
    private IPointerSurface? _sessionSurface;
    private long _sessionStartedMs;
    private IReadOnlyList<IPointerSurface>? _ordered;

    /// <summary>累计开始的会话数 / 正常结束数 / 强制取消数。</summary>
    public int SessionsStarted { get; private set; }

    public int SessionsEnded { get; private set; }

    public int SessionsForcedCancel { get; private set; }

    /// <summary>当前会话（null = 没有会话在跑）。</summary>
    public IPointerSession? ActiveSession => _session;

    /// <summary>当前会话由哪个 surface 开启（诊断用）。</summary>
    public string? ActiveSurfaceId => _sessionSurface?.Id;

    /// <summary>
    /// 按 docs/11:189 排好序的 surface 列表：priority 降序，同优先级保持注册顺序。
    /// 排序用插入排序（稳定）——同优先级的相对次序是 ABI 语义，不稳定排序会静默改变它。
    /// </summary>
    public IReadOnlyList<IPointerSurface> SurfacesByPriority
    {
        get
        {
            if (_ordered is not null)
            {
                return _ordered;
            }

            var list = new List<IPointerSurface>(Surfaces);
            for (int i = 1; i < list.Count; i++)
            {
                IPointerSurface key = list[i];
                int j = i - 1;
                while (j >= 0 && list[j].Priority < key.Priority)
                {
                    list[j + 1] = list[j];
                    j--;
                }

                list[j + 1] = key;
            }

            _ordered = list;
            return list;
        }
    }

    /// <summary>
    /// 一次按下（docs/11 §5:176 的 onPointerDown）。返回是否有人接。
    /// 顺序：穿透判定（§5:190）→ 按优先级逐个问 surface（HitTest 不过就跳过）→
    /// 返回 null 的实现视为拒绝，转给下一个。
    /// surface 抛异常 = 拒绝并记日志（不许把宿主循环打穿）。
    /// </summary>
    public bool PointerDown(int pixelX, int pixelY, PointerButton button, int nowMs = 0)
    {
        if (_session is not null)
        {
            return false; // 已经有会话在跑：新的按下不抢（宿主在 up / cancel 之后才开新的）
        }

        if (!HitsAt(pixelX, pixelY))
        {
            return false; // docs/11:190 穿透区不许被任何 surface 变成可点
        }

        PetPoint local = ToLocal(pixelX, pixelY);
        foreach (IPointerSurface surface in SurfacesByPriority)
        {
            try
            {
                if (!surface.HitTest(local))
                {
                    continue;
                }

                IPointerSession? session = surface.OnPointerDown(local, button);
                if (session is null)
                {
                    continue; // docs/11:176 返回 null = 拒绝，转给下一个
                }

                _session = session;
                _sessionSurface = surface;
                _sessionStartedMs = nowMs;
                SessionsStarted++;
                return true;
            }
            catch (Exception error)
            {
                KernelLog.Write($"pointer-surface-failed id={surface.Id} {error.GetType().Name}: {error.Message}");
            }
        }

        return false;
    }

    /// <summary>会话内指针移动。没有会话时为无害 no-op。</summary>
    public void PointerMove(int pixelX, int pixelY)
    {
        if (_session is null)
        {
            return;
        }

        PetPoint local = ToLocal(pixelX, pixelY);
        Guarded(() => _session!.OnPointerMove(local), "pointer-move");
    }

    /// <summary>指针抬起：会话结束（无论实现方是否抛）。</summary>
    public void PointerUp(int pixelX, int pixelY)
    {
        if (_session is null)
        {
            return;
        }

        PetPoint local = ToLocal(pixelX, pixelY);
        Guarded(() => _session!.OnPointerUp(local), "pointer-up");
        CloseSession();
    }

    /// <summary>宿主侧取消（ALT、关机、锁屏；docs/11:183）。</summary>
    public void CancelSession(string why)
    {
        if (_session is null)
        {
            return;
        }

        Guarded(() => _session!.OnCancel(), "pointer-cancel");
        KernelLog.Write($"pointer-session-cancelled instance={Id} why={why}");
        CloseSession();
    }

    /// <summary>
    /// docs/11:260：会话持续超过 10s 没有 up / cancel → 宿主强制 cancel 并发 host.canceled。
    /// 帧循环每帧调一次；返回 true 表示本帧发生了一次强制取消。
    /// </summary>
    public bool EnforceSessionBudget(int nowMs)
    {
        if (_session is null)
        {
            return false;
        }

        if (nowMs - _sessionStartedMs <= SessionCancelBudgetMs)
        {
            return false;
        }

        SessionsForcedCancel++;
        CancelSession($"session-budget {SessionCancelBudgetMs}ms exceeded");
        Arbiter.NoteHostEvent(HostCanceledEventName);
        KernelLog.Write($"pointer-session-forced-cancel instance={Id} at={nowMs}");
        return true;
    }

    private void CloseSession()
    {
        _session = null;
        _sessionSurface = null;
        SessionsEnded++;
    }

    /// <summary>物理像素 → 宠物局部坐标（docs/11 §2 Point：原点在窗口左上，逻辑像素）。</summary>
    private PetPoint ToLocal(int pixelX, int pixelY)
    {
        double scale = Target is null || Target.Scale <= 0 ? 1.0 : Target.Scale;
        return new PetPoint(pixelX / scale, pixelY / scale);
    }

    private static void Guarded(GuardedStep body, string label)
    {
        try
        {
            body();
        }
        catch (Exception error)
        {
            KernelLog.Write($"{label}-failed {error.GetType().Name}: {error.Message}");
        }
    }
}

/// <summary>一次受保护的指针回调（自声明代替系统泛型委托，理由见 HostingTypes.cs 的先例）。</summary>
public delegate void GuardedStep();
