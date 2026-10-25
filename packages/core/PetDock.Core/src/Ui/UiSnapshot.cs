// UI 只读行类型（RFC-12 §2.2）。这里的每一个字段都必须是**可以在没有 UI 的情况下算出**
// 的纯数据：面板只负责摆放，不做判定，也不碰可变状态（docs/18 §2.5「面板只读展示」）。
using System.Collections.Generic;

namespace PetDock.Core.Ui;

/// <summary>无参回调（D-03b：系统自带的同名泛型委托含黑名单子串，自声明代替，同 PluginHost 的做法）。</summary>
internal delegate void UiCommand();

/// <summary>宠物页一行。Memory 是**进程级**读数（宠物与内核同进程，docs/10 §3），
/// 所以文案里必须写明「进程」，不能让读者以为这是单只宠物的独占内存。
/// Id 是**归因键**：性能页要按宠物取它自己那份帧读数（RFC-37 片 4），没有 id 就只能摊。</summary>
internal sealed record PetLine(
    string Id,
    string Name,
    string Source,
    string State,
    string Memory);

/// <summary>插件页一行。Granted 是当前有效的能力句柄数（docs/14 §2「能力是句柄」）。
/// Id 放最前：插件页的两个入口要「对某个插件说话」，必须有 id（RFC-13 §2.6）。</summary>
internal sealed record PluginLine(
    string Id,
    string Name,
    string Version,
    int Tier,
    string State,
    int Granted);

/// <summary>性能页一行。Overruns 是 FrameBudget 超限计数（docs/41 D-14 的观测面）。
/// 可空：`null` = 这只宠物的帧读数**不可归属**（没有自己的泵），页面写「—」而不是 0。</summary>
internal sealed record PerfLine(
    string Name,
    string Cpu,
    string Memory,
    string FrameMs,
    long? Overruns);

/// <summary>性能页顶部汇总 + 逐宠物明细。</summary>
internal sealed record PerfSummary(
    string Window,
    int Degraded,
    int Isolated,
    IReadOnlyList<PerfLine> Rows,
    bool CountersWired = false)
{
    /// <summary>
    /// 没有任何宠物时的诚实摘要（docs/18 §2.4：不编造数字）。
    /// 是**属性**而不是静态字段：字段会在类型初始化时把当时的语言冻结进字符串，
    /// 而 <see cref="I18n.Load"/> 发生在界面对象构造之前、静态初始化之后（RFC-42 §3.2）。
    /// </summary>
    public static PerfSummary Empty => new(
        I18n.Get("ui.perf.notSampled"), 0, 0, System.Array.Empty<PerfLine>());

    public bool IsEmpty => Rows.Count == 0;
}

/// <summary>
/// 帧泵的逐宠物读数（RFC-30 §3.2）。**唯一来源是帧泵**：常驻形态一个泵只服务一只宠物，
/// 所以「多只宠物共用一次采样」是事实而不是近似 —— 面板不得把泵读数摊给多只宠物
/// （docs/18 §2.4 的「不得编造数字」）。
/// </summary>
internal sealed record PetFrameReadings(
    double AvgFrameMs,
    double MaxFrameMs,
    double TargetFrameMs,
    long OverBudget);

/// <summary>
/// 桌上**某一只**的即时状态（RFC-37 片 4）。Id 是归因键，与 <see cref="PetLine.Id"/> 同一命名空间；
/// Frames 可空 = 这一只没有自己的泵（主窗还没建循环、或它不在集合里），页面据此写「—」。
/// 「可见」与「在出帧」分开报：隐藏的宠物窗口仍然存在，泵停着 —— 把两者并成一个布尔
/// 会让「收起但没停泵」这种真缺陷消失。
/// </summary>
internal sealed record PetReading(
    string Id,
    bool Visible,
    bool Pumping,
    PetFrameReadings? Frames);

/// <summary>
/// 设置页的当前状态（docs/18 §2.3）。面板读它来摆放控件，宿主改它来生效。
/// StartupEnabled 的事实源是注册表，不是设置文件（同一事实存两处必然漂移）。
/// </summary>
internal sealed record SettingsView(
    double IdleFrameBudgetMs,
    string Language,
    bool StartupEnabled)
{
    /// <summary>没有设置可读时的默认状态。</summary>
    public static SettingsView Default { get; } = new(1000.0 / 30.0, "zh-CN", false);
}

/// <summary>面板一次重绘所需的全部数据。</summary>
internal sealed record UiSnapshot(
    IReadOnlyList<PetLine> Pets,
    IReadOnlyList<PluginLine> Plugins,
    PerfSummary Perf,
    SettingsView? Settings = null)
{
    public static UiSnapshot Empty { get; } = new(
        System.Array.Empty<PetLine>(),
        System.Array.Empty<PluginLine>(),
        PerfSummary.Empty);
}
