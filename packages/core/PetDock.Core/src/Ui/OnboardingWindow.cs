// 首次运行引导窗（RFC-16 §3.3）。
//
// 为什么引导是**窗**而不是面板的一页：原规格 18 §2.5 把主控面板的页钉死了（判据 ui-panel-pages），
// 而引导是**一次性**的、不属于常驻界面。托盘也不能加第六项（原规格 18 §1 被 ui-tray-items 钉死）。
//
// 2026-10-28：语言选择从引导里搬进设置页（默认中文）。引导只剩一页「使用提示」，
// 不再写语言 —— 用户的语言是设置页的事实，引导没有资格替用户改它。
//
// 线程：全部在 UI 线程。窗以模态方式打开（ShowDialog），模态窗自带消息循环，
// 宿主的滴答器在模态期间**仍会触发** —— 所以宿主必须知道引导窗开着（见 UiHost.Onboarding.cs），
// 期间不泵授权询问（否则两个模态窗叠在一起）。
//
// 失败一律走页内内联错误条，不弹模态对话框（原规格 18 §5：失败不弹模态框）。
//
// 状态一律记在**普通字段**里，公开读数从字段读（不读控件的 Visible）：
// 窗被关掉之后判据仍要读它的结果（RFC-16 §3.5）。
using System;
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;

namespace PetDock.Core.Ui;

/// <summary>首次运行引导窗：一页（使用提示）。</summary>
internal sealed partial class OnboardingWindow : PetDockDialog
{
    /// <summary>
    /// 写设置（宿主侧执行：读出 → 改一个字段 → 整份写回）。
    /// complete = true 表示写一次性标记。返回 false = 写失败，error 有值。
    /// </summary>
    internal delegate bool WriteSettings(bool complete, out string error);

    // 常量是**键名**，不是文案（RFC-42 §3.4 第 3 条）：判据引用键，文案在 assets/i18n 的表里。

    /// <summary>页面里必须原样出现的说明的键。</summary>
    public const string ThroughCaptionKey = "ui.onboarding.through.caption";

    /// <summary>没有视图实现时的页内说明的键。</summary>
    public const string NoPetKey = "ui.onboarding.no-pet";

    /// <summary>唯一按钮的文案键（一页只有「完成」）。</summary>
    public const string FinishKey = "ui.onboarding.finish";

    private readonly WriteSettings _writeSettings;

    private readonly Panel[] _steps;
    private readonly Label _error = new();
    private readonly ThemedButton _next = new() { Text = I18n.Get(FinishKey), Name = "onboarding-next", Primary = true };
    private readonly ThemedButton _skip = new() { Text = I18n.Get("ui.onboarding.skip"), Name = "onboarding-skip" };

    private int _index;
    private bool _markerWritten;
    private bool _finished;
    private bool _errorShown;
    private string _errorText = string.Empty;
    private string _markerError = string.Empty;

    public OnboardingWindow(WriteSettings writeSettings, bool hasPet)
    {
        _writeSettings = writeSettings;

        Text = I18n.Get("ui.onboarding.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Theme.Apply(this);
        // 高度按这一页的正文定（2026-10-28 截图实测）：标题 36 + 说明 52 + 间隔 16 + 屏下列 32 + 入口 56
        // = 192，上下内边距各 24，按钮条 36 —— 多出来的空白会把「还没画完」读成界面坏了。
        ClientSize = new Size(Theme.Dip(this, 560), Theme.Dip(this, 280));

        _steps = new[] { BuildUsageStep(hasPet) };

        BuildBody();
        BuildFooter();

        _index = 0;
        ShowStep();
    }

    /// <summary>当前是第几步（0 起）。</summary>
    public int StepIndex => _index;

    /// <summary>一共几步（恒为 1：使用提示一页看完）。</summary>
    public int StepCount => _steps.Length;

    /// <summary>内联错误条当前是否可见。</summary>
    public bool ErrorShown => _errorShown;

    /// <summary>内联错误条的文本。</summary>
    public string ErrorText => _errorText;

    /// <summary>一次性标记是否已写成功。</summary>
    public bool MarkerWritten => _markerWritten;

    /// <summary>写标记失败时的原因（空 = 没有失败）。</summary>
    public string MarkerError => _markerError;

    /// <summary>引导是否已经走完（完成或跳过）。</summary>
    public bool Finished => _finished;

    /// <summary>按钮当前文案（来自 <see cref="NextButtonKey"/>）。</summary>
    public string NextButtonText => _next.Text;

    /// <summary>按钮当前文案的键（判据断言这个键，不断言中文字面量）。</summary>
    public string NextButtonKey => FinishKey;

    /// <summary>
    /// 某一步里所有标签的文案（判据读它断言说明**原样出现**在界面上，
    /// 而不是出现在某个常量里 —— 读常量只能证明常量没被改，证明不了文案真的在窗上）。
    /// </summary>
    public string CollectStepText(int index)
    {
        if (index < 0 || index >= _steps.Length)
        {
            return string.Empty;
        }

        var text = new System.Text.StringBuilder();
        CollectText(_steps[index], text);
        return text.ToString();
    }

    /// <summary>某一步里的单选框数量（判据用：引导不再让用户选语言，这里必须是 0）。</summary>
    public int RadioButtonCount(int index)
    {
        if (index < 0 || index >= _steps.Length)
        {
            return 0;
        }

        int count = 0;
        CollectRadios(_steps[index], ref count);
        return count;
    }

    private static void CollectRadios(Control parent, ref int count)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is RadioButton)
            {
                count++;
            }

