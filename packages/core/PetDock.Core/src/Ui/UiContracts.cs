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

    /// <summary>写底部状态栏（页面级提示，不弹模态框）。</summary>
    void SetStatus(string text);

    /// <summary>取一条本地化文案；缺键返回键名本身（RFC-42 §3.2）。</summary>
    string T(string key);

    /// <summary>带一个占位符的本地化文案。</summary>
    string T(string key, object arg0);

    /// <summary>导入素材包目录。返回 false 时 message 是给用户看的原因。</summary>
    bool ImportPet(string sourceDir, out string message);

    /// <summary>移除一只宠物（确认框由页面自己弹，宿主只执行）。</summary>
    string? RemovePet(string petId);

    /// <summary>对某个插件发起授权复核（弹窗在宿主侧）。</summary>
    void AuthorizePlugin(string pluginId);

    /// <summary>翻转某个插件的启用/停用（重启生效）。</summary>
    void TogglePluginDisabled(string pluginId);

    /// <summary>安装插件（目录或 zip；enable = 安装后立即启用）。返回 null = 成功。</summary>
    string? InstallPlugin(string source, bool enable);

    /// <summary>删除用户安装的插件。返回 null = 成功。</summary>
    string? RemovePlugin(string pluginId);

    /// <summary>应用设置。返回 null = 成功；否则是失败或部分失败的原因。</summary>
    string? ApplySettings(double idleFrameBudgetMs, bool startWithWindows, string material, string theme);

    /// <summary>打开日志目录。</summary>
    void OpenLogs();

    /// <summary>导出诊断包（owner 用于模态进度框）。</summary>
    void ExportDiagnostics(IntPtr owner);

    /// <summary>打开审计流水。</summary>
    void OpenAudit();
}
