// 一次「确定/取消」的确认（原规格 18 §1：退出是固定项，但要有一次确认）。
//
// 为什么不用 MessageBox.Show：它给的窗不进置顶档，与本产品其他模态窗同一条账
// （真机取证见 PetDockDialog.cs）。确认框看不见，用户得到的是「点了退出没反应，进程还在」
// 的观感 —— 比不弹框更糟：用户以为关掉了，桌宠其实还在。
//
// 外观（原规格 18 §2.6）：窗内一律走设计令牌 —— 深色底、奶油白字、自绘按钮。
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;

namespace PetDock.Core.Ui;

internal sealed class ConfirmDialog : PetDockDialog
{
    internal ConfirmDialog(string text, string caption)
    {
        Text = caption;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Theme.Apply(this);
        ClientSize = new Size(Theme.Dip(this, 460), Theme.Dip(this, 170));

        var message = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            Font = Theme.FontFor(this, TypeRole.Body),
            ForeColor = Theme.Text,
            BackColor = Theme.Bg,
            Padding = new Padding(
                Theme.Dip(this, Theme.SpaceLg), Theme.Dip(this, Theme.SpaceLg),
                Theme.Dip(this, Theme.SpaceLg), Theme.Dip(this, Theme.SpaceSm)),
        };

        FlowLayoutPanel buttons = Theme.DialogButtonBar(this);
        var ok = new ThemedButton { Text = I18n.Get("ui.confirm.ok"), DialogResult = DialogResult.OK, Primary = true };
        ok.FitToText();
        var cancel = new ThemedButton { Text = I18n.Get("ui.confirm.cancel"), DialogResult = DialogResult.Cancel };
        cancel.FitToText();
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        Controls.Add(message);
        Controls.Add(buttons);
        // 默认键 = 取消：不可逆动作上误按回车不该生效（与原 MessageBox 的 Button2 同一条理由）。
        AcceptButton = cancel;
        CancelButton = cancel;
    }

    /// <summary>问一句；true = 用户按了确定。</summary>
    internal static bool Ask(string text, string caption)
    {
        using var dialog = new ConfirmDialog(text, caption);
        return dialog.ShowDialog() == DialogResult.OK;
    }
}