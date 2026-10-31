// 图标集（R2b）：一个名字 → 一个字形。
//
// 为什么用字体字形而不是 Path 几何：Segoe MDL2 Assets 是 Windows 自带的图标字体，
// 缩放不失真、不需要随包发资源、也不会因为 DPI 变化去重建几何。代价是字形码位必须写死，
// 于是判据 ui-design-icon-coverage 保证「每个名字都有字形、未知名字有兜底」。
//
// 旧层的 IUiPage.Glyph 是「一个字符」，于是页面插件各自挑字符（风格散掉）。
// 新层把可选集收成一个枚举：插件只能从这套里挑，挑不到就用 Fallback。
using System;

namespace PetDock.Ui.Design;

/// <summary>界面用得到的图标。新增一项就必须给一个字形（判据会逐项点名）。</summary>
public enum IconName
{
    None = 0,
    Home,
    Settings,
    Plug,
    Refresh,
    Download,
    Delete,
    Close,
    Add,
    ChevronRight,
    Check,
    Warning,
    Error,
    Info,
    Search,
    Folder,
    Play,
    Pause,
    Save,
    Restart,
}

/// <summary>字形表：图标字体 + 码位 + 兜底。</summary>
public static class Icons
{
    /// <summary>图标字体。缺失时 WPF 会回退到系统字体，字形会变成方框 —— 这正是要兜底的原因。</summary>
    public const string FontFamily = "Segoe MDL2 Assets";

    /// <summary>未知图标画这个（问号），而不是留一个空白洞。</summary>
    public const string Fallback = "\uE9CE";

    /// <summary>取字形。未知/未定义的名字一律拿兜底字形。</summary>
    public static string Glyph(IconName name) => name switch
    {
        IconName.Home => "\uE80F",
        IconName.Settings => "\uE713",
        IconName.Plug => "\uE8F1",
        IconName.Refresh => "\uE72C",
        IconName.Download => "\uE896",
        IconName.Delete => "\uE74D",
        IconName.Close => "\uE711",
        IconName.Add => "\uE710",
        IconName.ChevronRight => "\uE76C",
        IconName.Check => "\uE73E",
        IconName.Warning => "\uE7BA",
        IconName.Error => "\uE783",
        IconName.Info => "\uE946",
        IconName.Search => "\uE721",
        IconName.Folder => "\uE8B7",
        IconName.Play => "\uE768",
        IconName.Pause => "\uE769",
        IconName.Save => "\uE74E",
        IconName.Restart => "\uE72C",
        IconName.None => string.Empty,
        _ => Fallback,
    };

    /// <summary>枚举里真的有这几项吗（判据用；也提醒改枚举的人别忘了字形）。</summary>
    public static IconName[] All() => (IconName[])Enum.GetValues(typeof(IconName));
}
