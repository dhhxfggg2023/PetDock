// 拖拽抛掷（2026-10-29）：把宠物甩出去后按重力/落地弹跳回到桌面。
//
// 为什么放在窗口层：速度、屏幕工作区和落地都是窗口自己的事实；行为插件只该决定「想走哪」，
// 不该知道「松手时的瞬时速度」。抛掷期间宿主暂停 brain 的 moveTo（FrameComposition 接线），
// 否则行为层每帧把宠物拉回原来的散步目标，物理就白算了。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.PetRuntime;

namespace PetDock.Core.Compositor;

internal sealed partial class LayeredFrameWindow
{
    private const double ThrowGravity = 2200.0;
    private const double ThrowBounce = 0.45;
    private const double ThrowMinSpeed = 420.0;
    private const double ThrowMaxSpeed = 2600.0;
    private const int ThrowMaxMs = 6000;

    private System.Windows.Forms.Timer? _throwTimer;
    private bool _throwing;
    private double _throwX;
    private double _throwY;
    private double _throwVx;
    private double _throwVy;
    private long _throwTickMs;
    private int _throwElapsedMs;
    private double _dragPrevX;
    private double _dragPrevY;
    private double _dragLastX;
    private double _dragLastY;
    private long _dragPrevMs;
    private long _dragLastMs;
    private bool _hasDragSample;
    private System.Windows.Forms.Timer? _supportTimer;
    private int _restingTop = int.MinValue;

    /// <summary>
    /// 桌面上的可落脚面（别的窗口的矩形）。宿主注入；null = 只有屏幕地板。
    /// 返回的是只读快照，可以每次调用都取一份（拓扑自己带缓存）。
    /// </summary>
    internal Func<IReadOnlyList<Rectangle>>? Platforms { get; set; }

    /// <summary>抛掷开始（宿主暂停 brain 的 moveTo，并显示一句「飞起来」）。</summary>
    internal event PetTapHookSignature? PhysicsStarted;

    /// <summary>抛掷结束（无论落地、被抓住还是超时；宿主恢复 brain）。</summary>
    internal event PetTapHookSignature? PhysicsEnded;

    /// <summary>抛掷真正落地（被抓住不算）；宿主据此显示「站稳了」。</summary>
    internal event PetTapHookSignature? PhysicsLanded;

    /// <summary>是否正在飞（判据与宿主都读它）。</summary>
    internal bool PhysicsActive => _throwing;

    /// <summary>按下时重置采样；上一次抛掷如果还在飞，先收掉（用户抓住了）。</summary>
    internal void BeginDragSample()
    {
        EndResting();
        if (_throwing)
        {
            StopThrow(landed: false);
        }

        Point cursor = Cursor.Position;
        _dragPrevX = _dragLastX = cursor.X;
        _dragPrevY = _dragLastY = cursor.Y;
        _dragPrevMs = _dragLastMs = Environment.TickCount64;
        _hasDragSample = true;
    }

    /// <summary>拖动中采样光标：只保留最近两帧，松手时算瞬时速度。</summary>
    internal void NoteDragSample()
    {
        if (!_hasDragSample)
        {
            return;
        }

        Point cursor = Cursor.Position;
        _dragPrevX = _dragLastX;
        _dragPrevY = _dragLastY;
        _dragPrevMs = _dragLastMs;
        _dragLastX = cursor.X;
        _dragLastY = cursor.Y;
        _dragLastMs = Environment.TickCount64;
    }

    /// <summary>松手时按最近两帧采样算瞬时速度。false = 采样不可用（没动过 / 停手太久）。</summary>
    private bool TryMeasureReleaseVelocity(out double vx, out double vy)
    {
        vx = 0;
        vy = 0;
        if (!_hasDragSample)
        {
            return false;
        }

        long dt = _dragLastMs - _dragPrevMs;
        if (dt <= 0 || dt > 220)
        {
            return false;
        }

        vx = (_dragLastX - _dragPrevX) * 1000.0 / dt;
        vy = (_dragLastY - _dragPrevY) * 1000.0 / dt;
        return true;
    }

    /// <summary>松手：速度够快就以当前窗口位置为起点抛出去。返回 true = 进入物理。</summary>
    internal bool TryStartThrow()
    {
        if (!_hasDragSample || !_dragMoved || !TryMeasureReleaseVelocity(out double vx, out double vy))
        {
            return false;
        }

        if (Math.Sqrt((vx * vx) + (vy * vy)) < ThrowMinSpeed)
        {
            return false;
        }

        (int x, int y) = CurrentPosition;
        StartThrow(x, y, vx, vy, announce: true);
        return true;
    }

