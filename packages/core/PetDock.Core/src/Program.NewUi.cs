// 「--ui next」这条路的装配（R1e，2026-10-31）。故意很短：新层自己起泵、自己开窗，
// 这里只做三件事 —— 读语言、把已本地化的文案递进去、把异常收成一条日志。
//
// 为什么不在新层里查文案：新层的文案服务还没写（R2/R3）。现在把「谁翻译」固定成 Core 的 I18n
// 会让新层永远依赖旧层；反过来，由调用方递字符串，新层保持对旧层零依赖（csproj 里也没有那条引用）。
//
// 这条分支只在**用户形态**下有意义：--uitest/--selftest 等诊断旗标不抢单实例锁，
// 也不会走这里（两个 Application 抢同一条线程只会互相打死，见 src/UiEntry.cs 的文件头）。
using System;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;
using PetDock.Hosting;
using PetDock.Ui;

namespace PetDock.Core;

internal static partial class Program
{
    /// <summary>命令行里是否有「--ui next」（成对出现才算：单独的 --ui 是参数错误，不当开关用）。</summary>
    private static bool WantsNewUi(string[] args)
    {
        int at = Array.IndexOf(args, "--ui");
        return at >= 0
            && at + 1 < args.Length
            && string.Equals(args[at + 1], "next", StringComparison.Ordinal);
    }

    /// <summary>起新界面层，直到用户退出。失败也只写日志、返回失败码 —— 不留幽灵进程。</summary>
    private static int RunNewUi()
    {
        try
        {
            UserSettings settings = new UserSettingsStore(PetDataStore.Resolve().Root).Load();
            I18n.Load(settings.Language);
        }
        catch (Exception error)
        {
            // 读设置失败也照样要能起来：I18n 会退回默认语言。
            KernelLog.Write($"ui-next-settings-failed {error.GetType().Name}: {error.Message}");
        }

        var strings = new NewUiStrings(
            I18n.Get("ui.next.title"),
            I18n.Get("ui.next.placeholder"),
            I18n.Get("ui.next.trayTip"),
            I18n.Get("ui.next.trayOpen"),
            I18n.Get("ui.next.trayExit"));

        try
        {
            KernelLog.Write("ui-next-start");
            int code = UiEntry.Run(strings);
            KernelLog.Write($"ui-next-exit code={code}");
            return code;
        }
        catch (Exception error)
        {
            KernelLog.Write($"ui-next-failed {error.GetType().Name}: {error.Message}");
            return ExitFail;
        }
    }
}
