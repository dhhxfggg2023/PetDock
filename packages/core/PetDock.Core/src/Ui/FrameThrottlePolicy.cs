// RFC-21 §3.3：档位表与**唯一**的档位判定。
//
// 为什么单独一个类：原规格 12 §4 的三条（静止 / 遮挡 / 全屏）如果让帧循环各判一半，
// 「现在该用哪一档」就有了两个答案。本文件是全仓**唯一**把读数折成档位的地方 ——
// ResidentFrameLoop 只调 Decide、只消费结果，不自己再判一套（RFC-21 §3.1 的属主表，
// 判据 ui-throttle-single-decision-owner 就是扫这一条）。
//
// 依赖方向：Ui → Hosting（既有：UiHost 已经引用 PetDock.Hosting）。判定不碰泵、不碰窗口、不渲染。
using System;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

// 可见度（t12 收紧，t9 只读审计结论）：本文件四个类型一律 internal。
// 理由：src/Ui 在本片之前零个 public 类型（UiHost / ResidentFrameLoop / UiSnapshot 全是 internal），
// 「判据要读所以要 public」不成立 —— test/SelfCheck 与 src 同属 PetDock.Core 这一个 assembly
// （PetDock.Core.csproj 的 SDK 默认 glob 含 test/），internal 一样读得到。

/// <summary>RFC-21 §3.3 的档位。取值写死：判据与诊断读数按数字断言，不许改号。</summary>
internal enum FrameTier
{
    /// <summary>正常：按用户设置出帧，每 tick 提交。</summary>
    Active = 0,

    /// <summary>静止：外表帧不变，周期封顶 30fps，且**只在内容变化时提交**。</summary>
    Idle = 1,

    /// <summary>遮挡：100ms（10fps），每 tick 提交。</summary>
    Occluded = 2,

    /// <summary>全屏：停泵，不提交，不藏窗。</summary>
    Fullscreen = 3,
}

/// <summary>一次判定的输入：全是现成读数，判定自己不采任何样。</summary>
internal readonly record struct ThrottleInputs(
    IntPtr Window,
    Func<int, int, byte?>? AlphaAt,
    bool FrameReused,
    (int Left, int Top, int Width, int Height) WindowRect,
    uint Dpi,
    double UserFrameMs);

/// <summary>一次判定的输出：档位 + 该档的目标周期 + 这一帧要不要提交 + 判据 token。</summary>
internal readonly record struct ThrottleDecision(
    FrameTier Tier,
    double TargetFrameMs,
    bool Submit,
    string Reason);

/// <summary>
/// 一次**原子**读数：七个字段取自同一次锁内（逐字段各读会拼出 `Decide` 从未产出过的组合）。
/// `IdleDecisions` 只在判成静止档那一处前进 ⇒「这窗每 tick 都判成静止」可判定。
/// </summary>
internal readonly record struct ThrottleReading(
    FrameTier Tier, double TargetFrameMs,
    long Ticks, long Composed, long Submitted, long Skipped, long IdleDecisions);

/// <summary>
/// 档位判定的**唯一**属主。全仓只有这一个方法把读数折成档位。
/// 判定顺序（先匹配先赢）：全屏 → DWM 藏窗 → 点探针遮挡 → 外表帧复用且几何未变 → 正常。
/// </summary>
internal sealed class FrameThrottlePolicy
{
    /// <summary>静止档的周期上限基准：原规格 12:72「降到 30 fps」—— 是**上限**，不是绝对值。</summary>
    internal const double IdleCapMs = 1000.0 / 30.0;

    /// <summary>遮挡档的周期：原规格 12:73「降帧到 10 fps」—— 是绝对数值，不受用户设置影响。</summary>
    internal const double OccludedFrameMs = 100.0;

    private readonly ThrottleProbe _probe = new();

    /// <summary>判定/探测抛异常的次数（fail-open 的读数面：任何一次失败都退回 Active）。</summary>
    internal int ProbeFailures { get; private set; }