            CollectRadios(child, ref count);
        }
    }

    private static void CollectText(Control parent, System.Text.StringBuilder text)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Label label && !string.IsNullOrEmpty(label.Text))
            {
                text.Append(label.Text).Append('\n');
            }

            CollectText(child, text);
        }
    }

    /// <summary>下一步（一页只有「完成」）。</summary>
    public void GoNext() => Finish();

    /// <summary>跳过引导（跳过也要写一次性标记 —— 否则每次启动都弹）。</summary>
    public void Skip() => Complete(completeOnboarding: true);

    /// <summary>完成引导。</summary>
    public void Finish() => Complete(completeOnboarding: true);

    /// <summary>
    /// 收尾：把一次性标记写出去。
    /// 写失败**不写标记**也不关窗：内联提示「下次启动还会出现」，让用户能立刻重试。
    /// </summary>
    private void Complete(bool completeOnboarding)
    {
        ClearError();
        if (!_writeSettings(completeOnboarding, out string error))
        {
            _markerError = error;
            _markerWritten = false;
            ShowError(I18n.Get("ui.onboarding.error.completeSave", error));
            return;
        }

        _markerWritten = true;
        _finished = true;
        Close();
    }

    private void BuildBody()
    {
        _error.Dock = DockStyle.Top;
        _error.Height = Theme.Dip(this, 48);
        _error.Name = "onboarding-error";
        _error.Font = Theme.FontFor(this, TypeRole.Body);
        _error.BackColor = Theme.BadSurface;
        _error.ForeColor = Theme.Bad;
        _error.Padding = new Padding(
            Theme.Dip(this, Theme.SpaceMd), Theme.Dip(this, Theme.SpaceXs), Theme.Dip(this, Theme.SpaceMd), Theme.Dip(this, Theme.SpaceXs));
        _error.Visible = false;
        _error.Text = string.Empty;

        var stepsHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Padding = new Padding(Theme.Dip(this, Theme.SpaceMd)),
        };

        foreach (Panel step in _steps)
        {
            step.Dock = DockStyle.Fill;
            step.Visible = false;
            stepsHost.Controls.Add(step);
        }

        Controls.Add(stepsHost);
        Controls.Add(_error);
    }

    private void BuildFooter()
    {
        FlowLayoutPanel footer = Theme.DialogButtonBar(this);

        _next.FitToText();
        _skip.FitToText();

        _skip.Click += (_, _) => Skip();
        _next.Click += (_, _) => GoNext();
        footer.Controls.Add(_next);
        footer.Controls.Add(_skip);
        Controls.Add(footer);
    }

    private void ShowStep()
    {
        for (int i = 0; i < _steps.Length; i++)
        {
            _steps[i].Visible = i == _index;
        }

        _next.Text = I18n.Get(NextButtonKey);
    }

    private void ShowError(string message)
    {
        _errorText = message;
        _errorShown = true;
        _error.Text = message;
        _error.Visible = true;
    }

    private void ClearError()
    {
        _errorText = string.Empty;
        _errorShown = false;
        _error.Text = string.Empty;
        _error.Visible = false;
    }
}
