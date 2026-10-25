// 首次运行引导窗（RFC-16 §3.3 的三步控件树）。
//
// 为什么引导是**窗**而不是面板的一页：docs/18 §2.5 把主控面板的四页钉死了（判据 ui-panel-pages），
// 而引导是**一次性**的、不属于常驻界面的四页。托盘也不能加第六项（docs/18 §1 被 ui-tray-items 钉死）。
//
// 线程：全部在 UI 线程。窗以模态方式打开（ShowDialog），模态窗自带消息循环，
// 宿主的滴答器在模态期间**仍会触发** —— 所以宿主必须知道引导窗开着（见 UiHost.Onboarding.cs），
// 期间不泵授权询问（否则两个模态窗叠在一起）。
//
// 失败一律走页内内联错误条，不弹模态对话框（docs/18 §5：失败不弹模态框）。
//
// 状态一律记在**普通字段**里，公开读数从字段读（不读控件的 Visible）：
// 窗被关掉之后判据仍要读它的结果（RFC-16 §3.5）。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui;

/// <summary>首次运行引导窗：三步（语言 → 放置示例宠物 → 穿透说明）。</summary>
internal sealed partial class OnboardingWindow : Form
{
    /// <summary>放置示例宠物（宿主侧执行：走既有的 PetImportService.Import）。返回 null = 成功，否则是错误文本。</summary>
    internal delegate string? PlaceSample();

    /// <summary>
    /// 写设置（宿主侧执行：读出 → 改一个字段 → 整份写回）。
    /// language = null 表示不改语言；complete = true 表示写一次性标记。
    /// 返回 false = 写失败，error 有值。
    /// </summary>
    internal delegate bool WriteSettings(string? language, bool complete, out string error);

    // 常量是**键名**，不是文案（RFC-42 §3.4 第 3 条）：判据引用键，文案将来在 assets/i18n 的表里；
    // 这一片还没有表，I18n.Get 原样返回键，缺键就显示键名本身。

    /// <summary>第 2 步必须原样出现的标注的键（docs/18 §4 的原话在资源表里，不改写）。</summary>
    public const string SampleDisclaimerKey = "ui.onboarding.sample.disclaimer";

    /// <summary>第 3 步必须原样出现的说明的键。</summary>
    public const string ThroughCaptionKey = "ui.onboarding.through.caption";

    /// <summary>放置成功后的回执的键。</summary>
    public const string PlacedKey = "ui.onboarding.sample.placed";

    /// <summary>没有视图实现时第 2/3 步被跳过时的页内说明的键。</summary>
    public const string NoPetKey = "ui.onboarding.no-pet";

    /// <summary>发行物里没带示例素材时的页内说明的键。</summary>
    public const string NoSampleKey = "ui.onboarding.no-sample";

    /// <summary>「下一步」与「完成」两个按钮文案的键（最后一步换成完成）。</summary>
    public const string NextKey = "ui.onboarding.next";
    public const string FinishKey = "ui.onboarding.finish";

    private readonly PlaceSample _placeSample;
    private readonly WriteSettings _writeSettings;

    private readonly Panel[] _steps;
    private readonly Label _error = new();
    private readonly Label _placed = new();
    private readonly RadioButton _zh = new() { Text = "简体中文 (zh-CN)", Name = "onboarding-language-zh" };
    private readonly RadioButton _en = new() { Text = "English (en-US)", Name = "onboarding-language-en" };
    private readonly ThroughHintPanel _hint = new() { Name = "onboarding-animation" };
    private readonly Button _next = new() { Text = "下一步", Name = "onboarding-next", Width = 96, Height = 30 };
    private readonly Button _skip = new() { Text = "跳过引导", Name = "onboarding-skip", Width = 96, Height = 30 };
    private readonly Button _place = new() { Text = "放置示例宠物", Name = "onboarding-place", Width = 120, Height = 30 };

    private int _index;
    private bool _samplePlaced;
    private bool _markerWritten;
    private bool _finished;
    private bool _errorShown;
    private string _errorText = string.Empty;
    private string _markerError = string.Empty;
    private string _sampleStatus = string.Empty;

    public OnboardingWindow(PlaceSample placeSample, WriteSettings writeSettings, bool hasPet)
    {
        _placeSample = placeSample;
        _writeSettings = writeSettings;

        Text = "PetDock — 首次运行引导";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 380);
        Font = SystemFonts.MessageBoxFont;