    /// <summary>
    /// 松手的总入口（2026-10-30）：先看脚下，再决定「抛出去」还是「落下去」。
    ///
    /// 旧实现的判据只有速度：甩得够快才进物理，慢放就地把窗口留在半空 ——
    /// 用户把宠物拖到屏幕中间松手，它**永远悬在那里**。用户说的「重力效果没考虑是否能下落」
    /// 就是这一条。现在的三分支：
    ///   · 站住了 + 不快  → 什么都不做（站在地板或某个窗口顶上，并把支撑监视接回去）
    ///   · 站住了 + 够快  → 抛出去（旧行为，甩一下会弹）
    ///   · 没站住（不管快慢）→ 进入物理：地心引力说了算，松手就是开始下落
    /// 返回 true = 进入物理。
    /// </summary>
    internal bool TryRelease()
    {
        if (!_hasDragSample || !_dragMoved)
        {
            return false;
        }

        (int x, int y) = CurrentPosition;
        Rectangle area = Screen.FromPoint(new Point(x, y)).WorkingArea;
        var pet = new Rectangle(x, y, Bounds.Width, Bounds.Height);
        bool supported = PhysicsSupport.TryFindSupport(pet, area, Platforms?.Invoke(), out int supportTop);
        bool fast = TryMeasureReleaseVelocity(out double vx, out double vy);

        if (supported && !fast)
        {
            // 站在别的窗口顶上：把「盯着这块面」接回去（BeginDragSample 已经把它摘掉了），
            // 否则那块面一会儿被关掉/挪走时，它会停在半空不再落下。
            if (supportTop != area.Bottom)
            {
                BeginResting(supportTop);
            }

            return false;
        }

        if (!fast)
        {
            vx = 0;
            vy = 0;
        }

        StartThrow(x, y, vx, vy, announce: true);
        return true;
    }

    /// <summary>判据专用：不经过鼠标，直接给一个初速度。</summary>
    internal void StartThrowForCheck(double vx, double vy)
    {
        (int x, int y) = CurrentPosition;
        StartThrow(x, y, vx, vy, announce: true);
    }

    private void StartThrow(int x, int y, double vx, double vy, bool announce)
    {
        EndResting();
        _throwX = x;
        _throwY = y;
        _throwVx = Math.Clamp(vx, -ThrowMaxSpeed, ThrowMaxSpeed);
        _throwVy = Math.Clamp(vy, -ThrowMaxSpeed, ThrowMaxSpeed);
        _throwTickMs = Environment.TickCount64;
        _throwElapsedMs = 0;
        _throwing = true;
        _throwTimer ??= new System.Windows.Forms.Timer { Interval = 16 };
        _throwTimer.Tick -= OnThrowTick;
        _throwTimer.Tick += OnThrowTick;
        _throwTimer.Start();

        if (announce)
        {
            try
            {
                PhysicsStarted?.Invoke();
            }
            catch (Exception error)
            {
                KernelLog.Write($"physics-started-failed {error.GetType().Name}: {error.Message}");
            }
        }
    }

    private void OnThrowTick(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        double dt = Math.Clamp((now - _throwTickMs) / 1000.0, 0.0, 0.05);
        _throwTickMs = now;
        _throwElapsedMs += (int)(dt * 1000);

        double previousY = _throwY;
        _throwVy += ThrowGravity * dt;
        _throwX += _throwVx * dt;
        _throwY += _throwVy * dt;

        int width = Bounds.Width;
        int height = Bounds.Height;
        Rectangle area = Screen.FromPoint(new Point((int)_throwX, (int)_throwY)).WorkingArea;
        if (_throwX < area.Left)
        {
            _throwX = area.Left;
            _throwVx = Math.Abs(_throwVx) * ThrowBounce;
        }
        else if (_throwX + width > area.Right)
        {
            _throwX = Math.Max(area.Left, area.Right - width);
            _throwVx = -Math.Abs(_throwVx) * ThrowBounce;
        }

        if (_throwY < area.Top)
        {
            _throwY = area.Top;
            _throwVy = Math.Abs(_throwVy) * ThrowBounce;
        }

        bool hitSupport = false;
        int supportTop = area.Bottom;

        // 先看别的窗口的顶边：只在下落时认，且必须「这次跨过」它。
        if (_throwVy > 0 && Platforms?.Invoke() is { Count: > 0 } surfaces)
        {
            var pet = new Rectangle((int)Math.Round(_throwX), (int)Math.Round(_throwY), width, height);
            int previousBottom = (int)Math.Round(previousY) + height;
            int newBottom = (int)Math.Round(_throwY) + height;
            if (PhysicsSupport.TryFind(pet, previousBottom, newBottom, area, surfaces, out int top))
            {
                _throwY = top - height;
                _throwVy = -Math.Abs(_throwVy) * ThrowBounce;
                _throwVx *= 0.82;
                hitSupport = true;
                supportTop = top;
            }
        }

        if (!hitSupport && _throwY + height >= area.Bottom)
        {
            _throwY = Math.Max(area.Top, area.Bottom - height);
            _throwVy = -Math.Abs(_throwVy) * ThrowBounce;
            _throwVx *= 0.82;
            hitSupport = true;
            supportTop = area.Bottom;
        }

        SetWindowPosition((int)Math.Round(_throwX), (int)Math.Round(_throwY));

        if (hitSupport && Math.Abs(_throwVy) < 70 && Math.Abs(_throwVx) < 40)
        {
            // 落在别的窗口顶上：开始盯这个面（走出边缘或窗口没了就继续掉）。
            if (supportTop != area.Bottom)
            {
                BeginResting(supportTop);
            }

            StopThrow(landed: true);
            return;
        }

        if (_throwElapsedMs >= ThrowMaxMs)
        {
            StopThrow(landed: true);
        }
    }

