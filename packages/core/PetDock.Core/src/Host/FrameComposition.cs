// 内容装配（t1 契约 §3.2 的收敛点）：**只有一条发现路径 —— PluginHost**。
//
// 退役了什么（§4.2）：旧 FrameContentLoader 的「逐级向上找仓库根、扫 packages/plugins 下
// 所有 bin/*.dll、按 OrdinalIgnoreCase 排序取第一个能构造的工厂」这套 dll 反射发现**已删除**。
// 它和 docs/13 §3 的「扫描 plugins/ 目录」（即 PluginHost.Discover：非递归、只认 manifest）
// 是两套发现语义；且「取排序第一个」意味着多个插件同时实现时的选中结果不是仲裁结果。
//
// 保留了什么（§4.2 理由 1/2）：IFrameContentProvider 与 IFrameContentSource 两个接口
// **保留、不改名、不删除**。装配逻辑因此变成两步：
//   1. 名册里有视图实现（docs/11 §3 那个）→ 用 PetFrameSource 包它（帧循环走 ABI）；
//   2. 否则名册里有 M0 的内容源工厂 → 用它的 Create()（M0 的过渡装配面，仍然在役）；
//   3. 都没有 → 返回 null，调用方退回内核纯色兜底 —— 但**自检会把这一步判成失败**
//      （见 Program.cs 的 frame-content 判据：source 必须是一个具体的、非兜底的内容源）。
//
// 环境变量 PETDOCK_FRAME_CONTENT_ASSEMBLY（指定单个程序集）也已退役：它是第二条装配入口，
// 与「单一发现路径」冲突，且自检与常驻都不需要它。
using System;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>一次内容装配的结果：谁提供了内容、以及为什么没有。</summary>
internal sealed record ComposeOutcome(
    IFrameContentSource? Content,
    string SourceType,
    string Note,
    PetInstance? Instance,
    PetSurfaceRoster? Roster)
{
    /// <summary>装上了一个具体（非兜底）的内容源。</summary>
    public bool HasContent => Content is not null && SourceType.Length > 0;

    /// <summary>装配说明（自检的 stderr 取证行）。字段名用 installed 而不是 frame ——
    /// 自检的 `frame=` 判的是「帧缓冲已就绪」，这里判的是「内容源已装上」，
    /// 两者同名会让人把「没装上」读成「没出帧」（或反过来）。</summary>
    public string Describe() =>
        $"source={SourceType} installed={(Content is null ? "false" : "true")} {Note}";
}

/// <summary>把「名册」变成「窗口能用的内容源」。</summary>
internal static class FrameComposition
{
    /// <summary>内核兜底内容源的类型名（判据要求 source 不等于它 —— M0 教训的直接修复）。</summary>
    public const string FallbackTypeName = "PetDock.Core.Compositor.SolidFrameContent";

    /// <summary>名册为空时的说明。</summary>
    public const string NoteNoSurface = "no-abi-surface-in-roster";

    /// <summary>
    /// 装配内容源。任何一步失败都只是「上层没提供内容」，绝不抛 ——
    /// 窗口必须仍然可见、仍然可命中；判据的非 0 由自检给，不由本方法给。
    /// </summary>
    public static ComposeOutcome Compose(string phase, bool attachInstance)
    {
        PetSurfaceRoster roster = PetSurfaceRoster.Discover(PluginHost.ProbePluginsRoot());
        KernelLog.Write($"surface-roster phase={phase} {roster.Describe()}");
        roster.BrainSelector = BrainSelectorFromEnvironment();

        if (roster.View is not null)
        {
            PetInstance? instance = roster.BuildInstance(phase);
            if (instance is not null && attachInstance)
            {
                instance.SuspendPort = SuspendPortFor(null);
            }

            if (instance is not null)
            {
                // 行为层选了谁必须可见：docs/11:308 让默认实现优先，任何「第三方 brain 被静默忽略」
                // 都会在屏幕上看成「宠物不动」，而在日志里查不出来。
                KernelLog.Write($"surface-brain instance={instance.Id}"
                    + $" selected={(instance.Brain?.Meta.Id ?? "(none)")}"
                    + $" selector={(roster.BrainSelector ?? "(unset)")}");

                return new ComposeOutcome(
                    new PetFrameSource(instance),
                    roster.ViewTypeName,
                    $"via=roster view={roster.ViewTypeName} dirs={roster.DiscoveredDirs}",
                    instance,
                    roster);
            }
        }

        IFrameContentProvider? provider = roster.ContentProvider;
        if (provider is not null)
        {
            try
            {
                IFrameContentSource? content = provider.Create();
                return new ComposeOutcome(
                    content,
                    content is null ? string.Empty : provider.GetType().FullName ?? provider.GetType().Name,
                    $"via=content-provider note={provider.Note}",
                    null,
                    roster);
            }
            catch (Exception error)
            {
                KernelLog.Write($"content-provider-failed {error.GetType().Name}: {error.Message}");
            }
        }

        return new ComposeOutcome(null, string.Empty, NoteNoSurface, null, roster);
    }

    /// <summary>挂起转发口：有真实插件宿主就转到 PluginHost.Disable，没有就用空实现。</summary>
    internal static IInstanceSuspendPort SuspendPortFor(PluginHost? host) =>
        host is null ? new NoopSuspendPort() : new HostSuspendPort(host);

    /// <summary>
    /// 行为层选择器的环境变量名（与 PETDOCK_PLUGINS_ROOT 同一形态）。
    /// 值是一个插件 id；未设置或匹配不到时退回「取首」（= docs/11:308 的默认实现优先）。
    /// 为什么用环境变量而不是写死：docs/01 P-02 第 4 条禁止 core 代码里特判任何插件 id，
    /// 但允许「内核通过插件 ID 引用配置条目」—— 这条就是那个配置条目。
    /// </summary>
    public const string BrainSelectorEnv = "PETDOCK_BRAIN";

    private static string? BrainSelectorFromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable(BrainSelectorEnv);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
