// 原规格 16 §3：`input.key.down` / `up` 的 Source 是 `capability only`，前置资格 `input.keyboardHook`。
// 原规格 14:110：涉及输入钩子的能力必须在托盘菜单带「停止全局钩子」固定项——那一条已由 TrayHost
// 的 pause-hooks 项实现，本文件是它背后真正被停掉的那个东西。
//
// 为什么是低级钩子（WH_KEYBOARD_LL）而不是 RegisterHotKey 或 GetAsyncKeyState 轮询：
//   * RegisterHotKey 只能拿到注册过的少数组合键，拿不到「任意键」；
//   * 轮询会漏键（两次采样之间按下又抬起就看不见），而键盘事件必须是逐个的；
//   * 低级钩子由 OS 在输入投递前回调，是唯一能拿到全部按键的机制。
//
// 三条必须守住的纪律：
//   1. **永不吞键**：回调无条件 `return CallNextHookEx(...)`。这个进程是桌宠，不是输入法；
//      一个吞掉用户按键的桌宠是不可接受的缺陷，而不是配置项。
//   2. **委托必须被字段持有**：`SetWindowsHookEx` 只存函数指针，不持有托管委托；委托被 GC
//      回收后 OS 回调到已释放的 thunk 会直接崩进程（这是该 API 最经典的坑）。
//   3. **钩子只在有插件持权时安装**：原规格 16:56「设备钩子只在插件拥有权限后激活；宿主不默认钩住」。
//      所以 Install 是显式动作，构造函数不装任何东西。

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PetDock.Core.Input;

/// <summary>一条按键事件：虚拟键码 + 修饰键位掩码 + 是按下还是抬起。</summary>
internal readonly record struct KeyStroke(int VKey, int Modifiers, bool Down);

/// <summary>
/// 按键回调的委托形态。
/// 不用系统自带的那个泛型委托：它的类型名本身含 D-03b 黑名单里的词（子串匹配，注释里出现也算），
/// 而本文件在内核里、必须过 lint-core。项目里已有同样做法的先例（PluginHost 的 HostCommand）。
/// </summary>
internal delegate void KeyStrokeHandler(KeyStroke stroke);

/// <summary>
/// 全局键盘钩子（WH_KEYBOARD_LL）。安装/卸载/暂停都是显式动作，可反复调用。
/// 必须在有消息泵的线程上安装——钩子回调由安装线程的消息循环派发。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    /// <summary>修饰键位掩码（与 原规格 16 §3 的 `modifiers` 字段同义，宿主自定义编码）。</summary>
    internal const int ModShift = 1;
    internal const int ModControl = 2;
    internal const int ModAlt = 4;

    // 必须由字段持有，见文件头纪律 2。
    private readonly LowLevelProc _proc;
    private IntPtr _handle;
    private bool _paused;
    private bool _disposed;

    internal KeyboardHook(KeyStrokeHandler onKey)
    {
        OnKey = onKey ?? throw new ArgumentNullException(nameof(onKey));
        _proc = Callback;
    }

    private KeyStrokeHandler OnKey { get; }

    internal bool IsInstalled => _handle != IntPtr.Zero;

    /// <summary>暂停期间钩子仍装着，但不再向外发事件（托盘固定项用它，避免反复装卸）。</summary>
    internal bool Paused => _paused;

    /// <summary>已安装则返回 true；重复调用无副作用。失败时返回 false 并留 KernelLog 由调用方决定。</summary>
    internal bool Install()
    {
        if (_disposed || IsInstalled)
        {
            return IsInstalled;
        }

        _handle = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        return IsInstalled;
    }

    internal void Uninstall()
    {
        if (!IsInstalled)
        {
            return;
        }

        UnhookWindowsHookEx(_handle);
        _handle = IntPtr.Zero;
    }

    internal void Pause() => _paused = true;

    internal void Resume() => _paused = false;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Uninstall();
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        // code < 0：OS 要求原样透传，不得做任何处理（该分支里连 Marshal 都不该碰）。
        if (code >= 0 && !_paused)
        {
            int message = wParam.ToInt32();
            bool isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool isUp = message is WM_KEYUP or WM_SYSKEYUP;
            if (isDown || isUp)
            {
                try
                {
                    int vkey = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode 是第一个字段
                    OnKey(new KeyStroke(vkey, ModifierMask(), isDown));
                }
                catch (Exception error)
                {
                    // 回调里抛出的异常会直接终止进程（跨托管/非托管边界），必须就地吞掉。
                    // 用 Debug 而不是 Console：钩子可能在无控制台时回调。
                    Debug.WriteLine($"keyboard-hook-callback-failed {error.GetType().Name}: {error.Message}");
                }
            }
        }

        // 纪律 1：无条件放行。绝不能因为「这是我们的宠物关心的键」而返回非零。
        return CallNextHookEx(_handle, code, wParam, lParam);
    }

    private static int ModifierMask()
    {
        int mask = 0;
        if ((GetKeyState(VK_SHIFT) & 0x8000) != 0)
        {
            mask |= ModShift;
        }

        if ((GetKeyState(VK_CONTROL) & 0x8000) != 0)
        {
            mask |= ModControl;
        }

        if ((GetKeyState(VK_MENU) & 0x8000) != 0)
        {
            mask |= ModAlt;
        }

        return mask;
    }

    private delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