    /// <summary>开始盯「站在别的窗口顶上」这件事（每 40ms 一次，比物理帧慢，误差肉眼看不出来）。</summary>
    private void BeginResting(int top)
    {
        _restingTop = top;
        _supportTimer ??= new System.Windows.Forms.Timer { Interval = 40 };
        _supportTimer.Tick -= OnSupportTick;
        _supportTimer.Tick += OnSupportTick;
        _supportTimer.Start();
    }

    /// <summary>不再站着了（被抓住 / 换位置 / 已经掉下去）。</summary>
    private void EndResting()
    {
        _restingTop = int.MinValue;
        _supportTimer?.Stop();
    }

    /// <summary>
    /// 支撑监视：走出窗口边缘、窗口被关掉或被挪走，都要重新落下去 ——
    /// 否则宠物会停在半空，看起来像卡住了。
    /// </summary>
    private void OnSupportTick(object? sender, EventArgs e)
    {
        if (_restingTop == int.MinValue || IsDisposed)
        {
            EndResting();
            return;
        }

        Rectangle rect = Bounds;
        if (Math.Abs(rect.Bottom - _restingTop) > 2)
        {
            EndResting();
            return;
        }

        IReadOnlyList<Rectangle>? surfaces = Platforms?.Invoke();
        if (surfaces is null)
        {
            // 拓扑整个被关掉（自检 / 没有宿主）：不再监视，别让宠物莫名其妙开始自由落体。
            EndResting();
            return;
        }

        // 空列表 ≠ 关闭：桌面上一块可落脚的面都没有了（窗口全关），必须掉下去。
        if (surfaces.Count > 0 && PhysicsSupport.HasSupport(rect, _restingTop, surfaces))
        {
            return;
        }

        EndResting();
        StartThrow(rect.Left, rect.Top, 0, 0, announce: true);
    }

    private void StopThrow(bool landed)
    {
        if (!_throwing)
        {
            return;
        }

        _throwing = false;
        _throwTimer?.Stop();

        // 落地 = 一次宿主自主位移（位移可达半屏）：通知行为层重锚，否则它接下来会朝
        // 起飞前那个绝对 X 走过去 —— 用户看到的是「甩出去之后它自己慢慢走回原处」。
        if (landed && !IsDisposed)
        {
            NotifyRelocated();
            (int x, int y) = CurrentPosition;
            RaiseWindowEvent(EventCatalog.WindowMoved, new System.Text.Json.Nodes.JsonObject
            {
                ["windowId"] = $"hwnd-{Handle.ToInt64():X}",
                ["position"] = new System.Text.Json.Nodes.JsonArray(x, y),
            });
        }

        if (!IsDisposed)
        {
            try
            {
                PhysicsEnded?.Invoke();
                if (landed)
                {
                    PhysicsLanded?.Invoke();
                }
            }
            catch (Exception error)
            {
                KernelLog.Write($"physics-ended-failed {error.GetType().Name}: {error.Message}");
            }
        }
    }

    private void DisposeThrow()
    {
        StopThrow(landed: false);
        EndResting();
        _supportTimer?.Dispose();
        _supportTimer = null;
        _throwTimer?.Dispose();
        _throwTimer = null;
    }
}
