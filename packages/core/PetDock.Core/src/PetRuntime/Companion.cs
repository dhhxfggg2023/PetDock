// 陪伴层（companion）ABI（2026-10-29，P4 插件线的第一块）。
//
// 为什么不是第四个原有角色：视图 / 行为 / 交互 / 状态四个角色各自只有一个「主」实现
// （交互层 0..n，但只收指针会话），而「喂养、心情、成长」这类玩法需要每实例 0..n 个、
// 能收 tick 与事件、能读写自己的状态、还能在面板上露一行进度与命令的东西。
// 把它们塞进 brain 会强迫用户二选一（装了喂养就丢了散步），塞进状态层又与落盘语义冲突。
// 所以这里新增一个**可选**角色：companion。它不改变既有四角色的任何语义 ——
// 没装 companion 的实例与之前完全一样（0 个 companion 时宿主一条调用都不发）。
//
// 三条约束（宿主强制执行，插件不能绕过）：
//   1. companion 不直接碰宿主：它只拿 <see cref="ICompanionHost"/>（语言 / 落盘 / 说话）；
//      指令仍走 <see cref="BrainDirective"/>，由宿主仲裁后落地；
//   2. 状态走 <see cref="IPetState"/>：写进缓冲，提交点由宿主决定（生命周期边界 + 插件主动请求）；
//   3. Report 是**不可变快照**：UI 线程随时读，实现不得返回会随后台 tick 变化的同一对象。
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PetDock.Core.PetRuntime;

/// <summary>
/// 一次桌面上发生的动作（宿主在气泡同时刻发；不含鼠标原始坐标）。
/// 与指针会话分开：会话是「拖拽/点击」的仲裁，这里只告诉陪伴层「用户刚刚做了什么」。
/// </summary>
public enum CompanionGesture
{
    /// <summary>单击（等过系统双击间隔之后才算）。</summary>
    Poke = 0,

    /// <summary>拖动开始（超过阈值）。</summary>
    Drag = 1,

    /// <summary>松手但速度不足以抛掷。</summary>
    Drop = 2,

    /// <summary>松手后进入抛掷（窗口层接管运动）。</summary>
    Throw = 3,

    /// <summary>抛掷落地。</summary>
    Land = 4,
}

/// <summary>
/// 面板上的一枚命令按钮（文字由插件本地化：宿主不认识「喂食」是什么，只负责摆按钮）。
/// 用 Command 而不是别的词：内核黑名单里的那个词不能整词落盘（见 tools/lint-core.mjs）。
/// </summary>
public sealed record CompanionCommand(string Id, string Label);

/// <summary>
/// 陪伴层给面板的一行只读状态。<see cref="Commands"/> 为空表示只展示、不可操作。
/// </summary>
public sealed record CompanionReport(string Title, string Status, string Detail)
{
    /// <summary>可点的命令（顺序即展示顺序）。</summary>
    public IReadOnlyList<CompanionCommand> Commands { get; init; } = System.Array.Empty<CompanionCommand>();

    /// <summary>空报告（没有可展示的陪伴层内容）。</summary>
    public static CompanionReport Empty { get; } = new(string.Empty, string.Empty, string.Empty);
}

/// <summary>
/// 宿主给陪伴层的口子。刻意只有三项：
/// <list type="bullet">
///   <item><see cref="Language"/>：当前界面语言（插件自己的文案自己翻，不往内核文案表里塞键）；</item>
///   <item><see cref="CommitState"/>：请求在安全点落盘（返回 false = 被拒或没有落盘层）；</item>
///   <item><see cref="Say"/>：让宠物头顶说一句话（走同一个非模态气泡，不抢焦点）。</item>
/// </list>
/// </summary>
public interface ICompanionHost
{
    /// <summary>当前界面语言码（如 zh-CN / en-US）。</summary>
    string Language { get; }

    /// <summary>请求落盘（宿主自己决定是不是现在能写；调用线程上同步返回）。</summary>
    bool CommitState();

    /// <summary>说一句话；空串 / 全空白 = 不说。</summary>
    void Say(string text);

