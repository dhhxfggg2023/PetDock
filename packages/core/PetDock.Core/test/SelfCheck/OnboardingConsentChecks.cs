// 引导窗与授权窗的互斥判据（RFC-16 §3.3）。
//
// 为什么单独一个文件：OnboardingChecks.cs 已经接近 D-03c 的单文件 400 行上限
// （D-03c 对 test/ 目录同样生效）。
//
// 判据要证明的是**纪律**，不是「引导看起来挺好」：
//   模态窗自带消息循环，宿主的滴答器在模态期间仍会触发。
//   如果引导开着的时候滴答器照常泵授权询问，用户先看到的会是一个压在引导上的
//   授权框 —— 两个模态窗叠在一起。正确的行为是：询问**留在队列里**（不丢请求），
//   引导关掉之后再弹。
//
// 判据走生产对象：真的 UiConsentPort（真的排队与阻塞）、真的 UiHost（真的滴答）。
// 唯一的替换是「引导窗」用一个**不显示**的真窗（AttachOnboarding 挂上去）——
// 显示真窗会挡住桌面，判据要的只是「有一个引导窗在管事」这个状态。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class OnboardingConsentChecks
{
    /// <summary>判据的输出与计数回调（与 UiSelfTest 的 Check 同形）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    /// <summary>等待后台线程把询问排进队列的时长（毫秒）。</summary>
    private const int EnqueueWaitMs = 500;

    /// <summary>引导窗的写设置回调（判据里不真的写盘，返回成功即可）。</summary>
    private static bool AcceptSettings(string? language, bool complete, out string error)
    {
        error = string.Empty;
        return true;
    }

    public static void Run(Check check)
    {
        try
        {
            RunAll(check);
        }
        catch (Exception error)
        {
            check("ui-onboarding-consent-not-pumped", false, $"{error.GetType().Name}: {error.Message}");
            check("ui-onboarding-consent-request-kept", false, "exception");
        }
    }

    private static void RunAll(Check check)
    {
        using var window = new LayeredFrameWindow();
        window.Hide();

        var port = new UiConsentPort();
        var store = new UserSettingsStore(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "petdock-onboarding-consent-" + Guid.NewGuid().ToString("N")));

        using var host = new UiHost(window, null, null, null, port, store);

        // 后台线程问一次授权：它会排进端口队列并阻塞到有人回答。
        var request = new ConsentRequest(
            "com.example.probe",
            "示例插件",
            1,
            new List<ConsentItem> { new("input.mousePosition", false, false) },
            false);

        var answered = new ManualResetEventSlim(false);
        ConsentAnswer? answer = null;
        var asker = new Thread(() =>
        {
            answer = port.Ask(request);
            answered.Set();
        });
        asker.IsBackground = true;
        asker.Start();

        // 等它排进队列（不等回答：这一轮就是要证明回答没来）。
        bool queued = answered.Wait(EnqueueWaitMs) == false && port.PendingCount == 1;

        // 挂一个**不显示**的真引导窗上去：模拟「引导窗在管事」。
        using var onboarding = new OnboardingWindow(AcceptSettings, hasPet: true);
        host.AttachOnboarding(onboarding);

        int pumpsBefore = host.ConsentPumpCount;
        host.TickOnce();
        int pumpsAfter = host.ConsentPumpCount;

        // ① 引导开着的时候，滴答不许泵授权询问。
        check("ui-onboarding-consent-not-pumped",
            pumpsAfter == pumpsBefore && !port.Showing,
            $"pumps-before={pumpsBefore} pumps-after={pumpsAfter} showing={port.Showing}");

        // ② 询问留在队列里，没有被丢掉（request not lost）。
        check("ui-onboarding-consent-request-kept",
            queued && port.PendingCount == 1,
            $"queued={queued} pending={port.PendingCount}");

        // 收尾：关掉端口让等待方醒来拿 null（= 没有回答 = 按拒绝处理），
        // 免得后台线程永远挂着（自检进程退出时它也不会拖住退出，但别留悬念）。
        port.Close();
        answered.Wait(EnqueueWaitMs);
        asker.Join(EnqueueWaitMs);
        check("ui-onboarding-consent-asker-released",
            answer is null,
            $"answered={answered.IsSet} answer={(answer is null ? "(none)" : "granted")}");
    }
}
