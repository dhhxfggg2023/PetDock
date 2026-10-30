// 实现 原规格 12 §2.1「只做这一种窗」与 §5「拖动、焦点」：WS_EX_LAYERED 透明分层窗宿主
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Core.Compositor;

/// <summary>
/// 分层窗宿主：WS_EX_LAYERED | TOPMOST | TOOLWINDOW | NOACTIVATE。
/// 逐像素命中穿透完全交给 OS 读 alpha —— 本类既不用 SetWindowRgn / SetLayeredWindowAttributes /
/// WS_EX_TRANSPARENT，也不覆写 WndProc 的 WM_NCHITTEST（原规格 12 §2.1、§2.3）。
/// 内容由 IFrameContentSource 提供（像素 + 尺寸，原规格 40 D-03b：内核零上层概念）；
/// 未注入内容源时退回不透明纯色占位帧。
/// </summary>
internal sealed partial class LayeredFrameWindow : Form
{
    private const int ScreenMargin = 32;

    private readonly IFrameContentSource _content;
    private readonly bool _ownsContent;
    private DibFrame? _frame;
    private bool _dragging;
    private Point _dragOffset;

    /// <summary>窗口事件出口（core.window.*）：宿主订阅；Compositor 不能反向引用 PetDock.Hosting。</summary>
    public event WindowEventDelegate? WindowEvent;

    /// <summary>在宠物身上按右键：宿主在这里弹菜单（原规格 18 §1，2026-10-26 用户要求）。
    /// 载荷是屏幕坐标 —— 菜单要落在鼠标处，而不是窗口原点（窗口可能被拖到负坐标）。
    /// 手势归属：右键从此归宿主（插件层原有的右键手势因此让位，用户 2026-10-26 明确同意）。</summary>
    public event ContextRequestDelegate? ContextRequested;

    /// <summary>
    /// 右键菜单回调。用具名委托而不是泛型委托：D-03b 词表把那个泛型名列为概念黑名单。
    /// **载荷带 source 窗口**（2026-10-30）：宿主必须知道用户右键的是桌上**哪一只** ——
    /// 只给一个屏幕坐标的话，桌上摆了三只时宿主只能弹同一份「全部宠物」的菜单，
    /// 用户点「移动/暂停/移除」时根本不知道动的是谁（这正是 2026-10-30 用户报的「右键菜单没用」）。
    /// </summary>
    public delegate void ContextRequestDelegate(Point screen, LayeredFrameWindow source);

    /// <summary>有没有人接右键（判据用：null 说明菜单没接上，右键会变成没反应的死键）。</summary>
    public bool ContextMenuWired => ContextRequested is not null;

    /// <summary>
    /// 宿主自主位移（2026-10-30）：**不是**行为层 moveTo 落地的那种移动 —— 拖拽落地、
    /// 抛掷落地、居中、归位、天体模式收手都算。区分这两者的理由见 IRelocationAware 的注释：
    /// 行为层只看得见矩形，分不清「我在走向目标」与「我被搬走了」，而它的目标是绝对坐标。
    /// </summary>
    internal event PetTapHookSignature? Relocated;

    /// <summary>有没有人接位移通知（判据用：false = 搬家之后行为层不会被通知，会自己走回去）。</summary>
    internal bool RelocationWired => Relocated is not null;

