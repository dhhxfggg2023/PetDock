// agent 状态这一层的文案（2026-10-29）：与喂养层同一套做法 —— 插件自带两门语言，
// 宿主只告诉它当前界面是哪种语言（ICompanionHost.Language），不往内核文案表里塞玩法键。
namespace PetDock.Agent;

internal sealed record AgentTexts(
    string Title,
    string StateIdle,
    string StateRunning,
    string StateDone,
    string StateFailed,
    string Missing,
    string Unreadable,
    string Refresh,
    string Stale,
    string DoneSay,
    string FailedSay,
    string AgeSeconds,
    string AgeMinutes,
    string AgeHours,
    string Progress,
    string Hint)
{
    public static readonly AgentTexts Zh = new(
        Title: "AI 助手",
        StateIdle: "空闲",
        StateRunning: "运行中",
        StateDone: "已完成",
        StateFailed: "失败",
        Missing: "未发现状态文件",
        Unreadable: "状态文件读不出来",
        Refresh: "刷新",
        Stale: "（可能已停）",
        DoneSay: "任务完成了。",
        FailedSay: "任务失败了。",
        AgeSeconds: "{0} 秒前",
        AgeMinutes: "{0} 分钟前",
        AgeHours: "{0} 小时前",
        Progress: "{0}%",
        Hint: "把状态 JSON 写到环境变量 PETDOCK_AGENT_STATE 指向的文件");

    public static readonly AgentTexts En = new(
        Title: "AI helper",
        StateIdle: "Idle",
        StateRunning: "Running",
        StateDone: "Done",
        StateFailed: "Failed",
        Missing: "No status file yet",
        Unreadable: "Status file is unreadable",
        Refresh: "Refresh",
        Stale: " (maybe stopped)",
        DoneSay: "The task finished.",
        FailedSay: "The task failed.",
        AgeSeconds: "{0}s ago",
        AgeMinutes: "{0} min ago",
        AgeHours: "{0} h ago",
        Progress: "{0}%",
        Hint: "Write status JSON to the file named by PETDOCK_AGENT_STATE");

    /// <summary>按界面语言挑表：zh 开头用中文，其余一律英文。</summary>
    public static AgentTexts For(string? language) =>
        language is not null && language.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase) ? Zh : En;
}
