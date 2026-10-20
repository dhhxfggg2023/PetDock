// 用户设置（docs/17 §1 的 UserSettings 形态；docs/18 §2.3 设置页）。
//
// 这里只管**两项**：空闲降帧阈值（帧周期）与语言。
// 开机自启不在这里：它的事实源是注册表（StartupRegistration）。
// 同一个事实存两处（文件 + 注册表）必然漂移——设置页显示的勾选状态一律从注册表读，
// 文件里不重复保存这一项。
using System;
using System.Globalization;

namespace PetDock.Hosting;

/// <summary>
/// 用户设置快照。字段可空性：docs/17 §2 说「只许加可空字段」，所以新增项一律带默认值，
/// 旧文件缺字段时读出来就是默认值，不需要迁移代码。
/// </summary>
internal sealed record UserSettings(
    double IdleFrameBudgetMs,
    string Language)
{
    /// <summary>清单版本（docs/17 §2：显式版本，只向前）。</summary>
    public const int SchemaVersion = 1;

    /// <summary>默认语言。</summary>
    public const string DefaultLanguage = "zh-CN";

    /// <summary>默认帧周期：30fps（= 1000/30，与 FrameBudget.IdleFrameBudgetMs 同一数值）。</summary>
    public const double DefaultIdleFrameBudgetMs = 1000.0 / 30.0;

    /// <summary>帧周期下限：60fps（更快没有意义，白烧 CPU）。</summary>
    public const double MinIdleFrameBudgetMs = 1000.0 / 60.0;

    /// <summary>帧周期上限：5fps（再慢就不是动画了）。</summary>
    public const double MaxIdleFrameBudgetMs = 1000.0 / 5.0;

    /// <summary>默认设置（文件缺失或损坏时的回落值）。</summary>
    public static UserSettings Default { get; } =
        new(DefaultIdleFrameBudgetMs, DefaultLanguage);

    /// <summary>
    /// 把越界的值拉回合法区间。写入前与读出后各过一遍：
    /// 外部改过文件、或从旧版本读出越界值时，都不会让帧泵跑在一个荒谬的周期上。
    /// </summary>
    public UserSettings Clamped() => this with
    {
        IdleFrameBudgetMs = Math.Clamp(
            IdleFrameBudgetMs,
            MinIdleFrameBudgetMs,
            MaxIdleFrameBudgetMs),
        Language = string.IsNullOrWhiteSpace(Language) ? DefaultLanguage : Language,
    };

    /// <summary>帧周期的可读写法（面板用毫秒整数编辑，这里给一位小数）。</summary>
    public decimal IdleFrameBudgetMsDecimal =>
        (decimal)Math.Round(IdleFrameBudgetMs, 1, MidpointRounding.AwayFromZero);

    /// <summary>一行自检输出：设置项与值（判据的可判定面）。</summary>
    public string Describe() =>
        $"idle-frame={IdleFrameBudgetMs.ToString("0.0", CultureInfo.InvariantCulture)}ms"
        + $" language={Language}"
        + $" start-with-windows=(见注册表)";
}
