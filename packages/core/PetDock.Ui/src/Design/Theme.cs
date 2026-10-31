// 当前主题（R2a）：一个可切换的单例 + 一个 Changed 事件。
//
// 为什么不是依赖注入：界面层的颜色是**进程级**的（用户在设置里切换主题，所有页面一起变），
// 传一个 Palette 参数进去只会让每个控件都多一个构造函数参数。这里刻意做成静态，
// 但把所有可变面收成「一个字段 + 一个事件」—— R3 的壳订阅 Changed 重画，页面不需要知道。
using System;

namespace PetDock.Ui.Design;

/// <summary>当前主题：读 Current，切 SetMode，订阅 Changed。</summary>
public static class Theme
{
    private static Palette _current = Palette.Dark;

    /// <summary>当前模式（默认深色）。</summary>
    public static ThemeMode Mode { get; private set; } = ThemeMode.Dark;

    /// <summary>当前配色。切换模式后立刻变。</summary>
    public static Palette Current => _current;

    /// <summary>模式变了（订阅方自己切回 UI 线程；实际切换都在 UI 线程发起）。</summary>
    public static event EventHandler? Changed;

    /// <summary>切换明暗。同模式重复调用不发事件（否则每次设置回显都会重画一遍）。</summary>
    public static void SetMode(ThemeMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        _current = mode == ThemeMode.Light ? Palette.Light : Palette.Dark;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
