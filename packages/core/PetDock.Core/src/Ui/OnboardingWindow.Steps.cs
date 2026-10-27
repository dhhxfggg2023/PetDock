// 引导窗的三步控件树（拆出主文件的原因：D-03c 的单文件 400 行上限）。
//
// 三步的排版纪律：Dock=Top 的控件是**后加的在上**，所以顺序要倒着写
// （先加的在下、先加的先占位 —— WinForms 的 Dock 按添加顺序从下往上排）。
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;

namespace PetDock.Core.Ui;

internal sealed partial class OnboardingWindow
{
    /// <summary>第 1 步：语言选择（只持久化选择，文案不随选择变化）。</summary>
    private Panel BuildLanguageStep()
    {
        var step = new Panel { Name = "onboarding-step-language", BackColor = Theme.Bg };
        var title = new Label
        {
            Text = I18n.Get("ui.onboarding.language.title"),
            Dock = DockStyle.Top,
            Height = Theme.Dip(this, 36),
            Font = Theme.FontFor(this, TypeRole.Heading),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
        };
        var note = new Label
        {
            Text = I18n.Get("ui.onboarding.language.note"),
            Dock = DockStyle.Top,
            Height = Theme.Dip(this, 52),
            Font = Theme.FontFor(this, TypeRole.Body),
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
        };

        _zh.Checked = true;
        _zh.Dock = DockStyle.Top;
        _en.Dock = DockStyle.Top;
        _zh.FitToText();
        _en.FitToText();

        step.Controls.Add(_en);
        step.Controls.Add(_zh);
        step.Controls.Add(note);
        step.Controls.Add(title);
        return step;
    }

    /// <summary>第 2 步：放置示例宠物（标注必须原样出现）。</summary>
    private Panel BuildSampleStep()
    {
        var step = new Panel { Name = "onboarding-step-sample", BackColor = Theme.Bg };
        var title = new Label
        {
            Text = I18n.Get("ui.onboarding.sample.title"),
            Dock = DockStyle.Top,
            Height = Theme.Dip(this, 36),
            Font = Theme.FontFor(this, TypeRole.Heading),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
        };
        var note = new Label
        {
            Text = I18n.Get("ui.onboarding.sample.note"),
            Dock = DockStyle.Top,
            Height = Theme.Dip(this, 52),
            Font = Theme.FontFor(this, TypeRole.Body),
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
        };
        var disclaimer = new Label
        {
            Text = I18n.Get(SampleDisclaimerKey),
            Dock = DockStyle.Top,
            Height = Theme.Dip(this, 32),
            Font = Theme.FontFor(this, TypeRole.Body),
            ForeColor = Theme.Warn,
            BackColor = Color.Transparent,
        };

        _placed.Dock = DockStyle.Top;
        _placed.Height = Theme.Dip(this, 32);
        _placed.Font = Theme.FontFor(this, TypeRole.Body);
        _placed.ForeColor = Theme.Good;
        _placed.BackColor = Color.Transparent;
        _placed.Text = string.Empty;

        _place.Click += (_, _) => TryPlaceSample();
        _place.FitToText();

        // 宿主条：Dock=Bottom 直接作用在按钮上会把它拉成整行宽的色块
        // （2026-10-28 截图实测），所以按钮在宿主条里按自身尺寸左对齐。
        var placeHost = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Dip(this, 56),
            BackColor = Theme.Bg,
            Name = "onboarding-place-host",
        };
        placeHost.Controls.Add(_place);
        placeHost.Resize += (_, _) =>
            _place.Location = new Point(
                Theme.Dip(placeHost, Theme.SpaceSm),
                Math.Max(0, (placeHost.Height - _place.Height) / 2));

        step.Controls.Add(placeHost);
        step.Controls.Add(_placed);
        step.Controls.Add(disclaimer);
        step.Controls.Add(note);
        step.Controls.Add(title);
        return step;
    }

    /// <summary>第 3 步：穿透说明（说明必须原样出现 + 示意动画）。</summary>
    private Panel BuildThroughStep()
    {
        var step = new Panel { Name = "onboarding-step-through", BackColor = Theme.Bg };
        var title = new Label
        {
            Text = I18n.Get("ui.onboarding.through.title"),
            Dock = DockStyle.Top,
            Height = Theme.Dip(this, 36),
            Font = Theme.FontFor(this, TypeRole.Heading),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
        };
        var caption = new Label
        {
            Text = I18n.Get(ThroughCaptionKey),
            Dock = DockStyle.Top,
            Height = Theme.Dip(this, 32),
            Font = Theme.FontFor(this, TypeRole.Body),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
        };
        var tip = new Label
        {
            Text = I18n.Get("ui.onboarding.through.tip"),
            Dock = DockStyle.Top,
            // 这句话在 100% 下就要折成两行；一行的高度会把第二行裁掉（ui-appearance-text-fits 实测）。
            Height = Theme.Dip(this, 56),
            AutoSize = false,
            Font = Theme.FontFor(this, TypeRole.Body),
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
        };

        _hint.Dock = DockStyle.Fill;

        step.Controls.Add(_hint);
        step.Controls.Add(tip);
        step.Controls.Add(caption);
        step.Controls.Add(title);
        return step;
    }
}
