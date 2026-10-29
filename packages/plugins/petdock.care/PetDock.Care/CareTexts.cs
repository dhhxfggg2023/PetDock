// 陪伴层自己的文案表（2026-10-29）：不往内核文案表里塞玩法键。
//
// 为什么插件自带两门语言：宿主只告诉它「现在界面是哪种语言」（ICompanionHost.Language），
// 「饱腹」「亲密」这些词属于这一层玩法，内核不认识它们，也不该为每个玩法插件扩键空间。
// 语言不认识时用 zh-CN：默认中文不等于强制中文 —— 用户选英文时这里给英文。
namespace PetDock.Care;

internal sealed record CareTexts(
    string Title,
    string FedLabel,
    string MoodLabel,
    string VimLabel,
    string LevelFormat,
    string DaysFormat,
    string FeedLabel,
    string PlayLabel,
    string RestLabel,
    string FeedSay,
    string PlaySay,
    string RestSay,
    string PokeSay,
    string LowFedSay,
    string LowVimSay)
{
    public static readonly CareTexts Zh = new(
        Title: "陪伴",
        FedLabel: "饱腹",
        MoodLabel: "心情",
        VimLabel: "体力",
        LevelFormat: "亲密 Lv.{0}",
        DaysFormat: "第 {0} 天",
        FeedLabel: "喂食",
        PlayLabel: "玩耍",
        RestLabel: "休息",
        FeedSay: "好吃！",
        PlaySay: "再玩一会儿嘛～",
        RestSay: "呼……睡饱了。",
        PokeSay: "嗯？",
        LowFedSay: "有点饿了。",
        LowVimSay: "好困……");

    public static readonly CareTexts En = new(
        Title: "Companion",
        FedLabel: "Fed",
        MoodLabel: "Mood",
        VimLabel: "Energy",
        LevelFormat: "Bond Lv.{0}",
        DaysFormat: "Day {0}",
        FeedLabel: "Feed",
        PlayLabel: "Play",
        RestLabel: "Rest",
        FeedSay: "Yum!",
        PlaySay: "Just a little longer!",
        RestSay: "Ahh... that was a good nap.",
        PokeSay: "Hm?",
        LowFedSay: "I'm a bit hungry.",
        LowVimSay: "So sleepy...");

    /// <summary>按界面语言挑表：zh 开头用中文，其余一律英文。</summary>
    public static CareTexts For(string? language) =>
        language is not null && language.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase) ? Zh : En;
}
