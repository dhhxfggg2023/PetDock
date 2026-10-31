// 新界面层的入口（R1e，2026-10-31）。这一片只做三件事：起 WPF 泵、开壳窗口、挂托盘。
//
// 为什么在 Main 里分叉而不是「新壳包旧壳」：旧路径的 UiHost 是 WinForms ApplicationContext，
// 它自己就是消息泵（Application.Run(host)）。两个 Application 抢同一条 STA 线程只会互相打死，
// 所以两条路必须在 Program.Main 里二选一（见 Core/src/Program.NewUi.cs）。
//
// 现在还没有页面、导航、服务：这一片要证的是「换泵之后进程能起、能退、托盘在」。
// 页面（R4–R6）将来从 PetDock.App 的契约拿数据，不从新壳直接摸内核。
//
// 文案不在这里查表：调用方把已经本地化好的字符串递进来（NewUiStrings）。
// 理由是新层的文案服务还没写（R2/R3），而「谁来翻译」这件事不该在这一片里被固定成 Core 的 I18n。
using System;
using System.Windows;

namespace PetDock.Ui;

/// <summary>新壳启动时需要的文案（全部已本地化）。</summary>
public sealed record NewUiStrings(
    string Title,
    string Placeholder,
    string TrayTip,
    string TrayOpen,
    string TrayExit);

/// <summary>新界面层的进程入口。返回值即退出码。</summary>
public static class UiEntry
{
    /// <summary>起泵并进入消息循环，直到壳窗口被关闭或托盘选择「退出」。</summary>
    public static int Run(NewUiStrings strings)
    {
        ArgumentNullException.ThrowIfNull(strings);

        var app = new System.Windows.Application
        {
            // 关窗即退出：这一层还没有「关掉面板但进程仍在」的托盘常驻语义。
            // R3 会把常驻/退出重新定义清楚；在那之前用最不容易留幽灵进程的默认值。
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose,
        };

        var window = new AppWindow(strings);
        using NotifyIconTray tray = new(strings.TrayTip, strings.TrayOpen, strings.TrayExit);

        // 托盘两项：打开窗口 / 退出进程。都只是把意图交回这里，托盘自己不碰窗口状态。
        tray.OpenRequested += (_, _) => Restore(window);
        tray.ExitRequested += (_, _) => app.Shutdown();

        try
        {
            return app.Run(window);
        }
        finally
        {
            // 托盘图标必须在退出前拿掉：否则进程没了图标还挂在托盘里，直到用户划过它。
            tray.Dispose();
        }
    }

    /// <summary>把窗口从最小化/隐藏里叫回来（托盘双击、托盘菜单）。</summary>
    private static void Restore(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }
}