    /// <summary>
    /// 把一次读数折成档位。**不抛**：探测异常一律退回 Active/active 并计数
    /// （RFC-21 §3.3「探测失败绝不冻结宠物」）。
    /// </summary>
    /// <param name="inputs">本次判定要看的读数。</param>
    /// <param name="lastSubmitRect">上一次**真的提交**时的窗口矩形（null = 还没提交过）。</param>
    /// <param name="lastSubmitDpi">上一次真的提交时的 DPI（null = 还没提交过）。</param>
    internal ThrottleDecision Decide(
        ThrottleInputs inputs,
        (int, int, int, int)? lastSubmitRect,
        uint? lastSubmitDpi)
    {
        try
        {
            // ① 全屏：前台有一个覆盖本宠物所在显示器的窗（桌面本体与任务栏类由探测层排除）。
            if (_probe.SampleFullscreen(inputs.Window))
            {
                // 全屏档**没有周期可表达**（原规格 11 §7 只有 8.3 / 33.3 / 25ms，没有 0fps），
                // 它的执行方式是停泵。这里返回的周期是「恢复时」要套用的值（RFC-21 §3.3）。
                return new ThrottleDecision(
                    FrameTier.Fullscreen,
                    inputs.UserFrameMs,
                    Submit: false,
                    "fullscreen-fg");
            }

            // ② DWM 把本窗藏了（虚拟桌面切换 / UWP 挂起 / 资源管理器接管显示）：用户看不见它，按遮挡档省电。
            //    注意它**不是**「被别的窗盖住」——那个问题由 ③ 回答（RFC-21 §8 第 3 条）：
            //    实测分层 topmost 宠物窗被完全盖住时 cloaked 恒为 0，两条口径不可互换。
            if (_probe.TrySampleCloaked(inputs.Window, out int cloaked) && cloaked != 0)
            {
                return new ThrottleDecision(
                    FrameTier.Occluded,
                    OccludedFrameMs,
                    Submit: true,
                    "occluded-cloaked");
            }

            // ③ 点探针：当前帧第一个**不透明**取样点上，最上层是谁。
            //    中心点常是透明像素（角色不是方的），用它判遮挡会被恒判「被遮挡」（RFC-21 §3.2 ①）。
            if (_probe.SampleOccluded(inputs.Window, inputs.AlphaAt))
            {
                return new ThrottleDecision(
                    FrameTier.Occluded,
                    OccludedFrameMs,
                    Submit: true,
                    "occluded-point");
            }

            // ④ 静止：外表帧复用 **且** 几何快照未变。几何也要判，因为 UpdateLayeredWindow 的位置
            //    是每次提交传进去的（LayeredFrameWindow.cs:174）—— 窗口被拖走、DPI 变了，屏幕那一帧就得重画。
            if (inputs.FrameReused
                && lastSubmitRect is (int, int, int, int) rect
                && rect == inputs.WindowRect
                && lastSubmitDpi == inputs.Dpi)
            {
                // 周期取 max(设置, 1000/30)：原规格 12:72 说「降到 30 fps」，那是**上限**。
                // 用户把阈值设成 5fps（200ms）时不该把他拉回 30fps（那叫提速）；
                // 默认 33.3ms 时这条是恒等式（RFC-21 §8 第 1 条如实登记）。
                return new ThrottleDecision(
                    FrameTier.Idle,
                    Math.Max(inputs.UserFrameMs, IdleCapMs),
                    Submit: false,
                    "idle-reused");
            }

            return new ThrottleDecision(FrameTier.Active, inputs.UserFrameMs, Submit: true, "active");
        }
        catch (Exception error)
        {
            // fail-open：探测炸了也照常出帧。计数给判据（ui-throttle-probe-failure-inert 读它）。
            ProbeFailures++;
            KernelLog.Write($"throttle-decide-failed {error.GetType().Name}: {error.Message}");
            return new ThrottleDecision(FrameTier.Active, inputs.UserFrameMs, Submit: true, "active");
        }
    }
}