    /// <summary>报告一次「我把它挪了」。处理函数抛异常不许打穿调用方（与右键同一条纪律）。</summary>
    internal void NotifyRelocated()
    {
        try
        {
            Relocated?.Invoke();
        }
        catch (Exception error)
        {
            KernelLog.Write($"relocated-failed {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>有没有人接双击（判据用：null 说明双击是死键；事件在 Pointer.cs 里声明）。</summary>
    public bool DoubleClickWired => DoubleClicked is not null;

    /// <summary>注入内容源构造（生产与自检路径：内容由上层给，内核只收像素+尺寸）。</summary>
    public LayeredFrameWindow(IFrameContentSource content, bool ownsContent = true)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _ownsContent = ownsContent;
        BuildWindow();
    }

    /// <summary>无内容源构造（内核兜底路径：不透明纯色占位帧）。</summary>
    public LayeredFrameWindow()
        : this(SolidFrameContent.KernelFallback())
    {
    }

    private void BuildWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        // 不设 MinimumSize/MaximumSize：它们的单位是物理像素，而逻辑尺寸是逻辑像素，
        // 128 的上下限会把 150% DPI 下 192x192 的窗口卡住（实测落地 192x128，方形窗口被压扁）。
        // 窗口尺寸的唯一来源是 ApplyPhysicalSize()。
        Size = _logicalSize;
        DoubleBuffered = false;

        var clock = System.Diagnostics.Stopwatch.StartNew();

        // 顺序有约束：DIB 先建好，再摸 Handle。
        // 取 Handle 会就地派发 WM_CREATE → OnHandleCreated。真正的顺序约束在 OnHandleCreated 里
        // 用 (_frame is null || _frame.Hwnd != IntPtr.Zero) 守住：_frame 还没建好时它只设尺寸、不出帧，
        // 出帧由构造完成后的一次 PresentFirstFrame() 补上（见 OnHandleCreated 的注释）。
        // 构造期先建一张逻辑尺寸的占位 DIB（此刻窗口还不存在，量不到它的 DPI），
        // 真正尺寸在 OnHandleCreated 里按窗口实测矩形校正（见 ApplyFrameToWindowSize）。
        // 这里绝不能拿「内容尺寸」当 DIB 尺寸：帧缓冲必须与窗口物理尺寸严格一致，
        // 不一致时 UpdateLayeredWindow 会从源 DC 之外取图，整帧一个像素都不显示（实测）。
        _frame = DibFrame.Create(_logicalSize.Width, _logicalSize.Height);
        long tFrame = clock.ElapsedMilliseconds;
        _frame.Hwnd = Handle;
        long tHandle = clock.ElapsedMilliseconds;

        Startup = new StartupTimings(tFrame, tHandle - tFrame);

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
    }

    /// <summary>窗口句柄（供自检读取 exstyle 与做命中探针）。</summary>
    public IntPtr WindowHandle => Handle;

    /// <summary>
    /// 真移窗到物理像素 (x,y)：SWP_NOSIZE|NOZORDER|NOACTIVATE。moveTo 指令的生产路径。
    /// 落点先夹进目标屏的工作区：行为层只看得见自己的窗口矩形，不知道屏幕边界；
    /// 不夹的话「走到屏幕外」会让宠物整个消失，用户只能等它自己走回来（或者永远回不来）。
    /// </summary>
    public bool MoveTo(int x, int y)
    {
        Rectangle area = Screen.FromPoint(new Point(x, y)).WorkingArea;
        Rectangle current = Win32.ScreenRect(Handle);
        int maxX = area.Right - current.Width;
        int maxY = area.Bottom - current.Height;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, maxX));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, maxY));
        return Win32.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    /// <summary>窗口当前物理位置（供 core.window.moved 的 position 与 Arbiter 校验）。</summary>
    public (int X, int Y) CurrentPosition
    {
        get
        {
            var rect = Win32.ScreenRect(Handle);
            return (rect.Left, rect.Top);
        }    }

    /// <summary>
    /// 构造期的分步耗时（毫秒），供自检输出。这是唯一能量出「启动代价花在哪一步」的证据：
    /// HandleLittle 那一步含 CreateWindowEx + WM_CREATE + OnHandleCreated + 首次 Present。
    /// </summary>
    public StartupTimings Startup { get; private set; }

    /// <summary>
    /// 最近一帧的外表内容是否与上一次是同一个位图对象（RFC-21 §3.5）。
    /// 与 <see cref="HasContentSource"/> 同一个姿势：**问内容源，不问类型**。
    /// 内容源可能是代理（RFC-15 §3.3），代理必须问里面那一份；不实现该自述面的内容源恒 false。
    /// </summary>
    public bool LastFrameReused => (_content as IFrameReuseInspector)?.LastFrameReused ?? false;

    /// <summary>本帧内容是否来自注入的内容源（false = 内核纯色兜底）。</summary>
    public bool HasContentSource => _content is IFrameContentInspector inspector
        ? inspector.DeliversRealContent
        : _content is not SolidFrameContent;

    /// <summary>
    /// 内容源**实际交付**给合成器的帧数（每次 GetFrameContent 返回非 null 就加一）。
    /// 为什么要有它：`HasContentSource` 只证明「装上了一个非兜底的内容源」，
    /// 「装上了但一帧都取不出来」在这条判据下是隐形的（M0 的 source=false 打 SELF-OK 就是这一类）。
    /// 计数放在窗口里而不是内容源上，是因为内容源有两条实现路径
    /// （ABI 视图适配器 / M0 的工厂接口），而「像素真的进了帧缓冲」只有这一处观察点。
    /// </summary>
    public long ContentFramesServed { get; private set; }

    /// <summary>帧缓冲是否就绪。</summary>
    public bool HasFrame => _frame is not null;

    /// <summary>读当前帧缓冲内某点的 BGRA（自检诊断用）；无帧缓冲时返回 null。</summary>
    public (byte B, byte G, byte R, byte A)? SampleFramePixel(int x, int y)
    {
        return _frame?.SamplePixel(x, y);
    }

    /// <summary>首帧绘制：内容位图 → 预乘 DIB → UpdateLayeredWindow；无内容时退回纯色兜底。</summary>
    public void PresentFirstFrame()
    {
        RenderFrame();
    }

    /// <summary>绘制并提交一帧：内容位图（或纯色兜底）→ 预乘 DIB → UpdateLayeredWindow。
    /// 首帧与帧泵的每一帧都走这条路；返回 false = 帧缓冲不可用。**语义一字不变**：合成 + 提交两步依次做。</summary>
    public bool RenderFrame()
    {
        return ComposeFrame() && PresentFrame();
    }

    /// <summary>合成：对齐 DIB 尺寸 → 向内容源要一帧 → 写进 DIB，**不提交**（不调 UpdateLayeredWindow）。
    /// 为什么必须与提交分开，见 RFC-21 §8 第 9 条。</summary>
    public bool ComposeFrame()
    {
        if (_frame is null)
        {
            return false;
        }

        // 每帧把「DIB 尺寸 = 窗口实测尺寸」这条不变式重新对齐一次：一次 GetWindowRect 的代价，
        // 换掉的是「窗口尺寸在别处被改过 → 整帧静默不可见」这类最难查的故障。
        ApplyFrameToWindowSize();
        DibFrame frame = _frame;

        Bitmap? content = _content.GetFrameContent(new Size(frame.Width, frame.Height));
        if (content is not null)
        {
            // DrawBitmap 要求位图与 DIB 尺寸严格一致；一致性由 IFrameContentSource 的契约保证。
            frame.DrawBitmap(content);
            if (HasContentSource)
            {
                // 只数**真实内容源**交付的帧：兜底源也会返回非 null 的位图，算进来会让
                // 「一帧都没出」看起来像「出了 63 帧」（M0 的 source=false 打 SELF-OK）。
                ContentFramesServed++;
            }
        }
        else
        {
            frame.FillOpaque(Color.FromArgb(0xFF, 0x3A, 0x6E, 0xC0));
        }

        return true;
    }

    /// <summary>提交：把当前 DIB 推上屏（UpdateLayeredWindow），不重新合成。
    /// 一次上屏是整窗合成；「只合成」那半只是 128×128×4 写入（30fps 下约 2MB/s）。</summary>
    public bool PresentFrame()
    {
        if (_frame is null)
        {
            return false;
        }

        Rectangle screenRect = Win32.ScreenRect(Handle);
        _frame.Present(new Point(screenRect.Left, screenRect.Top), 255);
        return true;
    }

    /// <summary>把窗口移到主屏工作区右下角，留 32px 边距（物理像素）。</summary>
    public void MoveToBottomRight()
    {
        Rectangle area = Screen.PrimaryScreen!.WorkingArea;
        // 用实测尺寸而不是请求尺寸算边距：窗口被压扁时，按请求尺寸算出来的边距就是错的
        // （实测过 192x128 的窗口配 192 的边距，底边留出 96px 而不是 32px）。
        Size actual = WindowSize;
        SetWindowPosition(area.Right - actual.Width - ScreenMargin,
            area.Bottom - actual.Height - ScreenMargin);
        NotifyRelocated();
    }

    /// <summary>把窗口移到主屏中心（自检稳定取点用）。</summary>
    public void MoveToCenter()
    {
        Rectangle area = Screen.PrimaryScreen!.Bounds;
        Size actual = WindowSize;
        SetWindowPosition(area.X + (area.Width - actual.Width) / 2,
            area.Y + (area.Height - actual.Height) / 2);

        // 「移动」这个命令本身要能留住：不通知的话行为层会拿搬家前的绝对目标把它走回去，
        // 用户看到的是「点了移动，十几秒后它又自己走回去了」。
        NotifyRelocated();
    }

    /// <summary>把窗口移动到屏幕坐标（物理像素）。</summary>
    public void SetWindowPosition(int screenX, int screenY)
    {
        Win32.SetWindowPos(Handle, IntPtr.Zero, screenX, screenY, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 把自己重新插到 topmost 层最前面（不改位置和尺寸、不抢焦点）。
    /// 分两步做，因为一步实测不够：先确保 WS_EX_TOPMOST 样式位在（SetWindowPos 可能把它清掉），
    /// 再用 HWND_TOPMOST 把 Z 序提到这一层最前——TOPMOST 是一整个层，层内先后同样决定 WindowFromPoint 的答案。
    /// </summary>
    public void BringToFrontOfTopmost()
    {
        long before = Win32.GetWindowLongPtr(Handle, Win32.GWL_EXSTYLE).ToInt64();
        if ((before & Win32.WS_EX_TOPMOST) == 0)
        {
            Win32.SetWindowLongPtr(Handle, Win32.GWL_EXSTYLE, new IntPtr(before | Win32.WS_EX_TOPMOST));
        }

        bool moved = Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        long after = Win32.GetWindowLongPtr(Handle, Win32.GWL_EXSTYLE).ToInt64();
        // 事后自检：这是「把窗口提到最前」这件事本身有没有生效的唯一证据，
        // 一次实测里 exstyle 里的 TOPMOST 位是会被系统抹掉的，所以不能假设它一直在。
        KernelLog.Write($"topmost before=0x{before:X} after=0x{after:X} setpos-ok={moved}"
            + $" win32err={Marshal.GetLastWin32Error()}");
    }

    /// <summary>当前 DPI（Win10 1607+，失败时回退 96）。</summary>
    public uint CurrentDpi => DpiContext.DpiForWindow(Handle);

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_LAYERED
                | Win32.WS_EX_TOPMOST
                | Win32.WS_EX_TOOLWINDOW
                | Win32.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // 取 Handle 会就地派发 WM_CREATE，所以这一段可能在构造尚未走完时就执行：
        // 此时 _frame 可能已建好但 Hwnd 还没设，由这里先接上 Hwnd 再出帧（句柄就是在这里诞生的）。
        // 句柄重建时本方法会再进来一次，重新校正尺寸并出帧 —— 重复出帧无害，缺帧才有害。
        // 注意：这里的每一步都不能抛（.dsh-code/mistakes.md：WM_CREATE 里抛异常会弹模态对话框，
        // 把无限长的等待记在「取 Handle」那一段计时上，完全看不出真因）。
        ApplyPhysicalSize();
        ApplyFrameToWindowSize();

        if (_frame is not null)
        {
            _frame.Hwnd = Handle;
            PresentFirstFrame();
        }

        MoveToBottomRight();
    }

    private void RaiseWindowEvent(string type, System.Text.Json.Nodes.JsonObject? payload)
    {
        try { WindowEvent?.Invoke(type, payload); }
        catch (Exception ex) { KernelLog.Write($"window-event-listener-failed {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>窗口事件委托：Compositor 不能引用 PetDock.Hosting 的类型，宿主侧适配。</summary>
    internal delegate void WindowEventDelegate(string type, System.Text.Json.Nodes.JsonObject? payload);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _speech?.Dispose();
            DisposeThrow();
            _tapTimer.Dispose();
            _frame?.Dispose();
            if (_ownsContent)
            {
                _content.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}

/// <summary>构造期分步耗时（毫秒）：DIB 建立 / 取句柄（含首次 Present）。</summary>
internal readonly record struct StartupTimings(long FrameMs, long HandleMs);
