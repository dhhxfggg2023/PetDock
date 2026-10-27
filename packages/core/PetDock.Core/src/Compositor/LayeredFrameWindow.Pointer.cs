// RFC-35 §3.1：把窗口的鼠标消息交给交互层（原规格 11 §5），而不是只用来拖窗。
//
// 为什么单独一个 partial：LayeredFrameWindow.cs 贴着 400 行上限（D-03c），
// 而「谁先拿到按下」是本片唯一要新增的裁决点。三个鼠标消息处理函数随本片一起搬进来
// （它们本来就在主文件末尾），一个文件里「消息进来 → 问交互层 → 没人接才拖」是完整的。
//
// 三条纪律（原规格 11:188-190）：
//   1. 这一层不做「点击」，只做「会话」—— down 之后窗口不再自己拖，除非没人接；
//   2. 命中判定（hitmap / alpha → surface）在 PetInstance 里，本文件不重复判；
//   3. 会话期间要抓住鼠标，否则指针一移出窗口就再也收不到 up。
using System;
using System.Windows.Forms;
using PetDock.Core.PetRuntime;

namespace PetDock.Core.Compositor;

internal sealed partial class LayeredFrameWindow
{
    /// <summary>按下询问：返回 true = 交互层接手了这次按下（窗口不拖）。（宿主侧适配 PetInstance.PointerDown）</summary>
    internal PointerDownHookSignature? PointerDownHook;

    /// <summary>会话内移动（宿主侧适配 PetInstance.PointerMove）。</summary>
    internal PointerMoveHookSignature? PointerMoveHook;

    /// <summary>抬起（宿主侧适配 PetInstance.PointerUp）。</summary>
    internal PointerMoveHookSignature? PointerUpHook;

    /// <summary>取消（宿主侧适配 PetInstance.CancelSession）：失焦、窗口销毁等。</summary>
    internal PointerCancelHookSignature? PointerCancelHook;

    /// <summary>本窗是否正在拖（判据用它区分「按下去拖窗」与「按下去进会话」）。</summary>
    internal bool IsDraggingNow => _dragging;

    /// <summary>
    /// 双击（2026-10-27，原规格 18 §1）：宠物上连点两下 → 打开主控面板。这是右键菜单之外的第二条
    /// 入口 —— 托盘图标在部分机器/布局上不进外壳（结论见 原规格 98 §3.1 第 1 条），而「界面上什么也点不开」
    /// 比「菜单没接上」更致命。双击不是新语义：宿主只把它接到与托盘「主控面板」同一个动作上。
    /// </summary>
    internal event PetTapHookSignature? DoubleClicked;

    /// <summary>上一次「点了一下但没拖动」的时刻与光标位置（双击判定用；系统阈值见 NoteTap）。</summary>
    private DateTime _lastTapAt;
    private Point _lastTapPos;
    private bool _hasLastTap;

    /// <summary>按下时的窗口位置：抬起来还在这里 = 这是一次「点」，不是一次「拖」。</summary>
    private (int X, int Y) _downWindowPos;

    /// <summary>拖过的距离上限（像素）：超过它就算拖窗，不计入双击。</summary>
    private const int TapSlopPixels = 3;