    /// <summary>
    /// 请宿主播一个音效文件（WAV；路径由插件自己解析，宿主只决定播不播、播不播得出来）。
    /// 静音开关、缺文件、格式不支持都由宿主统一处理并记日志；插件不该自己 new 播放器 ——
    /// 「用户关了声音」只有一个落点才不可能漏。这不是新能力（见 SoundBoard 的注释）。
    /// </summary>
    void PlaySound(string soundPath);
}

/// <summary>陪伴层的初始化上下文（与 <see cref="BrainContext"/> 同形：实例 id / 状态层 / 预算 / 宿主口子）。</summary>
public sealed class CompanionContext
{
    public CompanionContext(
        string petInstanceId,
        string displayName,
        IPetState? state,
        ResourceBudget budget,
        ICompanionHost host)
    {
        PetInstanceId = petInstanceId ?? throw new System.ArgumentNullException(nameof(petInstanceId));
        DisplayName = displayName ?? string.Empty;
        State = state;
        Budget = budget;
        Host = host ?? throw new System.ArgumentNullException(nameof(host));
    }

    /// <summary>实例 id。</summary>
    public string PetInstanceId { get; }

    /// <summary>给用户看的名字（没有名字时是实例 id）。</summary>
    public string DisplayName { get; }

    /// <summary>本实例的状态层（可为 null：宿主没接落盘层）。</summary>
    public IPetState? State { get; }

    /// <summary>宿主预算。</summary>
    public ResourceBudget Budget { get; }

    /// <summary>宿主口子（语言 / 落盘 / 说话）。</summary>
    public ICompanionHost Host { get; }
}

/// <summary>
/// 一次 tick 的结果：可选的指令与可选的一句话。
/// 指令与 brain 的指令走同一个仲裁器；执行顺序是「先 brain 后 companion」，
/// 所以 companion 对同一属性（比如动画）的指令会覆盖 brain 的 —— 这是有意让陪伴层
/// 在特殊时刻（比如睡觉）接管表现，而不是每帧抢。
/// </summary>
public readonly record struct CompanionTick(IReadOnlyList<BrainDirective>? Directives, string? Say)
{
    /// <summary>什么都不做。</summary>
    public static CompanionTick None => default;

    /// <summary>只说一句。</summary>
    public static CompanionTick Speak(string text) => new(null, text);

    /// <summary>只发指令。</summary>
    public static CompanionTick Signal(IReadOnlyList<BrainDirective> directives) => new(directives, null);
}

/// <summary>
/// 每实例 0..n 个的陪伴层角色（可选角色，没装就不存在）。
/// 线程：<see cref="Tick"/> / <see cref="OnEvent"/> / <see cref="OnGesture"/> 在帧循环线程，
/// <see cref="Report"/> 会被 UI 线程读 —— 实现必须让 Report 返回不可变快照，
/// 不能在读的时候原地改一个可变对象。
/// </summary>
public interface IPetCompanion
{
    /// <summary>角色身份（与清单 id 一起用于去重与诊断）。</summary>
    InterfaceMeta Meta { get; }

    /// <summary>初始化：与 brain 同时机。失败即抛；宿主记 fault 后继续跑其余角色。</summary>
    Task InitAsync(CompanionContext context);

    /// <summary>下线（宿主给 1000ms 预算，超时只记日志）。</summary>
    Task DisposeAsync();

    /// <summary>每帧一次；dt 以毫秒计。抛异常 = 本 tick 结果作废并记 fault。</summary>
    CompanionTick Tick(int dtMs, WorldSnapshot world);

    /// <summary>总线事件（与 brain 收到的是同一串）。</summary>
    void OnEvent(PetEvent evt);

    /// <summary>桌面动作（点击 / 拖拽 / 休息 / 抛掷 / 落地）。</summary>
    void OnGesture(CompanionGesture gesture);

    /// <summary>
    /// 面板按钮的命令入口（<see cref="CompanionCommand.Id"/>）。
    /// 返回值 = 要让宠物说的话（null / 空白 = 不说）；抛异常由宿主吞掉并记 fault。
    /// </summary>
    string? Invoke(string commandId);

    /// <summary>给面板的只读状态快照（任意线程可读）。</summary>
    CompanionReport Report { get; }
}
