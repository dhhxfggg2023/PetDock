// UI 插件契约（2026-10-28「一切皆可插件」）：壳不认识任何具体页面。
//
// 依赖方向：core 声明契约 → sdk 类型转发 → 插件实现。壳只做四件事：
// 发现插件、排导航、套主题、给页面一个受控的数据/命令面。宠物页、插件页、设置页
// 自己也是插件（随包发行的 first-party UI 插件），与第三方走同一条路径。
//
// 为什么页面返回 FrameworkElement 而不是 XAML 文件：WPF 的资源、样式、数据绑定都在
// 进程内；页面插件是受信任的原生插件（tier 0，随包发行），与内核同进程。tier 1/2 的
// 第三方页面将来走声明式描述，不把未受信任代码塞进 UI 线程。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

namespace PetDock.Core.Ui;

/// <summary>一个 UI 插件：贡献零到多页。Id 与清单 id 一致，用于诊断与去重。</summary>
public interface IUiPlugin
{
    /// <summary>插件 id（清单里的 id）。</summary>
    string Id { get; }

    /// <summary>建页。壳在 UI 线程调用一次；页面自己负责数据绑定。</summary>
    IReadOnlyList<IUiPage> CreatePages(IUiPageContext context);
}

/// <summary>壳里的一页。Order 决定导航顺序；Title/Note 由页面自己本地化。</summary>
public interface IUiPage
{
    /// <summary>稳定 id（自检、深链、恢复上次页）。</summary>
    string Id { get; }

    /// <summary>导航项文字。</summary>
    string Title { get; }

    /// <summary>页头一行说明。</summary>
    string Note { get; }

    /// <summary>导航图标：一个字符或短标签（WPF 不依赖图标字体）。</summary>
    string Glyph { get; }

    /// <summary>排序，小的在前。</summary>
    int Order { get; }

    /// <summary>建视图。每次调用返回新实例；壳负责释放。</summary>
    FrameworkElement CreateView();
}

/// <summary>
/// 一次可能耗时很久的宿主命令的结果：成功、失败、用户取消三种结局分开说。
/// 取消不是失败（用户自己按的），所以页面不能把它当错误条展示。
/// </summary>
public sealed record UiCommandResult(bool Ok, bool Cancelled, string Message)
{
    public static UiCommandResult Success(string message = "") => new(true, false, message);

    public static UiCommandResult Failure(string message) => new(false, false, message);

    public static UiCommandResult Cancel(string message) => new(false, true, message);
}

/// <summary>
/// 回收站里的一只插件（插件页「已删除的插件」区）。DirName 是回收站里的目录名，
/// 也是恢复/彻底删除的稳定键；Restorable=false 表示目录还在但清单已不可用。
/// </summary>
public sealed record TrashLine(
    string DirName,
    string Id,
    string Name,
    string Version,
    long Bytes,
    string RemovedAt,
    bool Restorable);

/// <summary>状态条的口吻：info 是计数与提示，success 是「做成了」，problem 是「没做成，原因在这里」。</summary>
public enum UiStatusKind
{
    /// <summary>计数、说明这类中性的话。</summary>
    Info = 0,

    /// <summary>操作成功。</summary>
    Success = 1,

    /// <summary>操作失败，文案要说清楚原因。</summary>
    Problem = 2,
}

/// <summary>
/// 页面能看到/能做的全部事情。页面不持有宿主引用，只拿这个口子；
/// 所有写操作都是「意图」，判定与落账仍在宿主侧（原规格 18 §2.4）。
/// </summary>
public interface IUiPageContext
{
    /// <summary>最近一次只读快照。页面在 Changed 时重新读。</summary>
    UiSnapshot Snapshot { get; }

    /// <summary>宿主侧数据变了（导入完成、插件状态变化、设置应用）。可能在非 UI 线程触发。</summary>
    event EventHandler? Changed;

    /// <summary>请宿主重新取快照并触发 Changed。</summary>
    void Refresh();

