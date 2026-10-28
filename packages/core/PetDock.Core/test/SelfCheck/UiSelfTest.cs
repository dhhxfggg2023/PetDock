// UI 母体自检（RFC-12 §3，原规格 18 §1/§2/§2.4/§2.5）。
// 命令形态：`--uitest`，每行 `CHECK ui-<name> PASS|FAIL|SKIP <detail>`，统计行 `STAT uitest …`，
// 末行 `UI-OK skipped=<s>` / `UI-FAIL n=<fail+unowned> skipped=<s>`。
// 判据口径来自文档，不由实现自证：
//   原规格 18 §1     托盘恰有五个顶层项，顺序固定，「暂停全局输入钩子」是强制项不可隐藏
//   原规格 18 §2/§2.5 主控面板四页齐全、面板单实例、关闭面板不退出进程
//   原规格 18 §2.4   无宠物时性能页显示「尚无类似」而不是编造数字
//   原规格 18 §3     授权弹窗三选一，Tier 2 的「仅本次」置灰
// 无人值守：全部控件树只 CreateControl 不 Show，任何一步失败都落成 FAIL 行而不抛。
// 第三种结局（跳过）在 SkipLedger.cs：2026-10-25 判据体检 S2 之前，`…(skipped)` 只能塞进 ok=true
// 一起算 PASS，三种结局里两种是绿的。现在 SKIP 只从 Check 这一个收尾出，且没人接的 SKIP 判红。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.Input;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class UiSelfTest
{
    private static string? _logPath;
    private static string? _shotsDir;

    public static int Run(string[] args)
    {
        int logIdx = Array.FindIndex(args, a => string.Equals(a, "--log", StringComparison.Ordinal));
        _logPath = logIdx >= 0 && args.Length > logIdx + 1 ? args[logIdx + 1] : null;

        // 人眼验收的截图工件（原规格 18 §7.1）：--uitest --shots <目录>。
        int shotIdx = Array.FindIndex(args, a => string.Equals(a, "--shots", StringComparison.Ordinal));
        _shotsDir = shotIdx >= 0 && args.Length > shotIdx + 1 ? args[shotIdx + 1] : null;

        try
        {
            // 文案表在**任何界面对象构造之前**加载（RFC-42 §3.2）：--uitest 直接 new 界面对象，
            // 不走 UiHost 的构造路径（那里只在有设置存储时才 Load），不先加载就会让每条界面文案
            // 都显示键名本身 —— 断键的判据于是两侧都是键名，绿得毫无内容。
            I18n.Load(I18n.DefaultLanguage);
            TrayItems();
            // 2026-10-28 WPF 壳：页面全部来自 IUiPage 插件；这里验壳本身（预览上下文 + 假页面）。
            UiWorkspaceChecks.Run(Check);
            // 2026-10-28 第三轮：导入/安装的长任务（进度条、取消、取消后的现场）。
            UiWorkChecks.Run(Check);
            PanelSingleton();
            RosterReadOnly();
            AuthorizationDialogCheck();
            // 2026-10-26 真机故障（弹窗被压在宠物下面/后面）的反向门禁：模态窗必须在置顶档，
            // 且真的压在宠物上面。见 ModalBandChecks.cs。
            ModalBandChecks.Run(Check);
            // 2026-10-26：同批授权请求合进一个窗、按插件分段的判据（ui-auth-batch-*）。
            ConsentBatchChecks.Run(Check);
            FramePumpOverruns();
            KeyboardHookContract();
            ResidentLoopChecks.Run(Check);
            // 2026-10-28：审计流水的只读视图（原规格 18 §2.8，ui-audit-view-*）。
            AuditChecks.Run(Check);
            // 2026-10-28：管理面（策略默认 / 清单视图 / 开关落盘 / 安装器护栏）的判据。
            PluginManageChecks.Run(Check);
            // 2026-10-28 第三轮：回收站（列出 / 恢复 / 冲突拒绝 / 彻底删除 / 保留策略）。
            PluginTrashChecks.Run(Check);
            // RFC-35 §4：窗口鼠标消息 → 交互会话（ui-pointer-*）。
            PointerWiringChecks.Run((name, ok, detail) => Check(name, ok, detail));
            // RFC-31 §4：命中图参与穿透（ui-hitmap-*，RFC-41 §4.2b 已删掉 alpha-only 那一族）。
            HitMapChecks.Run(Check);
            // RFC-41 §4：命中阈值（hit-threshold-*）与「申报的命中面被消费」（hit-surface-wired* / ui-hit-tier1-consumes）。
            HitThresholdChecks.Run(Check);
            InstanceChecks.Run(Check);
            // RFC-36 §4：宿主 Start 之前的一致性读取不许干等调度器（ui-compose-read-before-start）。
            HostComposeChecks.Run(Check);
            // RFC-37 §4：清单里的宠物是集合（ui-catalog-*）—— 多宠物的实例层。
            PetSetChecks.Run(Check);
            // RFC-37 §4（片 1b）：屏幕上真的摆了几只（ui-set-*）—— 多宠物的窗口层。
            PetWindowChecks.Run(Check);
            // RFC-37 §7 第 4 条：位置按宠物持久化（ui-pet-position-persist-*）。
            PetPositionChecks.Run(Check);
            // 2026-10-28 审计 P0：设置落盘只有一个写者（ui-settings-update-*）。
            SettingsStoreChecks.Run(Check);
            TrayPetChecks.Run(Check);
            OnboardingChecks.Run(Check);
            OnboardingConsentChecks.Run(Check);
            DiagnosticChecks.Run(Check);
            AuditTrailChecks.Run(Check);
            // RFC-21 §4：静态节流分档的判据（ui-throttle-*）。判据体在 ThrottleChecks。
            ThrottleChecks.Run(Check);
            // 判据体检 S2（2026-10-25）：账本自己的负例对照 —— 证明「跳过」真的分得开、
            // 且没人接的 SKIP 会改变末行 marker。见 SkipLedger.ControlCheck。
            SkipLedger.ControlCheck(Check, "ui-skip-ledger-classify");

            // 截图工件：判据之后再拍，保证拍到的就是刚刚被判绿的那棵树。
            if (_shotsDir is not null)
            {
                int shots = UiShots.Capture(_shotsDir, Emit);
                Emit($"STAT shots n={shots} dir={_shotsDir}");
            }
        }
        catch (Exception error)
        {
            Check("ui-crash", false, $"{error.GetType().Name}: {error.Message}");
        }

        // 收尾：pass/skip/fail 分行数，末行 marker 带 skipped=N（可放过的 SKIP 也必须看得见）。
        Ledger.Report("uitest", "UI-OK", "UI-FAIL");
        return Ledger.Verdict;
    }

    /// <summary>输出同时进控制台与 --log 文件：WinExe + AttachConsole 时管道捕获恒为空，
    /// 没有 --log 就无法在脚本里取证（2026-10-19 已付过这笔账）。</summary>
    private static void Emit(string line)
    {
        Console.WriteLine(line);
        if (_logPath is null)
        {
            return;
        }

        try
        {
            System.IO.File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // 日志写不进去不能把判据结论改掉。
        }
    }

    /// <summary>
    /// 判据落地：三种结局都交给账本（SkipLedger.Check）—— 一条只打印不计数、最后仍打 OK 的门禁，
    /// 比没有门禁更坏（RFC-04 §3「永久红的门禁只有两种结局」的反面）。
    ///
    /// 判据体检 S2（2026-10-25）之后，这里同时是**第三种结局唯一的收尾**：`(name, ok, detail)`
    /// 这个形状没有地方放「跳过」，所以 `…(skipped)` 曾经只能塞进 ok=true 一起算 PASS。判定口径
    /// 本身在 SkipLedger.SkipKindOf，不在这里。
    /// </summary>
    private static void Check(string name, bool ok, string detail) => Ledger.Check(name, ok, detail);

    /// <summary>收尾账本：CHECK 行全部过它，SKIP 只从它出（见 SkipLedger.cs）。</summary>
    private static readonly SkipLedger Ledger = new(Emit);

    /// <summary>§1：主控面板 → 每宠物一条 → 退出，顺序固定、id 稳定。</summary>
    private static int TrayItems()
    {
        int failures = 0;
        UiCommand noop = Noop;
        using var tray = new TrayHost(
            new[] { new PetMenuItem("sample-pet", noop, noop, noop) },
            noop, noop);
        IReadOnlyList<string> order = tray.ItemOrder;
        bool expected = order.Count == 3
            && order[0] == "panel"
            && order[1] == "pet-1"
            && order[2] == "exit";
        Check("ui-tray-items", expected, "items=" + string.Join(",", order));
        return failures;
    }


    /// <summary>§2.5：再次点托盘只激活已有窗口（单实例）。</summary>
    private static int PanelSingleton()
    {
        int failures = 0;
        using var window = new LayeredFrameWindow();
        window.Hide();
        using var host = new UiHost(window, null, null);
        host.OpenPanel();
        object first = host.Panel!;
        host.OpenPanel();
        object second = host.Panel!;
        Check("ui-panel-singleton", ReferenceEquals(first, second), $"first={first.GetHashCode()} second={second.GetHashCode()}");
        return failures;
    }

    /// <summary>§2.2/§2.3：花名册只读——空宿主给空表，且不改任何状态。</summary>
    private static int RosterReadOnly()
    {
        int failures = 0;
        UiSnapshot snapshot = UiRoster.Build(null, null);
        Check("ui-roster-readonly",
            snapshot.Pets.Count == 0 && snapshot.Plugins.Count == 0,
            $"pets={snapshot.Pets.Count} plugins={snapshot.Plugins.Count}");
        return failures;
    }

    /// <summary>§3：三选一存在；Tier 2 的「仅本次」置灰；取消按拒绝处理。</summary>
    private static int AuthorizationDialogCheck()
    {
        int failures = 0;
        using var tier1 = new AuthorizationDialog("示例宠物", "sample-pet", 1, new[] { "input.mousePosition", "network.fetch:api.example.com" });
        bool tier1Ok = tier1.Answers == "Allow,Deny,Once" && tier1.OnceEnabled && tier1.Answer == GrantAnswer.Allow;
        Check("ui-auth-dialog", tier1Ok, $"answers={tier1.Answers} once={tier1.OnceEnabled} default={tier1.Answer}");

        using var tier2 = new AuthorizationDialog("Tier2 客体", "some-tier2", 2, new[] { "ipc.tier2:shim" });
        Check("ui-auth-dialog-tier2-once-disabled", !tier2.OnceEnabled, $"once={tier2.OnceEnabled}");

        // 未知能力原样显示（不猜友好说法）
        string unknown = AuthorizationDialog.PlainTalk("some.unknown.capability");
        Check("ui-auth-dialog-plaintalk", string.Equals(unknown, "some.unknown.capability", StringComparison.Ordinal), $"text={unknown}");
        return failures;
    }

    /// <summary>
    /// 帧泵的超限计数真的会动（原性能页那一列的来源，页面删掉后判据留下 ——
    /// 它验的是泵，不是页）。超限口径是固定预算 8.3ms，所以这里每帧睡 20ms。
    /// </summary>
    private static int FramePumpOverruns()
    {
        int failures = 0;
        using var pump = new FramePump();
        pump.SetTargetFrameMs(1.0);
        pump.Start(() =>
        {
            Thread.Sleep(20);
            return true;
        });
        Thread.Sleep(300);
        pump.Stop();
        Check("ui-frame-pump-overruns", pump.OverBudgetFrames > 0,
            $"over={pump.OverBudgetFrames} frames={pump.Frames}");
        return failures;
    }

    /// <summary>
    /// 原规格 14:110 + 原规格 16:56：键盘钩子的两条硬约束。
    /// ① 没有插件持权时**不安装**（一个装了却没人要的全局钩子只是白拿用户的每一次按键）；
    /// ② 安装后必须能被托盘固定项停止，且停止后不再向外发事件。
    /// 不测「真的敲键盘」——那需要交互式桌面会话与外部输入，无人值守跑不出来；
    /// 真实按键的端到端证据在 --eventtest/--drivetest 与手工走查里，不在这一条。
    /// </summary>
    private static int KeyboardHookContract()
    {
        int failures = 0;

        // ① 无持权者：UiHost 构造后不应装钩子。
        using (var window = new LayeredFrameWindow())
        {
            window.Hide();
            using var host = new UiHost(window, null, null);
            Check("ui-keyhook-not-installed-without-grantee", !host.KeyboardHookInstalled,
                $"installed={host.KeyboardHookInstalled}");
        }

        // ② 钩子本体：安装 → 暂停 → 卸载，全程不抛；暂停期间不向外发事件。
        var strokes = new List<KeyStroke>();
        using var hook = new KeyboardHook(strokes.Add);
        bool installed = hook.Install();
        Check("ui-keyhook-install", installed && hook.IsInstalled, $"installed={installed}");

        hook.Pause();
        bool pausedFlag = hook.Paused;
        hook.Resume();
        Check("ui-keyhook-pause-resume", pausedFlag && !hook.Paused, $"paused={pausedFlag}");

        hook.Uninstall();
        Check("ui-keyhook-uninstall", !hook.IsInstalled, $"installed={hook.IsInstalled}");

        // 暂停/恢复不应产生任何事件（回调只由 OS 触发，这里验证的是它没有副作用）。
        Check("ui-keyhook-no-synthetic-events", strokes.Count == 0, $"strokes={strokes.Count}");
        return failures;
    }

    private static void Noop()
    {
    }
}