    /// <summary>
    /// 一次「点」的记账与双击判定。阈值取系统设置（`SystemInformation.DoubleClickTime` /
    /// `DoubleClickSize`），不自造常量：用户把双击速度调慢时这里必须跟着慢。
    /// 两下都必须是「点」而不是「拖」—— 拖动宠物是最常用的手势，不能每次落地都算一下双击。
    /// </summary>
    private void NoteTap()
    {
        DateTime now = DateTime.UtcNow;
        Point cursor = Cursor.Position;
        bool second = _hasLastTap
            && (now - _lastTapAt).TotalMilliseconds <= SystemInformation.DoubleClickTime
            && Math.Abs(cursor.X - _lastTapPos.X) <= SystemInformation.DoubleClickSize.Width
            && Math.Abs(cursor.Y - _lastTapPos.Y) <= SystemInformation.DoubleClickSize.Height;

        if (!second)
        {
            _hasLastTap = true;
            _lastTapAt = now;
            _lastTapPos = cursor;
            return;
        }

        _hasLastTap = false;
        try
        {
            DoubleClicked?.Invoke();
        }
        catch (Exception error)
        {
            // 与右键同一条纪律：处理函数炸了不许把消息循环打穿。
            KernelLog.Write($"double-click-failed {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// 判据专用（先例：`RaiseContextRequestedForCheck`）：自检没有真鼠标，而生产路径（NoteTap）
    /// 除了阈值判定只剩「抛事件」，所以让判据从同一个事件进。
    /// </summary>
    internal void RaiseDoubleClickedForCheck() => DoubleClicked?.Invoke();

    /// <summary>会话期间的鼠标捕获是否由本窗持有（会话结束必须还回去）。</summary>
    private bool _pointerCapture;

    /// <summary>问交互层要不要这次按下；它抛异常 = 没人接（窗口照旧可拖，不许把消息循环打穿）。</summary>
    private bool TryHandOverPointerDown(int x, int y, PointerButton button)
    {
        if (PointerDownHook is null)
        {
            return false;
        }

        try
        {
            if (!PointerDownHook(x, y, button))
            {
                return false;
            }
        }
        catch (Exception error)
        {
            KernelLog.Write($"pointer-down-hook-failed {error.GetType().Name}: {error.Message}");
            return false;
        }

        // 会话期间抓鼠标：不抓的话，指针一移出窗口就收不到 up，会话会一直挂到 10s 预算。
        _pointerCapture = true;
        Capture = true;
        return true;
    }

    private void RaisePointerMove(int x, int y)
    {
        try
        {
            PointerMoveHook?.Invoke(x, y);
        }
        catch (Exception error)
        {
            KernelLog.Write($"pointer-move-hook-failed {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>抬起：先给会话（它可能还持有捕获），再决定要不要还捕获。</summary>
    private void RaisePointerUp(int x, int y)
    {
        try
        {
            PointerUpHook?.Invoke(x, y);
        }
        catch (Exception error)
        {
            KernelLog.Write($"pointer-up-hook-failed {error.GetType().Name}: {error.Message}");
        }

        ReleasePointerCapture();
    }

    /// <summary>宿主侧取消（原规格 11:183）：失焦 / 窗口销毁 / 捕获被系统拿走。</summary>
    internal void RaisePointerCancel(string why)
    {
        if (PointerCancelHook is null)
        {
            return;
        }

        try
        {
            PointerCancelHook(why);
        }
        catch (Exception error)
        {
            KernelLog.Write($"pointer-cancel-hook-failed {error.GetType().Name}: {error.Message}");
        }

        ReleasePointerCapture();
    }

    private void ReleasePointerCapture()
    {
        if (!_pointerCapture)
        {
            return;
        }

        _pointerCapture = false;
        Capture = false;
    }

    /// <summary>按下：交互层优先，没人接才落回拖窗（M1 以来的行为）。</summary>
    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (TryHandOverPointerDown(e.X, e.Y, PointerButton.Left))
        {
            return;
        }

        _dragging = true;
        Capture = true;
        _dragOffset = new Point(e.X, e.Y);
        _downWindowPos = CurrentPosition;
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        // 会话内的移动先给交互层（没有会话时宿主侧是无害 no-op）。
        RaisePointerMove(e.X, e.Y);

        if (!_dragging)
        {
            return;
        }

        Point cursor = Cursor.Position;
        SetWindowPosition(cursor.X - _dragOffset.X, cursor.Y - _dragOffset.Y);
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        // 会话结束时它会自己还捕获；这里先给交互层，再处理拖窗。
        RaisePointerUp(e.X, e.Y);

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Capture = false;

        // 拖拽落地 = 真实移窗完成：这里就是 core.window.moved 的原生生产点（契约 §2）。
        (int X, int Y) pos = CurrentPosition;
        RaiseWindowEvent(EventCatalog.WindowMoved, new System.Text.Json.Nodes.JsonObject
        {
            ["windowId"] = $"hwnd-{Handle.ToInt64():X}",
            ["position"] = new System.Text.Json.Nodes.JsonArray(pos.X, pos.Y),
        });

        // 双击判定只认「点」：窗口没被拖走才算（拖动落地不参与，见 NoteTap 的注释）。
        bool tapped = Math.Abs(pos.X - _downWindowPos.X) <= TapSlopPixels
            && Math.Abs(pos.Y - _downWindowPos.Y) <= TapSlopPixels;
        if (tapped)
        {
            NoteTap();
        }
        else
        {
            _hasLastTap = false;
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        // 失焦 = 宿主取消（原规格 11:183 的 ALT 情形）。拖窗不需要取消（拖窗由鼠标捕获保证 up 到达）。
        RaisePointerCancel("deactivate");
        base.OnDeactivate(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        RaisePointerCancel("closing");
        base.OnFormClosing(e);
    }
}

/// <summary>按下钩子的形状（自声明代替系统泛型委托，先例见 HostingTypes.cs）。</summary>
internal delegate bool PointerDownHookSignature(int x, int y, PointerButton button);

/// <summary>移动/抬起钩子的形状。</summary>
internal delegate void PointerMoveHookSignature(int x, int y);

/// <summary>取消钩子的形状（宿主侧按 why 记账：deactivate / closing）。</summary>
internal delegate void PointerCancelHookSignature(string why);

/// <summary>双击（2026-10-27）的形状。自声明而不是用系统泛型委托：D-03b 词表把那个泛型名列为概念黑名单。</summary>
internal delegate void PetTapHookSignature();