    /// <summary>
    /// 写底部状态栏（页面级提示，不弹模态框）。文案**留在这个页面上**，直到用户换页或页面自己 Clear：
    /// 旧实现一 Refresh 就清空，于是「先写结果再刷新列表」变成了一帧都看不见的静默失败。
    /// </summary>
    void SetStatus(string text, UiStatusKind kind = UiStatusKind.Info);

    /// <summary>清掉状态栏（页面自己决定什么时候清，比如用户开始改输入）。</summary>
    void ClearStatus();

    /// <summary>
    /// 问一次「确定吗」。走宿主自绘的置顶确认框，不用系统 MessageBox：
    /// 面板最大化时 MessageBox 会被压在宠物窗下面（真机取证见 Ui/PetDockDialog.cs）。
    /// </summary>
    bool Confirm(string text, string caption);

    /// <summary>取一条本地化文案；缺键返回键名本身（RFC-42 §3.2）。</summary>
    string T(string key);

    /// <summary>带一个占位符的本地化文案。</summary>
    string T(string key, object arg0);

    /// <summary>
    /// 导入素材包目录（后台执行，不阻塞界面）。复制/登记在后台线程，建实例与换源仍回到 UI 线程。
    /// 返回失败或取消时 Message 是给用户看的原因；返回的 Task 在 UI 线程上完成。
    /// </summary>
    Task<UiCommandResult> ImportPetAsync(string sourceDir);

    /// <summary>移除一只宠物（确认框由页面自己弹，宿主只执行）。</summary>
    string? RemovePet(string petId);

    /// <summary>对某个插件发起授权复核（弹窗在宿主侧）。</summary>
    void AuthorizePlugin(string pluginId);

    /// <summary>翻转某个插件的启用/停用（重启生效）。</summary>
    void TogglePluginDisabled(string pluginId);

    /// <summary>
    /// 安装插件（目录或 zip；enable = 安装后立即启用）。解压/复制/校验在后台线程执行。
    /// 返回成功、失败或用户取消；返回的 Task 在 UI 线程上完成。
    /// </summary>
    Task<UiCommandResult> InstallPluginAsync(string source, bool enable);

    /// <summary>删除用户安装的插件。返回 null = 成功。</summary>
    string? RemovePlugin(string pluginId);

    /// <summary>回收站里的插件（按删除时间从新到旧；只读扫描，可能在 UI 线程调用）。</summary>
    IReadOnlyList<TrashLine> TrashPlugins();

    /// <summary>把一只插件从回收站恢复并启用；返回的 Task 在 UI 线程上完成。</summary>
    Task<UiCommandResult> RestorePluginAsync(string dirName);

    /// <summary>彻底删除回收站里的一只（页面负责先确认）；返回的 Task 在 UI 线程上完成。</summary>
    Task<UiCommandResult> PurgePluginAsync(string dirName);

    /// <summary>应用设置。返回 null = 成功；否则是失败或部分失败的原因。</summary>
    string? ApplySettings(double idleFrameBudgetMs, bool startWithWindows, string material, string theme);

    /// <summary>改宠物窗口的像素边长（64..256）。返回 null = 成功；否则是给用户看的原因。</summary>
    string? SetPetSize(int size);

    /// <summary>
    /// 换界面语言（写入设置并立即生效，不重启）。返回 null = 成功；否则是给用户看的原因。
    /// 语言不塞进 <see cref="ApplySettings"/>：它换的是整张文案表并重建面板，
    /// 与「材质/亮度」的生效路径和失败面都不同，混在一个参数里会让两条路径互相污染。
    /// </summary>
    string? SetLanguage(string language);

    /// <summary>打开日志目录。</summary>
    void OpenLogs();

    /// <summary>导出诊断包（owner 用于模态进度框）。</summary>
    void ExportDiagnostics(IntPtr owner);

    /// <summary>打开审计流水。</summary>
    void OpenAudit();
}
