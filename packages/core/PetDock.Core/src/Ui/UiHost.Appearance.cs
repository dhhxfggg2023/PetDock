// 外观设置（材质 + 亮度）的落地点（2026-10-28 第二版）。
//
// 为什么单开一个文件：UiHost.Panel.cs 已经贴着 D-03c 的 400 行上限；
// 这两件事（DWM 合成、换色板后重建面板）本来也是「外观」这一件事。
using PetDock.Core.Compositor;
using PetDock.Core.Ui.Design;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>
    /// 把材质与亮度落到当前面板上，返回 null（成功）或一句「为什么与选的不完全一样」。
    /// 亮度变化只换色板、不重建面板（2026-10-28 第三轮）：页面全部是 WPF 且只认
    /// DynamicResource，换资源键的值就够了。旧做法 Dispose 再 OpenPanel 会把窗口放回屏幕中央、
    /// 跳回第一页、丢掉滚动位置 —— 用户视角就是「切个深浅色，界面重开了一次」。
    /// </summary>
    private string? ApplyAppearance(MaterialKind material, ThemeMode theme)
    {
        Theme.Material = material;

        bool themeChanged = Theme.Mode != theme;
        Theme.Mode = theme;
        if (themeChanged && _panel is not null && !_panel.IsDisposed)
        {
            _panel.ApplyPalette();
            KernelLog.Write($"theme-applied mode={Theme.ModeToken(theme)}");
        }

        if (_panel is null || _panel.IsDisposed)
        {
            return null;
        }

        MaterialReport report = Theme.ApplyMaterial(_panel);
        _panel.Invalidate(true);
        return !report.Blurred && material != MaterialKind.Opaque
            ? I18n.Get("cp.settings.material.fallback")
            : null;
    }
}