// --eventtest 的「跳过」那一半（拆出以守住 D-03c 的单文件 400 行上限；先例 EventSelfTest.Log.cs）。
//
// 本文件是判据体检 S1（2026-10-25）的落点：--eventtest 的 SKIP 曾经**既不计绿也不计红** ——
// 原来是 Run() 里一个 `List<string> skipped` 加三条硬编码常量、无条件 Add，然后只打一行
// `SKIP-NOT-GREEN`，末行照样 EVENT-OK + exit 0，退出码照样 0。于是「事件源缺失」永久失明，
// 而 D-16 的门禁又是用 marker 判绿的，更看不见它。现在 SKIP 走 SkipLedger（见 SkipLedger.cs）：
// 收尾报 pass/skip/fail，可放过的 SKIP 印进末行，**没人接的 SKIP 判红**。
using System;

namespace PetDock.Hosting;

internal static partial class EventSelfTest
{
    /// <summary>收尾账本：CHECK 行全部过它，SKIP 只从它出。</summary>
    private static readonly SkipLedger Ledger = new(Console.WriteLine);

    /// <summary>PASS 的条数（FAIL 由各判据方法自己的累加器数，收尾时一起交给账本）。</summary>
    private static int _passed;

    /// <summary>
    /// 判据落地：FAIL 必须计入 failures —— 一条只打印不计数、最后仍打 OK 的门禁，比没有门禁更坏
    /// （RFC-04 §3）。SKIP 走账本的第三种结局，**不计进 PASS**：`(skipped)` / `(unowned)` 标记
    /// 与 --uitest 端是同一个口径（SkipLedger.SkipKindOf）。
    /// </summary>
    private static void Check(ref int failures, string key, bool ok, string detail)
    {
        SkipKind? skip = SkipLedger.SkipKindOf(ok, detail);
        if (skip is SkipKind kind)
        {
            Ledger.Skip(kind, key, SkipLedger.ProbeOf(detail));
            return;
        }

        Console.WriteLine($"CHECK {key} {(ok ? "PASS" : "FAIL")} {detail}");
        if (ok)
        {
            _passed++;
        }
        else
        {
            failures++;
        }
    }

    /// <summary>收尾：把数好的 PASS/FAIL 交给账本，跑负例装置，打统计与 marker。</summary>
    private static int Report(string[] args, int failures)
    {
        Ledger.Adopt(_passed, failures);

        // 负例装置（--skip-probe=unowned|environment）：把一次跳过人为送进**真账本**，证明末行
        // marker 真的会因「没人接的 SKIP」改变结论。门禁自己也要能红得起来 —— 否则「改完还能红」
        // 这句话只能靠嘴说，而这一次要的是读数。
        if (SkipProbe(args) is SkipKind injected)
        {
            Ledger.Skip(injected, "eventtest-skip-probe", "injected-by=--skip-probe");
        }

        Ledger.Report("eventtest", "EVENT-OK", "EVENT-FAIL");
        return Ledger.Verdict;
    }

    private static SkipKind? SkipProbe(string[] args)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, "--skip-probe=unowned", StringComparison.Ordinal))
            {
                return SkipKind.Unowned;
            }

            if (string.Equals(arg, "--skip-probe=environment", StringComparison.Ordinal))
            {
                return SkipKind.Environment;
            }
        }

        return null;
    }

    /// <summary>
    /// 契约 §2.6：没有生产触发源的事件必须显式登记，不许静默略过。
    ///
    /// 2026-10-25 之前这里是三条**硬编码常量、无条件 Add**（原 EventSelfTest.cs:362-364），
    /// 只打一行 SKIP-NOT-GREEN、不进失败计数 —— 于是它们天天缺席却天天算绿（判据体检 S1）。
    /// 硬编码那句「no-producer」在 pet.state.migrated 上**今天就是假的**：
    /// src/Host/PersistentPetState.cs:287 正在发它。常量不会错，错的是没人再量一次。
    ///
    /// 现在每个事件先真扫一遍源码生产点，再由扫描结果决定结局：
    ///   · 扫描失败（找不到仓库根 / 名字不在目录里）→ 这条判据什么也没检查 → 红。
    ///   · 零生产点 → Skip(Unowned) → 红：不是环境缺失，是没人接。
    ///   · 有生产点 → PASS 断言（生产点被删掉就红），并把「本次运行发不出来」的原因与读数
    ///     登记成可放过的 SKIP。
    /// </summary>
    private static int EventSources()
    {
        int failures = 0;
        foreach ((string name, string key, string why) in UncoveredEvents)
        {
            int sites = SourceScan.CountProducerSites(name);
            string reading = $"catalog={name} producer-sites={sites} unexercised={why}";

            if (sites < 0)
            {
                Check(ref failures, $"event-source-{key}", false, $"source-scan-failed {reading}");
                continue;
            }

            if (sites == 0)
            {
                Ledger.Skip(SkipKind.Unowned, $"event-source-{key}", reading);
                continue;
            }

            Check(ref failures, $"event-source-{key}", true, reading);
            Ledger.Skip(SkipKind.Environment, $"event-unexercised-{key}", reading);
        }

        return failures;
    }

    /// <summary>
    /// 目录里登记了、但本自检跑不到端到端的那几个事件 —— 注意**不是**「无生产者」，那件事由
    /// 扫描判定，这里只登记「这次为什么发不出来」。两条 key.* 的真原因是授权门：docs/16 §3 把
    /// 它们的 Source 定为 `capability only`，没有插件持 input.keyboardHook 时不装钩子，
    /// 于是无人值守跑不出真实按键。pet.state.migrated 的真原因是需要一份旧版本的状态文件。
    /// </summary>
    private static readonly (string Name, string Key, string Why)[] UncoveredEvents =
    {
        (PetDock.Core.PetRuntime.EventCatalog.PetStateMigrated, "pet-state-migrated", "needs-legacy-state-file"),
        (PetDock.Core.PetRuntime.EventCatalog.KeyDown, "input-key-down", "capability-only-source-no-grantee"),
        (PetDock.Core.PetRuntime.EventCatalog.KeyUp, "input-key-up", "capability-only-source-no-grantee"),
    };
}
