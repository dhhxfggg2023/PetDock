// 主控面板窗口（2026-10-28 WPF 重构）：WinForms 只做窗口外壳与 ElementHost，
// 界面本体是 WPF 的 UiWorkspaceView，页面全部来自 IUiPlugin。
//
// 为什么保留一个 Form：托盘/单实例/模态 owner 仍走 WinForms 的消息泵；
// 另起一个 WPF Application 会让两个消息循环与两套 DPI 感知叠在一起。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using PetDock.Core.Ui.Design;

namespace PetDock.Core.Ui;

/// <summary>内置页的稳定 id（自检按 id 断言，不按标题文字）。</summary>
internal static class PanelPages
{
    public const string Pets = "pets";
    public const string Plugins = "plugins";
    public const string Settings = "settings";

    public static readonly string[] All = { Pets, Plugins, Settings };
}

internal sealed class ControlPanel : Form
{
    /// <summary>诊断入口的稳定 op 名（宿主侧按它分派）。</summary>
    public const string DiagnosticOpOpenLogs = "open-logs";
    public const string DiagnosticOpExportBundle = "export-bundle";
    public const string DiagnosticOpViewAudit = "view-audit";

    private readonly ElementHost _element;

    public ControlPanel(IUiPageContext context, IReadOnlyList<IUiPage> pages, bool showWindow = true)
    {
        Text = I18n.Get("cp.title");
        Icon = TrayHost.LoadIcon();
        StartPosition = FormStartPosition.CenterScreen;
        Theme.Apply(this);
        Width = Theme.Dip(this, 1040);
        Height = Theme.Dip(this, 680);
        MinimumSize = new Size(Theme.Dip(this, 820), Theme.Dip(this, 520));
        ShowInTaskbar = true;

        Workspace = new UiWorkspaceView(pages, context);
        _element = new ElementHost
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Child = Workspace,
        };
        Controls.Add(_element);
    }

    /// <summary>WPF 壳（自检读 CurrentPage/PageIds，截图走它的 RenderTargetBitmap）。</summary>
    public UiWorkspaceView Workspace { get; }

    /// <summary>当前页 id。</summary>
    public string? CurrentPage => Workspace.CurrentPage;

    /// <summary>导航里的页 id，按显示顺序。</summary>
    public IReadOnlyList<string> PageIds => Workspace.PageIds;

    public void ShowPage(string id) => Workspace.ShowPage(id);

    public void SetStatus(string text, UiStatusKind kind = UiStatusKind.Info) => Workspace.SetStatus(text, kind);

    /// <summary>清状态栏（换页或页面自己决定清掉时调）。</summary>
    public void ClearStatus() => Workspace.ClearStatus();
}
