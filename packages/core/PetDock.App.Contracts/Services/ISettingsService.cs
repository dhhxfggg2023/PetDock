namespace PetDock.App;

/// <summary>
/// 设置的只读快照。字段全部是**能在没有界面时算出来**的值（与旧 SettingsView 同一口径）：
/// 界面只负责摆放，不改写、不猜默认。
/// </summary>
public sealed record AppSettings(
    double IdleFrameBudgetMs,
    string Language,
    bool StartWithWindows,
    string? Theme,
    string? Material,
    int DefaultPetSize,
    string? PetName,
    string? Version,
    bool SoundEnabled,
    bool CelestialMode);

/// <summary>
/// 设置：读当前值、切语言。
///
/// 「应用设置」（阈值/开机自启/材质/静音/天体）暂不进契约：它的每一步都可能部分失败，
/// 返回值要能说清「哪一项没成」，现在打包成一个 AppResult 会丢掉这个信息。
/// 那是 R3 屏（设置分区）之前必须补的设计，先记在 R1f 的待办里。
/// </summary>
public interface ISettingsService
{
    /// <summary>当前设置（含版本与主宠名字这类只读事实）。</summary>
    AppSettings Read();

    /// <summary>换界面语言（写设置并立即生效）。不支持的语言返回失败并给原因。</summary>
    AppResult SetLanguage(string language);
}
