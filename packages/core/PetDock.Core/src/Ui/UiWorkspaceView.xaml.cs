// WPF 主壳：只管导航、页头、状态条与主题资源；页面全部来自 IUiPlugin（2026-10-28）。
// 这是「一切皆可插件」的壳侧半边：它不知道 pets/plugins/settings 的存在，
// 只按 IUiPage.Order 排序、按 Id 切页、把 IUiPageContext 交给页面。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using PetDock.Core.Ui.Design;
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;
using Orientation = System.Windows.Controls.Orientation;

namespace PetDock.Core.Ui;

public partial class UiWorkspaceView : UserControl
{
    private readonly IReadOnlyList<IUiPage> _pages;
    private readonly IUiPageContext _context;
    private readonly Dictionary<string, FrameworkElement> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RadioButton> _nav = new(StringComparer.Ordinal);

    private string? _current;

    /// <summary>进度条旁边「取消」按钮要调的动作（宿主设置；null = 没有长任务）。</summary>
    private UiCommand? _cancelWork;

    public UiWorkspaceView(IReadOnlyList<IUiPage> pages, IUiPageContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _pages = (pages ?? Array.Empty<IUiPage>())
            .OrderBy(p => p.Order)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToArray();

        InitializeComponent();
        BrandSub.Text = I18n.Get("cp.brand.tagline");
        VersionText.Text = I18n.Get(
            "ui.workspace.version",
            typeof(UiWorkspaceView).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        ApplyPalette();
        BuildNav();

        if (_pages.Count == 0)
        {
            TitleText.Text = I18n.Get("ui.workspace.empty.title");
            NoteText.Text = I18n.Get("ui.workspace.empty.note");
            StatusText.Text = I18n.Get("ui.workspace.empty.status");
        }
        else
        {
            ShowPage(_pages[0].Id);
        }

        _context.Changed += OnContextChanged;
        Unloaded += (_, _) => _context.Changed -= OnContextChanged;
    }

    /// <summary>当前页 id（自检读它）。</summary>
    public string? CurrentPage => _current;

    /// <summary>导航里的页 id，按显示顺序。</summary>
    public IReadOnlyList<string> PageIds => _pages.Select(p => p.Id).ToArray();

    /// <summary>状态条文字（自检读它，证明页面 SetStatus 真的落到壳上）。</summary>
    public string StatusTextValue => StatusText.Text;

    /// <summary>状态条当前口吻（自检读它，证明 problem 真的换成了红色那档）。</summary>
    public UiStatusKind StatusKind { get; private set; }

    /// <summary>切页；未知 id 抛异常（静默失败会让深链与自检都失去意义）。</summary>
    public void ShowPage(string id)
    {
        IUiPage page = _pages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))
            ?? throw new ArgumentOutOfRangeException(nameof(id), id, "unknown ui page");

        _current = page.Id;
        if (!_views.TryGetValue(id, out FrameworkElement? view))
        {
            view = page.CreateView();
            _views[id] = view;
        }

        PageHost.Content = view;
        TitleText.Text = page.Title;
        NoteText.Text = page.Note;

        // 长任务期间换页不能把「正在做什么」擦掉：进度条还在跑，状态条就必须说明它在跑什么。
        if (!IsWorking)
        {
            ClearStatus();
        }

