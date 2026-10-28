// 引导窗的控件树（拆出主文件的原因：D-03c 的单文件 400 行上限）。
//
// 一页的排版纪律：Dock=Top 的控件是**后加的在上**，所以顺序要倒着写
// （先加的在下、先加的先占位 —— WinForms 的 Dock 按添加顺序从下往上排）。
//
// 2026-10-28：语言选择搬去设置页之后，这一页只讲两件事「入口在哪」与「透明区能点穿」。
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;

namespace PetDock.Core.Ui;

internal sealed partial class OnboardingWindow
{
    /// <summary>唯一一页：使用提示（入口 + 透明区说明 + 设置在哪）。</summary>
    private Panel BuildUsageStep(bool hasPet)
    {
        var step = new Panel { Name = "onboarding-step-usage", BackColor = Theme.Bg };
        var title = new Label
        {
            Text = I18n.Get("ui.onboarding.usage.title"),
            Dock = DockStyle.Top,
            Height = Theme.Dip(this, 36),
            Font = Theme.FontFor(this, TypeRole.Heading),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
        };
        var note = new Label
        {
            Text = I18n.Get("ui.onboarding.usage.note"),
            Dock = DockStyle.Top,
            Height = Theme.Dip(this, 52),
            Font = Theme.FontFor(this, TypeRole.Body),
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
        };

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
        step.Controls.Add(note);
        step.Controls.Add(title);
        return step;
    }
}