        _steps = hasPet
            ? new[] { BuildLanguageStep(), BuildSampleStep(), BuildThroughStep() }
            : new[] { BuildLanguageStep() };

        BuildBody(hasPet);
        BuildFooter();

        _index = 0;
        ShowStep();
    }

    /// <summary>当前是第几步（0 起）。</summary>
    public int StepIndex => _index;

    /// <summary>一共几步（有视图实现时三步，没有时一步）。</summary>
    public int StepCount => _steps.Length;

    /// <summary>用户选的语言（读单选框，不读界面文案 —— 文案不随选择变化）。</summary>
    public string SelectedLanguage => _en.Checked ? "en-US" : "zh-CN";

    /// <summary>示例是否已放置成功。</summary>
    public bool SamplePlaced => _samplePlaced;

    /// <summary>放置结果或跳过原因的一行说明。</summary>
    public string SampleStatusText => _sampleStatus;

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

    /// <summary>示意动画是否在跑（计时器已启动）。</summary>
    public bool AnimationRunning => _hint.Running;

    /// <summary>示意动画已推进的帧数。</summary>
    public int AnimationFrameCount => _hint.FrameCount;

    /// <summary>「下一步」按钮当前文案（来自 <see cref="NextButtonKey"/>，不随语言选择变化）。</summary>
    public string NextButtonText => _next.Text;

    /// <summary>「下一步」按钮当前文案的键（判据断言这个键，不断言中文字面量）。</summary>
    public string NextButtonKey => _index == _steps.Length - 1 ? FinishKey : NextKey;

    /// <summary>
    /// 某一步里所有标签的文案（判据读它断言 docs/18 §4 的两句话**原样出现**在界面上，
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

    /// <summary>放置示例宠物（点按钮或判据调用都一样）。</summary>
    public void TryPlaceSample()
    {
        ClearError();
        string? error;
        try
        {
            error = _placeSample();
        }
        catch (Exception ex)
        {
            error = $"放置示例宠物失败：{ex.Message}";
        }

        if (error is null)
        {
            _samplePlaced = true;
            _sampleStatus = I18n.Get(PlacedKey);
            _placed.Text = _sampleStatus;
            return;
        }

        // 失败：内联错误条 + 旧宠物原样保留（宿主侧不换源）。
        _sampleStatus = error;
        ShowError(error);
    }

    /// <summary>跳过引导（跳过也要写一次性标记 —— 否则每次启动都弹）。</summary>
    public void Skip() => Complete(completeOnboarding: true, saveLanguage: false);

    /// <summary>完成引导。</summary>
    public void Finish() => Complete(completeOnboarding: true, saveLanguage: true);

    /// <summary>关窗时停掉示意动画的计时器。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hint.StopAnimation();
        }

        base.Dispose(disposing);
    }

    /// <summary>第 1 步：只写语言，不写标记。</summary>
    private bool SaveLanguage()
    {
        ClearError();
        if (_writeSettings(SelectedLanguage, complete: false, out string error))
        {
            return true;
        }

        _markerError = error;
        ShowError($"{error}（语言选择未能保存）");
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
            ShowError($"{error}（引导完成状态未能保存，下次启动还会出现）");
            return;
        }

        _markerWritten = true;
        _finished = true;
        Close();
    }

    private void BuildBody(bool hasPet)
    {
        _error.Dock = DockStyle.Top;
        _error.Height = 44;
        _error.Name = "onboarding-error";
        _error.BackColor = Color.FromArgb(0xFF, 0xFD, 0xE9, 0xD9);
        _error.ForeColor = Color.FromArgb(0xFF, 0x8A, 0x3D, 0x00);
        _error.Visible = false;
        _error.Text = string.Empty;

        var stepsHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };

        if (!hasPet)
        {
            // 没有视图实现：第 2/3 步不出现，只在第 1 步里说明当前没有可显示的宠物。
            var notice = new Label
            {
                Text = I18n.Get(NoPetKey),
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = Color.FromArgb(0xFF, 0x66, 0x66, 0x66),
            };
            _steps[0].Controls.Add(notice);
        }

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
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };

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

        // 示意动画只在第 3 步跑：别的步骤上它没有意义，白烧 CPU。
        if (_steps[_index].Name == "onboarding-step-through")
        {
            _hint.StartAnimation();
        }
        else
        {
            _hint.StopAnimation();
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
