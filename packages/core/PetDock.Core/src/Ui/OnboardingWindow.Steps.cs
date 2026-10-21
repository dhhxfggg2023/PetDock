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
            Text = "选择界面语言",
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font(Font, FontStyle.Bold),
        };
        var note = new Label
        {
            Text = "这一步只保存你的选择。当前版本的界面文案仍是中文，尚未本地化。",
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
            Text = "放置一只示例宠物",
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font(Font, FontStyle.Bold),
        };
        var note = new Label
        {
            Text = "示例宠物会复制到你的数据区，之后你可以用面板里的「导入素材包…」换成自己的。",
            Dock = DockStyle.Top,
            Height = 44,
        };
        var disclaimer = new Label
        {
            Text = SampleDisclaimerText,
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
            Text = "透明区域可以点穿",
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font(Font, FontStyle.Bold),
        };
        var caption = new Label
        {
            Text = ThroughCaptionText,
            Dock = DockStyle.Top,
            Height = 28,
        };
        var tip = new Label
        {
            Text = "试着点一下宠物边角上什么都没有的地方 —— 下面那个窗口会接到你的点击。",
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
