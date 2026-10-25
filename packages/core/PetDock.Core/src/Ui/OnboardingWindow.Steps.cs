// 引导窗的三步控件树（拆出主文件的原因：D-03c 的单文件 400 行上限）。
//
// 三步的排版纪律：Dock=Top 的控件是**后加的在上**，所以顺序要倒着写
// （先加的在下、先加的先占位 —— WinForms 的 Dock 按添加顺序从下往上排）。
using System.Drawing;
using System.Windows.Forms;

namespace PetDock.Core.Ui;

internal sealed partial class OnboardingWindow
{
    /// <summary>第 1 步：语言选择（只持久化选择，文案不随选择变化）。</summary>
    private Panel BuildLanguageStep()
    {
        var step = new Panel { Name = "onboarding-step-language" };
        var title = new Label
        {
            Text = I18n.Get("ui.onboarding.language.title"),
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font(Font, FontStyle.Bold),
        };
        var note = new Label
        {
            Text = I18n.Get("ui.onboarding.language.note"),
            Dock = DockStyle.Top,
            Height = 44,
            ForeColor = Color.FromArgb(0xFF, 0x66, 0x66, 0x66),
        };

        _zh.Checked = true;
        _zh.Dock = DockStyle.Top;
        _zh.Height = 26;
        _en.Dock = DockStyle.Top;
        _en.Height = 26;

        step.Controls.Add(_en);
        step.Controls.Add(_zh);
        step.Controls.Add(note);
        step.Controls.Add(title);
        return step;
    }

    /// <summary>第 2 步：放置示例宠物（标注必须原样出现）。</summary>
    private Panel BuildSampleStep()
    {
        var step = new Panel { Name = "onboarding-step-sample" };
        var title = new Label
        {
            Text = I18n.Get("ui.onboarding.sample.title"),
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font(Font, FontStyle.Bold),
        };
        var note = new Label
        {
            Text = I18n.Get("ui.onboarding.sample.note"),
            Dock = DockStyle.Top,
            Height = 44,
        };
        var disclaimer = new Label
        {
            Text = I18n.Get(SampleDisclaimerKey),
            Dock = DockStyle.Top,
            Height = 26,
            ForeColor = Color.FromArgb(0xFF, 0x8A, 0x3D, 0x00),
        };

        _placed.Dock = DockStyle.Top;
        _placed.Height = 26;
        _placed.Text = string.Empty;

        _place.Click += (_, _) => TryPlaceSample();
        _place.Dock = DockStyle.Bottom;

        step.Controls.Add(_place);
        step.Controls.Add(_placed);
        step.Controls.Add(disclaimer);
        step.Controls.Add(note);
        step.Controls.Add(title);
        return step;
    }

    /// <summary>第 3 步：穿透说明（说明必须原样出现 + 示意动画）。</summary>
    private Panel BuildThroughStep()
    {
        var step = new Panel { Name = "onboarding-step-through" };
        var title = new Label
        {
            Text = I18n.Get("ui.onboarding.through.title"),
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font(Font, FontStyle.Bold),
        };
        var caption = new Label
        {
            Text = I18n.Get(ThroughCaptionKey),
            Dock = DockStyle.Top,
            Height = 28,
        };
        var tip = new Label
        {
            Text = I18n.Get("ui.onboarding.through.tip"),
            Dock = DockStyle.Top,
            Height = 28,
        };

        _hint.Dock = DockStyle.Fill;

        step.Controls.Add(_hint);
        step.Controls.Add(tip);
        step.Controls.Add(caption);
        step.Controls.Add(title);
        return step;
    }
}