        if (_nav.TryGetValue(id, out RadioButton? button))
        {
            button.IsChecked = true;
        }
    }

    /// <summary>
    /// 写状态条（页面通过 IUiPageContext.SetStatus 间接触发）。文案一直留着，
    /// 直到换页或页面自己清 —— 页面里「先写结果、再刷新列表」的写法必须让人看得见结果。
    /// </summary>
    public void SetStatus(string text, UiStatusKind kind = UiStatusKind.Info)
    {
        StatusKind = kind;
        StatusText.Text = text ?? string.Empty;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, StatusBrush(kind));
    }

    /// <summary>清状态条。</summary>
    public void ClearStatus()
    {
        StatusKind = UiStatusKind.Info;
        StatusText.Text = string.Empty;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "Pd.TextMuted");
    }

    /// <summary>是否有长任务在跑（自检读它，证明进度条真的出现/收起）。</summary>
    public bool IsWorking => WorkBar.Visibility == Visibility.Visible;

    /// <summary>开始一次长任务：显示不确定进度条与取消按钮，状态条换成「正在做什么」。</summary>
    internal void BeginWork(string text, UiCommand cancel)
    {
        _cancelWork = cancel;
        WorkCancel.Content = I18n.Get("ui.work.cancel");
        WorkCancel.IsEnabled = true;
        WorkBar.IsIndeterminate = true;
        WorkBar.Value = 0;
        WorkBar.Visibility = Visibility.Visible;
        WorkCancel.Visibility = Visibility.Visible;
        SetStatus(text ?? string.Empty);
    }

    /// <summary>换一句阶段进度（总量未知，进度条回到不确定态）。</summary>
    public void SetWorkStage(string text)
    {
        WorkBar.IsIndeterminate = true;
        WorkBar.Value = 0;
        SetStatus(text ?? string.Empty);
    }

    /// <summary>确定态进度（0..1）。文案由宿主拼好（含已复制/总量/百分比），壳只负责画。</summary>
    public void SetWorkProgress(double ratio, string text)
    {
        WorkBar.IsIndeterminate = false;
        WorkBar.Value = Math.Clamp(ratio, 0.0, 1.0) * 100.0;
        SetStatus(text ?? string.Empty);
    }

    /// <summary>进度条是否确定态（自检读它）。</summary>
    public bool WorkProgressKnown => !WorkBar.IsIndeterminate;

    /// <summary>进度条当前百分比（自检读它）。</summary>
    public double WorkPercent => WorkBar.Value;

    /// <summary>结束长任务：收起进度条与取消按钮。状态条留给页面写结果。</summary>
    public void EndWork()
    {
        _cancelWork = null;
        WorkBar.Visibility = Visibility.Collapsed;
        WorkCancel.Visibility = Visibility.Collapsed;
    }

    private void OnCancelWork(object sender, RoutedEventArgs e)
    {
        UiCommand? cancel = _cancelWork;
        if (cancel is null)
        {
            return;
        }

        // 只请求一次：再点也不会把同一个取消重复投递（按钮同时灰掉，给用户一个「收到了」）。
        _cancelWork = null;
        WorkCancel.IsEnabled = false;
        WorkCancel.Content = I18n.Get("ui.work.cancelling");
        cancel();
    }

    /// <summary>状态条颜色：成功走 Good，失败走 Bad，中性走说明文字那档。</summary>
    private static string StatusBrush(UiStatusKind kind) => kind switch
    {
        UiStatusKind.Success => "Pd.Good",
        UiStatusKind.Problem => "Pd.Bad",
        _ => "Pd.TextMuted",
    };

    private void BuildNav()
    {
        foreach (IUiPage page in _pages)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var glyph = new TextBlock
            {
                Text = page.Glyph,
                Width = 26,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 引用而不是拷贝：换色板时这枚图标也要跟着变（拷一份 Brush 就永远停在旧颜色）。
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Pd.TextMuted");
            content.Children.Add(glyph);
            content.Children.Add(new TextBlock
            {
                Text = page.Title,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var button = new RadioButton
            {
                GroupName = "pd-nav",
                Style = (Style)FindResource("Pd.NavButton"),
                Content = content,
                Tag = page.Id,
            };

            // 导航项的内容是自绘的 StackPanel，读屏拿不到标题；显式给一个名字。
            AutomationProperties.SetName(button, page.Title);
            AutomationProperties.SetHelpText(button, page.Note);
            button.Checked += (_, _) => ShowPage(page.Id);
            _nav[page.Id] = button;
            NavHost.Children.Add(button);
        }
    }

    /// <summary>
    /// 把内核色板转成 WPF 资源。页面插件只认 DynamicResource 键，不认 Theme 类型。
    /// 亮/暗切换时**原地**再调一次：资源键名不变，所以同一个窗口、同一页、同一滚动位置都能保留。
    /// </summary>
    internal void ApplyPalette()
    {
        Set("Pd.Bg", Theme.Bg);
        Set("Pd.BgDeep", Theme.BgDeep);
        Set("Pd.Surface", Theme.Surface);
        Set("Pd.SurfaceHover", Theme.SurfaceHover);
        Set("Pd.Line", Theme.Line);
        Set("Pd.Text", Theme.Text);
        Set("Pd.TextDim", Theme.TextDim);
        Set("Pd.TextMuted", Theme.TextMuted);
        Set("Pd.Accent", Theme.Accent);
        Set("Pd.AccentHover", Theme.AccentHover);
        Set("Pd.AccentSoft", Theme.AccentSoft);
        Set("Pd.OnAccent", Theme.OnAccent);
        Set("Pd.Good", Theme.Good);
        Set("Pd.Warn", Theme.Warn);
        Set("Pd.Bad", Theme.Bad);
        Set("Pd.BadSurface", Theme.BadSurface);
        Set("Pd.WarnSurface", Theme.WarnSurface);
        Background = (Brush)Resources["Pd.Bg"];
        Foreground = (Brush)Resources["Pd.Text"];
    }

    private void Set(string key, System.Drawing.Color color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        Resources[key] = brush;
    }

    private void OnContextChanged(object? sender, EventArgs e)
    {
        // 数据变化既不重建页面、也不清状态条：页面在 Changed 里自己刷新绑定（避免输入到一半被清），
        // 而「刚刚那步成没成」正是刷新之后用户最该看到的一句话。
    }
}
