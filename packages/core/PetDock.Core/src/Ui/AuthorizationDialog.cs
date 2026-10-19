// 授权弹窗（docs/18 §3，RFC-12 §2.1 补的交互细节）。
// 规则来自文档，不是我的设计：拒绝后不允许重试同一能力；「仅本次」只对 Tier 1 有效，
// 因此 Tier 2 的「仅本次」按钮置灰。能力描述照抄 docs/14 §2 的条目，用大白话重述。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PetDock.Core.Ui;

/// <summary>用户对一次授权请求的回答。</summary>
internal enum GrantAnswer
{
    Allow,
    Deny,
    Once,
}

/// <summary>授权弹窗。无状态：每次打开都是新的，答案由调用方落账。</summary>
internal sealed class AuthorizationDialog : Form
{
    private readonly RadioButton _allow;
    private readonly RadioButton _deny;
    private readonly RadioButton _once;
    private readonly Button _ok;

    public AuthorizationDialog(string pluginName, string source, int tier, IReadOnlyList<string> capabilities)
    {
        Text = "PetDock — 能力授权";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 560;
        Height = 430;
        Font = SystemFonts.MessageBoxFont;

        var title = new Label
        {
            Text = $"{pluginName}（{source}）",
            AutoSize = false,
            Height = 24,
            Dock = DockStyle.Top,
            Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold),
            Padding = new Padding(12, 8, 12, 0),
        };

        var intro = new Label
        {
            Text = "这个插件请求以下能力。你可以拒绝其中的任何一项；被拒绝的能力它不会再申请一次。",
            AutoSize = false,
            Height = 40,
            Dock = DockStyle.Top,
            Padding = new Padding(12, 4, 12, 0),
        };

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
        };
        foreach (string capability in capabilities)
        {
            list.Items.Add(PlainTalk(capability));
        }

        if (list.Items.Count == 0)
        {
            list.Items.Add("（这个插件没有请求任何能力）");
        }

        _allow = new RadioButton { Text = "允许（记住这个决定）", AutoSize = true, Checked = true, Name = "Allow" };
        _deny = new RadioButton { Text = "拒绝", AutoSize = true, Name = "Deny" };
        _once = new RadioButton
        {
            Text = tier >= 2 ? "仅本次（Tier 2 不支持：进程外插件按会话授权）" : "仅本次",
            // 「仅本次」仅对 Tier 1 会话有效（docs/18 §3 规则）——不可选的选项要看起来不可选。
            Enabled = tier < 2,
            AutoSize = true,
            Name = "Once",
        };

        var choices = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Bottom,
            Height = 76,
            Padding = new Padding(12, 4, 12, 0),
            WrapContents = false,
        };
        choices.Controls.Add(_allow);
        choices.Controls.Add(_deny);
        choices.Controls.Add(_once);

        _ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 88 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 88 };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(12, 6, 12, 6),
        };
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(cancel);

        var footer = new Label
        {
            Text = "素材与第三方组件的合规说明见 docs/52-legal-licensing.md。",
            AutoSize = false,
            Height = 22,
            Dock = DockStyle.Bottom,
            Padding = new Padding(12, 0, 12, 0),
            ForeColor = SystemColors.GrayText,
        };

        Controls.Add(list);
        Controls.Add(footer);
        Controls.Add(choices);
        Controls.Add(buttons);
        Controls.Add(intro);
        Controls.Add(title);
        AcceptButton = _ok;
        CancelButton = cancel;
    }

    /// <summary>本次回答。对话框被取消时按「拒绝」处理（默认拒绝比默认允许安全）。</summary>
    public GrantAnswer Answer =>
        _deny.Checked ? GrantAnswer.Deny
        : _once.Enabled && _once.Checked ? GrantAnswer.Once
        : GrantAnswer.Allow;

    /// <summary>三个选项是否都在场（自检用，按稳定顺序）。</summary>
    public string Answers =>
        string.Join(",", new[] { _allow, _deny, _once }.Select(r => r.Name));

    /// <summary>「仅本次」是否可选（Tier 2 必须置灰，docs/18 §3）。</summary>
    public bool OnceEnabled => _once.Enabled;

    /// <summary>把能力 ID 说成人话（docs/14 §2 的描述列）。未知能力原样显示 ——
    /// 猜一个友好的说法比显示原始 ID 更危险：用户会以为他知道自己授权了什么。</summary>
    public static string PlainTalk(string capabilityId) => capabilityId switch
    {
        "input.mousePosition" => "读取全局鼠标位置（input.mousePosition）",
        "input.moveWindow" => "把这扇窗移到屏幕上任意位置（input.moveWindow）",
        "input.clickThrough" => "点击穿透：透明区域让下面的程序收到点击（input.clickThrough）",
        "input.keyboardHook" => "安装全局键盘/鼠标钩子，能看到你在任何程序里的按键（input.keyboardHook）",
        "assets.read:cas" => "读取宠物素材库中的文件（assets.read:cas）",
        "assets.write:cas" => "往宠物素材库里写文件（assets.write:cas）",
        "audio.play" => "播放声音（audio.play）",
        "persist.write:self" => "保存它自己的状态（persist.write:self）",
        "persist.read:self" => "读回它自己的状态（persist.read:self）",
        "screen.changeDpi" => "跟随显示器缩放变化（screen.changeDpi）",
        _ when capabilityId.StartsWith("network.fetch:", StringComparison.Ordinal) =>
            "访问网络：" + capabilityId["network.fetch:".Length..] + "（" + capabilityId + "）",
        _ when capabilityId.StartsWith("ipc.tier2:", StringComparison.Ordinal) =>
            "与它自己的独立进程通信（" + capabilityId + "）",
        _ => capabilityId,
    };
}
