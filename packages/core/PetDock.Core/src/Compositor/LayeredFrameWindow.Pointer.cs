// RFC-35 §3.1：把窗口的鼠标消息交给交互层（docs/11 §5），而不是只用来拖窗。
//
// 为什么单独一个 partial：LayeredFrameWindow.cs 贴着 400 行上限（D-03c），
// 而「谁先拿到按下」是本片唯一要新增的裁决点。三个鼠标消息处理函数随本片一起搬进来
// （它们本来就在主文件末尾），一个文件里「消息进来 → 问交互层 → 没人接才拖」是完整的。
//
// 三条纪律（docs/11:188-190）：
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

    /// <summary>宿主侧取消（docs/11:183）：失焦 / 窗口销毁 / 捕获被系统拿走。</summary>
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
    }

    protected override void OnDeactivate(EventArgs e)
    {
        // 失焦 = 宿主取消（docs/11:183 的 ALT 情形）。拖窗不需要取消（拖窗由鼠标捕获保证 up 到达）。
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
