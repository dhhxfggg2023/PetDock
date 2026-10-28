// 界面语言切换（2026-10-28）：语言从引导窗搬进设置页，默认中文。
//
// 生效路径与「亮度」一致：写设置 → 换文案表 → 重建面板。
//   重建要排到当前消息之后：这次调用就发生在 WPF 单选按钮的 Checked 事件里，
//   当场 Dispose 掉承载事件的页面会拆掉事件源（同一段理由见 UiHost.Appearance.cs）。
// 语言**不**塞进 ApplySettings：那一条管的是材质/亮度，改动面与失败面都不同。
//
// 默认中文只对「没设置过的用户」生效：这里从不主动把 Language 改回默认值，
// 用户选过的语言就是用户的事实。
using System;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>
    /// 换界面语言（UI 线程）：写设置、换文案表、刷新托盘、重建面板。
    /// 返回 null = 成功；否则是给用户看的原因（页面把它放进状态条，不弹模态框）。
    /// </summary>
    private string? SetLanguageFromPanel(string language)
    {
        string? wanted = I18n.Canonical(language);
        if (wanted is null)
        {
            return I18n.Get("cp.settings.language.unsupported", language ?? string.Empty);
        }

        // 已经是这门语言：不写盘、不重建（页面在重新绑定时会回到同一个选项，
        // 没必要为一次无变化的点击把整扇面板拆掉）。
        if (string.Equals(wanted, I18n.Language, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        UserSettingsStore? store = _settings;
        if (store is null)
        {
            return I18n.Get("cp.settings.unavailable");
        }

        // 读出 → 改一个字段 → 整份写回：凭记忆造一份 UserSettings 会抹掉引导标记等字段。
        try
        {
            store.Update(current => current with { Language = wanted });
        }
        catch (Exception error)
        {
            return I18n.Get("ui.panel.settingsSaveFailed", error.Message);
        }

        I18n.Load(wanted);
        _tray.ReloadLanguage();
        KernelLog.Write($"i18n-reloaded lang={I18n.Language} via=settings");

        ControlPanel? stale = _panel;
        if (stale is not null && !stale.IsDisposed)
        {
            _panel = null;
            stale.BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
            {
                stale.Dispose();
                OpenPanel();
            }));
        }

        return null;
    }
}
