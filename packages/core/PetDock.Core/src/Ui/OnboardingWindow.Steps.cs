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
    /// <summary>唯一一页：语言选择 + 使用提示（只持久化选择，文案不随选择变化）。</summary>
    private Panel BuildLanguageStep(bool hasPet)
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

        // 提示直接排在同一页：第二页只有一个空框和一句「透明区能点穿」，
        // 用户已经问过「第二个选择框是干嘛的」——它是示意框，不是可选项，删掉。
        var tip = new Label
        {
            Text = I18n.Get("ui.onboarding.through.tip"),
            Dock = DockStyle.Top,
            Height = Theme.Dip(this, 56),
            AutoSize = false,
            Font = Theme.FontFor(this, TypeRole.Body),
            ForeColor = Theme.TextDim,
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

        var gap = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Dip(this, Theme.SpaceMd),
            BackColor = Color.Transparent,
        };

        step.Controls.Add(tip);
        if (!hasPet)
        {
            // 没有视图实现时提示不成立，换成一句「当前没有可显示的宠物」。
            var notice = new Label
            {
                Text = I18n.Get(NoPetKey),
                Dock = DockStyle.Top,
                Height = Theme.Dip(this, 32),
                Font = Theme.FontFor(this, TypeRole.Body),
                ForeColor = Theme.TextDim,
                BackColor = Color.Transparent,
            };
            step.Controls.Add(notice);
        }
        else
        {
            step.Controls.Add(caption);
        }

        step.Controls.Add(gap);
        step.Controls.Add(_en);
        step.Controls.Add(_zh);
        step.Controls.Add(note);
        step.Controls.Add(title);
        return step;
    }

}
