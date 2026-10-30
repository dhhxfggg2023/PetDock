// 内容装配的唯一入口（D-03c 拆分，2026-10-28）：窗口持有可换源代理、句柄回填、
// moveTo 接线都在这里；自检与常驻共用同一条链。
using System.Collections.Generic;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.Core;

internal static partial class Program
{
    /// <summary>
    /// 内容装配的唯一入口：见 FrameComposition。装配结果连同实例一起返回，
    /// 自检与常驻共用同一条链（两条链正是 M0 source=false 静默 SELF-OK 的成因）。
    /// </summary>
    private static LayeredFrameWindow ComposeWindow(string phase, PluginHost? host = null)
        => ComposeWindow(phase, host, out _);

    /// <summary>
    /// 同 <see cref="ComposeWindow(string, PluginHost?)"/>，另外交出窗口真正持有的可换源代理。
    /// 导入与恢复都通过它换宠物；调用方拿不到它就会去换一份窗口没在用的源。
    /// </summary>
    private static LayeredFrameWindow ComposeWindow(
        string phase,
        PluginHost? host,
        out SwappableFrameSource? frames,
        IReadOnlyCollection<string>? disabledPlugins = null,
        IReadOnlyCollection<string>? enabledPlugins = null,
        bool respectStartupPolicy = false,
        bool allowDefaultPet = true)
    {
        ComposeOutcome outcome = FrameComposition.Compose(
            phase,
            attachInstance: true,
            host,
            disabledPlugins,
            enabledPlugins,
            respectStartupPolicy,
            allowDefaultPet);
        if (outcome.Instance is not null && host is not null)
        {
            outcome.Instance.SuspendPort = FrameComposition.SuspendPortFor(host);
        }

        Composition = outcome;
        KernelLog.Write($"frame-content phase={phase} {outcome.Describe()}");
        SwappableFrameSource? proxy = outcome.Content is null
            ? null
            : new SwappableFrameSource(outcome.Content, outcome.Roster);
        frames = proxy;
        LayeredFrameWindow window = proxy is null
            ? new LayeredFrameWindow()
            : new LayeredFrameWindow(proxy);

        // 窗口句柄回填（M4-VERIFY-r2.md 的 F 号缺口）：窗口要到这一步才存在，而帧源在
        // FrameComposition 里就先建好了。不回填的话 WorldProbe 永远拿 hwnd==0 采样 ——
        // 鼠标取屏幕绝对坐标、WindowRect 恒 0、IsOccluded 恒 false，三类世界输入里两类的值都是假的。
        // 事件订阅走代理：换源之后事件要跟着新宠物走，挂在旧源上等于新宠物收不到窗口移动。
        if (proxy is not null)
        {
            proxy.WindowHandle = window.WindowHandle;
            window.WindowEvent += (type, payload) => proxy.PublishWindowEvent(type, payload);
            KernelLog.Write($"frame-source-bound window=0x{window.WindowHandle.ToInt64():X} swappable=true");
        }

        // RFC-35 §3.2：窗口鼠标消息 → 实例的交互会话（原规格 11 §5）。接线只在 FrameComposition
        // 一处（自检也调它），免得「生产接上了、判据测的是另一份接线」。
        if (outcome.Instance is PetInstance pet)
        {
            FrameComposition.WireWindow(window, pet);
            KernelLog.Write($"window-wiring instance={pet.Id} surfaces={pet.SurfacesByPriority.Count} mover=true");
        }

        return window;
    }

    /// <summary>最近一次内容装配的结果（自检判据读它）。</summary>
    private static ComposeOutcome? Composition { get; set; }
}
