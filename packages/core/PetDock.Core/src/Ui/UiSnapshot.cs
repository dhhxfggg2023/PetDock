// UI 只读行类型（RFC-12 §2.2）。这里的每一个字段都必须是**可以在没有 UI 的情况下算出**
// 的纯数据：面板只负责摆放，不做判定，也不碰可变状态（原规格 18 §2.5「面板只读展示」）。
using System.Collections.Generic;

namespace PetDock.Core.Ui;

/// <summary>无参回调（D-03b：系统自带的同名泛型委托含黑名单子串，自声明代替，同 PluginHost 的做法）。</summary>
internal delegate void UiCommand();

/// <summary>宠物页一行。Memory 是**进程级**读数（宠物与内核同进程，原规格 10 §3），
/// 所以文案里必须写明「进程」，不能让读者以为这是单只宠物的独占内存。</summary>
public sealed record PetLine(
    string Id,
    string Name,
    string Source,
    string State,
    string Memory)
{
    /// <summary>标准包声明的作者（没有就 null；界面显示「未声明」而不是编一个）。</summary>
    public string? Author { get; init; }

    /// <summary>标准包声明的许可（字符串；对象形式取 spdx/name）。</summary>
    public string? License { get; init; }

    /// <summary>素材包目录（只读展示，不用于写操作）。</summary>
    public string? Path { get; init; }

    /// <summary>包内 icon.png/thumbnail.png 的绝对路径；没有就 null。</summary>
    public string? IconPath { get; init; }
}

/// <summary>插件页一行。Granted 是当前有效的能力句柄数（原规格 14 §2「能力是句柄」）。
/// Id 放最前：插件页的两个入口要「对某个插件说话」，必须有 id（RFC-13 §2.6）。
/// Removable 只由清单 essential 决定；内置的可选插件同样可删，系统必需的不可删。</summary>
public sealed record PluginLine(
    string Id,
    string Name,
    string Version,
    int Tier,
    string State,
    int Granted,
    string Source = "",
    bool Enabled = true,
    bool Loaded = true,
    bool Removable = false,
    bool Essential = false);

/// <summary>
/// 桌上**某一只**的即时状态（RFC-37 片 4）。「可见」与「在出帧」分开报：隐藏的宠物窗口
/// 仍然存在，泵停着 —— 把两者并成一个布尔会让「收起但没停泵」这种真缺陷消失。
/// </summary>
public sealed record PetReading(
    string Id,
    bool Visible,
    bool Pumping);

/// <summary>
/// 设置页的当前状态（原规格 18 §2.3）。面板读它来摆放控件，宿主改它来生效。
/// StartupEnabled 的事实源是注册表，不是设置文件（同一事实存两处必然漂移）。
/// </summary>
public sealed record SettingsView(
    double IdleFrameBudgetMs,
    string Language,
    bool StartupEnabled,
    string? Theme = null,
    string? Material = null,
    int PetSize = 128,
    string? PetName = null,
    string? Version = null)
{
    /// <summary>没有设置可读时的默认状态。</summary>
    public static SettingsView Default { get; } = new(
        1000.0 / 30.0, "zh-CN", false, "system", "opaque", 128, null, null);
}

/// <summary>面板一次重绘所需的全部数据。</summary>
public sealed record UiSnapshot(
    IReadOnlyList<PetLine> Pets,
    IReadOnlyList<PluginLine> Plugins,
    SettingsView? Settings = null)
{
    public static UiSnapshot Empty { get; } = new(
        System.Array.Empty<PetLine>(),
        System.Array.Empty<PluginLine>());
}
