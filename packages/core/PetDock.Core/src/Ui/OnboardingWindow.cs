// 首次运行引导窗（RFC-16 §3.3 的三步控件树）。
//
// 为什么引导是**窗**而不是面板的一页：原规格 18 §2.5 把主控面板的四页钉死了（判据 ui-panel-pages），
// 而引导是**一次性**的、不属于常驻界面的四页。托盘也不能加第六项（原规格 18 §1 被 ui-tray-items 钉死）。
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

/// <summary>首次运行引导窗：一页（选语言 + 穿透提示）。</summary>
internal sealed partial class OnboardingWindow : PetDockDialog
{
    /// <summary>
    /// 写设置（宿主侧执行：读出 → 改一个字段 → 整份写回）。
    /// language = null 表示不改语言；complete = true 表示写一次性标记。
    /// 返回 false = 写失败，error 有值。
    /// </summary>
    internal delegate bool WriteSettings(string? language, bool complete, out string error);

    // 常量是**键名**，不是文案（RFC-42 §3.4 第 3 条）：判据引用键，文案将来在 assets/i18n 的表里；
    // 这一片还没有表，I18n.Get 原样返回键，缺键就显示键名本身。

    /// <summary>页面里必须原样出现的说明的键。</summary>
    public const string ThroughCaptionKey = "ui.onboarding.through.caption";

    /// <summary>没有视图实现时的页内说明的键。</summary>
    public const string NoPetKey = "ui.onboarding.no-pet";

    /// <summary>唯一按钮的文案键（一页只有「完成」）。</summary>
    public const string FinishKey = "ui.onboarding.finish";

    private readonly WriteSettings _writeSettings;

    private readonly Panel[] _steps;
    private readonly Label _error = new();
    private readonly ThemedRadio _zh = new() { Text = I18n.Get("ui.onboarding.language.zh"), Name = "onboarding-language-zh" };
    private readonly ThemedRadio _en = new() { Text = I18n.Get("ui.onboarding.language.en"), Name = "onboarding-language-en" };
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
        // 高度按「三步里最长的那一步」定：第一、二步内容少，留太多空白会读成界面没画完
        // （2026-10-28 截图实测）。
        ClientSize = new Size(Theme.Dip(this, 560), Theme.Dip(this, 336));

        // 2026-10-28：删掉「放置示例宠物」与独立的穿透说明页。用户问的「第二个选择框」
        // 就是那页里的大示意框 —— 它不是选项，删掉；提示并到语言页，一次看完。
        _steps = new[] { BuildLanguageStep(hasPet) };

        BuildBody();
        BuildFooter();

        _index = 0;
        ShowStep();
    }

    /// <summary>当前是第几步（0 起）。</summary>
    public int StepIndex => _index;

    /// <summary>一共几步（恒为 1：语言与使用提示同页）。</summary>
    public int StepCount => _steps.Length;

    /// <summary>用户选的语言（读单选框，不读界面文案 —— 文案不随选择变化）。</summary>
    public string SelectedLanguage => _en.Checked ? "en-US" : "zh-CN";

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

    /// <summary>按钮当前文案（来自 <see cref="NextButtonKey"/>，不随语言选择变化）。</summary>
    public string NextButtonText => _next.Text;

    /// <summary>按钮当前文案的键（判据断言这个键，不断言中文字面量）。</summary>
    public string NextButtonKey => FinishKey;

    /// <summary>
    /// 某一步里所有标签的文案（判据读它断言 原规格 18 §4 的两句话**原样出现**在界面上，
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

    /// <summary>选语言（自检与判据驱动用；等价于点单选框）。</summary>
    public void SelectLanguage(string code)
    {
        bool english = string.Equals(code, "en-US", StringComparison.Ordinal);
        _en.Checked = english;
        _zh.Checked = !english;
    }

    /// <summary>下一步（最后一步上是「完成」）。</summary>
    public void GoNext()
    {
        // 第 1 步的「下一步」就把语言写下去（RFC-16 §3.3）：
        // 用户选了语言却因为中途关窗而丢掉选择，是没道理的事。
        if (_index == 0 && !SaveLanguage())
        {
            return;
        }

        if (_index < _steps.Length - 1)
        {
            _index++;
            ShowStep();
            return;
        }

        Finish();
    }

    /// <summary>跳过引导（跳过也要写一次性标记 —— 否则每次启动都弹）。</summary>
    public void Skip() => Complete(completeOnboarding: true, saveLanguage: false);

    /// <summary>完成引导。</summary>
    public void Finish() => Complete(completeOnboarding: true, saveLanguage: true);

    /// <summary>第 1 步：只写语言，不写标记。</summary>
    private bool SaveLanguage()
    {
        ClearError();
        if (_writeSettings(SelectedLanguage, complete: false, out string error))
        {
            return true;
        }

        _markerError = error;
        ShowError(I18n.Get("ui.onboarding.error.languageSave", error));
        return false;
    }

    /// <summary>
    /// 收尾：把一次性标记（以及语言）一起写出去。
    /// 写失败**不写标记**也不关窗：内联提示「下次启动还会出现」，让用户能立刻重试。
    /// </summary>
    private void Complete(bool completeOnboarding, bool saveLanguage)
    {
        ClearError();
        if (!_writeSettings(saveLanguage ? SelectedLanguage : null, completeOnboarding, out string error))
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
