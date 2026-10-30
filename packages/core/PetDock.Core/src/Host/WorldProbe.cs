using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>
/// Fills WorldSnapshot from real sources (contract §7.1): cursor via GetCursorPos minus the window
/// origin (outside → null), foreground app via GetForegroundWindow → pid → process name, occlusion
/// via TopLevelWindowFromPoint at the window centre. WorldSnapshot fields are value-only — zero added,
/// zero removed. TimeOfDayMinutes is injectable for the self-test; wall clock otherwise.
/// </summary>
public sealed class WorldProbe
{
    private readonly Func<int>? _timeOfDayMinutes;
    private PetRect _lastWindowRect = new(0, 0, 0, 0);

    // 前台进程名的按句柄缓存：`ProcessNameOf` 内部是一次全进程枚举（GetProcessById），
    // 每 tick 采样会以 ~3KB/帧的速度制造 GC 垃圾，D-14d 的稳态判据被它顶成 FAIL。
    // 同一前台句柄必是同一进程，句柄变了才需要重新问一次。
    private IntPtr _focusedHandle;
    private string? _focusedName;

    public WorldProbe(Func<int>? timeOfDayMinutes = null) => _timeOfDayMinutes = timeOfDayMinutes;

    public PetPoint? LastCursor { get; private set; }
    public string? LastFocusedApp { get; private set; }
    public int SampleCount { get; private set; }

    /// <summary>
    /// 最近一次 GetCursorPos 的**原始屏幕坐标**，与窗口无关（光标在窗口外也照样有值）。
    /// 为什么与 <see cref="LastCursor"/> 分开：`WorldSnapshot.Cursor` 按契约 §7.1 必须是
    /// 「窗口内坐标，窗口外 → null」，而 `input.pointer.move`（原规格 16 §3，host-generated）
    /// 要的是「指针在哪」，不受窗口边界限制 —— 两者共用一个字段会让鼠标一离开宠物就再也不发事件，
    /// 而「鼠标移过来 → 宠物反应」这条 M4 判据恰恰要求它在窗口外也继续发。
    /// </summary>
    public PetPoint? LastScreenCursor { get; private set; }

    /// <summary>Sample one world snapshot. hwnd==0 keeps the last known window rect (never had one → 0,0,0,0).</summary>
    public WorldSnapshot Sample(IntPtr hwnd)
    {
        SampleCount++;
        PetPoint? cursor = SampleCursor(hwnd);
        PetRect rect = SampleRect(hwnd);
        string? focused = SampleFocusedApp();
        bool occluded = SampleOccluded(hwnd);
        int tod = _timeOfDayMinutes is not null ? _timeOfDayMinutes() : DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        return new WorldSnapshot(cursor, rect, focused, tod, occluded);
    }

    private PetPoint? SampleCursor(IntPtr hwnd)
    {
        if (!Win32.GetCursorPos(out POINT p)) return null;
        LastScreenCursor = new PetPoint(p.X, p.Y);
        if (hwnd == IntPtr.Zero)
        {
            LastCursor = LastScreenCursor;
            return LastCursor;
        }
        System.Drawing.Rectangle screen = Win32.ScreenRect(hwnd);
        if (!screen.Contains(p.X, p.Y)) return null; // outside the window → null (contract §7.1)
        LastCursor = new PetPoint(p.X - screen.Left, p.Y - screen.Top);
        return LastCursor;
    }

    private PetRect SampleRect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return _lastWindowRect;
        System.Drawing.Rectangle r = Win32.ScreenRect(hwnd);
        _lastWindowRect = new PetRect(r.Left, r.Top, r.Width, r.Height);
        return _lastWindowRect;
    }

    private string? SampleFocusedApp()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        // 同一个前台句柄只问一次进程名。`ProcessNameOf` 每次都要 `Process.GetProcessById`
        // （起一个 Process 对象 + 读进程信息），30fps 下这是每帧一次的分配，实测把 D-14d 的
        // 稳态判据顶成 FAIL。句柄变了才重问：句柄复用（同数值换了窗）存在，所以键是句柄本身。
        if (hwnd != _focusedHandle)
        {
            _focusedHandle = hwnd;
            if (Win32.GetWindowThreadProcessId(hwnd, out uint pid) == 0)
            {
                _focusedName = null;
            }
            else
            {
                string name = Win32.ProcessNameOf(pid);
                // 契约 §7.1：这里**永不**返回 "(unknown)"，取不到就当「没有前台应用」。
                _focusedName = string.IsNullOrEmpty(name) || name == "(unknown)" ? null : name;
            }
        }

        if (_focusedName is null) return null;
        LastFocusedApp = _focusedName;
        return _focusedName;
    }

    private bool SampleOccluded(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        System.Drawing.Rectangle r = Win32.ScreenRect(hwnd);
        var centre = new POINT { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 };
        IntPtr top = Win32.TopLevelWindowFromPoint(centre);
        return top != IntPtr.Zero && top != hwnd;
    }
}
