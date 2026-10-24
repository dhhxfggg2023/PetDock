// UI 母体自检（RFC-12 §3，docs/18 §1/§2/§2.4/§2.5）。
// 命令形态：`--uitest`，每行 `CHECK ui-<name> PASS|FAIL <detail>`，末行 UI-OK / UI-FAIL n=<k>。
// 判据口径来自文档，不由实现自证：
//   docs/18 §1     托盘恰有五个顶层项，顺序固定，「暂停全局输入钩子」是强制项不可隐藏
//   docs/18 §2/§2.5 主控面板四页齐全、面板单实例、关闭面板不退出进程
//   docs/18 §2.4   无宠物时性能页显示「尚无类似」而不是编造数字
//   docs/18 §3     授权弹窗三选一，Tier 2 的「仅本次」置灰
// 无人值守：全部控件树只 CreateControl 不 Show，任何一步失败都落成 FAIL 行而不抛。
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

    public static int Run(string[] args)
    {
        int logIdx = Array.FindIndex(args, a => string.Equals(a, "--log", StringComparison.Ordinal));
        _logPath = logIdx >= 0 && args.Length > logIdx + 1 ? args[logIdx + 1] : null;

        int failures = 0;
        try
        {
            TrayItems();
            TrayHookSticky();
            PanelPagesCheck();
            PanelSingleton();
            RosterReadOnly();
            AuthorizationDialogCheck();
            ConsentUiChecks.Run(Emit, Check);
            PerfEmptyHonest();
            PerfPageSample();
            // RFC-30 §4：逐宠物行、归属规则、超限计数是否真的会动。
            PerfChecks.Run(Check);
            KeyboardHookContract();
            ResidentLoopChecks.Run(Check);
            ImportChecks.Run(Check);
            SettingsChecks.Run(Check);
            // RFC-35 §4：窗口鼠标消息 → 交互会话（ui-pointer-*）。
            PointerWiringChecks.Run((name, ok, detail) => Check(name, ok, detail));
            // RFC-31 §4：命中图参与穿透（ui-hitmap-*）；RFC-32 §4：单实例锁（ui-single-instance-*）。
            HitMapChecks.Run(Check);
            InstanceChecks.Run(Check);
            // RFC-36 §4：宿主 Start 之前的一致性读取不许干等调度器（ui-compose-read-before-start）。
            HostComposeChecks.Run(Check);
            OnboardingChecks.Run(Check);
            OnboardingConsentChecks.Run(Check);
            DiagnosticChecks.Run(Check);
            DiagnosticBundleChecks.Run(Check);
            AuditTrailChecks.Run(Check);
            // RFC-21 §4：静态节流分档的判据（ui-throttle-*）。判据体在 ThrottleChecks。
            ThrottleChecks.Run(Check);
            failures = _failed;
        }
        catch (Exception error)
        {
            Emit($"CHECK ui-crash FAIL {error.GetType().Name}: {error.Message}");
            failures = _failed + 1;
        }

        Emit(failures == 0 ? "UI-OK" : $"UI-FAIL n={failures}");
        return failures == 0 ? 0 : failures;
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
    /// 判据落地：FAIL 必须计入 failures —— 一条只打印不计数、最后仍打 OK 的门禁，
    /// 比没有门禁更坏（RFC-04 §3「永久红的门禁只有两种结局」的反面）。
    /// </summary>
    private static void Check(string name, bool ok, string detail)
    {
        Emit($"CHECK {name} {(ok ? "PASS" : "FAIL")} {detail}");
        if (!ok)
        {
            _failed++;
        }
    }

    private static int _failed;

    /// <summary>§1：五项、顺序固定、id 稳定。</summary>
    private static int TrayItems()
    {
        int failures = 0;
        UiCommand noop = Noop;
        using var tray = new TrayHost(
            "sample-pet", noop, noop, noop, noop, noop, noop, noop, noop);
        IReadOnlyList<string> order = tray.ItemOrder;
        bool expected = order.Count == 5
            && order[0] == "toggle-all"
            && order[1] == "panel"
            && order[2] == "pet"
            && order[3] == "pause-hooks"
            && order[4] == "exit";
        Check("ui-tray-items", expected, "items=" + string.Join(",", order));
        return failures;
    }

    /// <summary>§1 强制项：暂停钩子必须始终可见（不能被隐藏）。</summary>
    private static int TrayHookSticky()
    {
        int failures = 0;
        UiCommand noop = Noop;
        using var tray = new TrayHost(
            "sample-pet", noop, noop, noop, noop, noop, noop, noop, noop);
        Check("ui-tray-hook-item-sticky", tray.HookItemVisible, $"visible={tray.HookItemVisible}");
        return failures;
    }

    /// <summary>§2.5：四页齐全且 id 稳定（不按标题文字断言）。</summary>
    private static int PanelPagesCheck()
    {
        int failures = 0;
        using var panel = new ControlPanel(() => UiSnapshot.Empty, showWindow: false);
        IReadOnlyList<string> ids = panel.PageIds;
        bool ok = ids.Count == 4
            && ids[0] == PanelPages.Pets
            && ids[1] == PanelPages.Plugins
            && ids[2] == PanelPages.Perf
            && ids[3] == PanelPages.Settings;
        Check("ui-panel-pages", ok, "pages=" + string.Join(",", ids));

        // 切页：每一页都必须真的存在（未知页 → 显式异常，不是静默失败）
        bool switched = true;
        foreach (string id in PanelPages.All)
        {
            try
            {
                panel.ShowPage(id);
                switched &= string.Equals(panel.CurrentPage, id, StringComparison.Ordinal);
            }
            catch (ArgumentOutOfRangeException)
            {
                switched = false;
            }
        }

        Check("ui-panel-switch", switched, $"last={panel.CurrentPage}");
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
        UiSnapshot snapshot = UiRoster.Build(null, null, PerfSummary.Empty);
        Check("ui-roster-readonly",
            snapshot.Pets.Count == 0 && snapshot.Plugins.Count == 0 && snapshot.Perf.IsEmpty,
            $"pets={snapshot.Pets.Count} plugins={snapshot.Plugins.Count} perf-empty={snapshot.Perf.IsEmpty}");
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

    /// <summary>§2.4：无宠物时性能页必须明说「尚无宠物运行」，不得编造数字。</summary>
    private static int PerfEmptyHonest()
    {
        int failures = 0;
        using var page = new PerfPage(() => UiSnapshot.Empty);
        page.Bind(PerfSummary.Empty);
        Check("ui-perf-empty-honest", page.RowCount == 0 && page.SummaryText.Contains("尚无宠物运行", StringComparison.Ordinal),
            $"rows={page.RowCount} summary={page.SummaryText}");
        return failures;
    }

    /// <summary>
    /// §2.4：有数据时行数与摘要如实反映快照。
    /// RFC-30 §3.4 之前这一条断的是「降级 1」——那个 1 是判据自己塞进 PerfSummary 的，
    /// 面板当时**没有任何接收口**能拿到降级数，所以那条断言其实在给「编造的数字」盖章。
    /// 现在降级/隔离区没有接收口就写「未接」，本判据改成断这一条。
    /// </summary>
    private static int PerfPageSample()
    {
        int failures = 0;
        using var page = new PerfPage(() => UiSnapshot.Empty);
        var rows = new List<PerfLine>
        {
            new("sample-pet", "0.05 %", "12.0 MB", "1.2 ms", 3),
        };
        page.Bind(new PerfSummary("60.0s", 1, 0, rows));
        Check("ui-perf-rows", page.RowCount == 1 && page.SummaryText.Contains("未接", StringComparison.Ordinal),
            $"rows={page.RowCount} summary={page.SummaryText}");
        return failures;
    }

    /// <summary>
    /// docs/14:110 + docs/16:56：键盘钩子的两条硬约束。
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
