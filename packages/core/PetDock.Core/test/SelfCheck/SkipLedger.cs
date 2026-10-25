// 自检的第三种结局：SKIP。--eventtest 与 --uitest 共用这一个账本。
//
// 为什么需要它（2026-10-25 判据体检 S1/S2）：在它之前，两类自检的结局只有 PASS 与 FAIL，
// 于是「跳过」只能伪装成通过 ——
//   · --eventtest 把 SKIP 打成一行 SKIP-NOT-GREEN，却**不进失败计数**，末行照样 EVENT-OK + exit 0。
//     而那三条是 EventSelfTest.cs:362-364 的**硬编码常量、无条件 Add**：它们不读任何东西，
//     所以「每天必缺席」这句话永远为真，门禁对「事件源缺失」永久失明（D-16 用 EVENT-OK 判绿）。
//   · --uitest 连 SKIP 这个字都没有：ConsentPersistChecks / ConsentHostChecks 在无法断言时
//     主动 Report(check, ..., ok: true, "…(skipped)")，把跳过塞进 PASS。
// 「跳过即绿」不是某条判据测错，而是**红与绿的定义里有洞**。本文件就是补洞的那个拥有者。
//
// 口径（两类 SKIP，写死在这儿，调用方不许自己解释）：
//   · Environment —— 本机确实缺这个条件（没有该硬件 / 该外部进程 / 该授权）。可放过：
//     计入 `skipped=N` 并印在末行 marker 里，让读 marker 的人看得见，但不改变结论。
//   · Unowned —— 没有任何东西会产出它。这不是环境缺失，是没人接。**一律判红**。
// 登记一条 SKIP 必须附一次**探针读数**（producer-sites=N / no-declared-caps / …）：空读数说明
// 没人真的量过，一律降级成 Unowned。硬编码常量制造的 SKIP 走的正是这条路。
// 行内残留（如实登记，不假装它不存在）：C# 里没有编译期字面量检测，所以「这条读数是真读数、
// 还是又一句自称」这一层挡不住 —— 挡得住的是**读数必须原样打进行里**：每一行 SKIP 都带着它，
// 读的人自己看得到它是量出来的还是编出来的。
//
// 行形态沿用本仓既有先例 tests/fixtures/PetDock.EventReplay.Tests/Runner.cs：
//   CHECK <name> SKIP <读数>              ← 第三种结局，与 PASS/FAIL 同一行形态
//   STAT <command> pass=<p> skip=<s> unowned=<u> fail=<f> keys=<…>
//   <CMD>-OK skipped=<s>   /   <CMD>-FAIL n=<fail+unowned> skipped=<s>
// 与那份先例的 `--strict` 口径**故意不同**：那里 SKIP 要显式 --strict 才计入失败，因为那些 SKIP
// 是「核心侧入口尚未存在」的人工声明、由人逐条复核；这里 Unowned 默认判红，因为它是内核自己事件
// 目录上的接线缺口，不是待复核的声明。
using System;
using System.Collections.Generic;

namespace PetDock.Hosting;

/// <summary>SKIP 的两类口径（见文件头）。</summary>
internal enum SkipKind
{
    /// <summary>本机缺这个条件（无该硬件 / 外部进程 / 授权）：放过，但计入 skipped=N。</summary>
    Environment,

    /// <summary>没有任何东西会产出它：不是缺环境，是没人接。一律判红。</summary>
    Unowned,
}

/// <summary>账本的输出回调（D-03b：系统自带的同名泛型委托含黑名单子串，自声明代替，同 UiCommand）。</summary>
internal delegate void SkipEmit(string line);

/// <summary>判据回调（D-03b：同上，自声明代替系统自带的那个泛型委托）。</summary>
internal delegate void SkipCheck(string name, bool ok, string detail);

/// <summary>两类自检共用的第三种结局账本：pass / skip / fail 分开数，末行 marker 带上 skipped=N。</summary>
internal sealed class SkipLedger
{
    /// <summary>detail 里的跳过标记（既有三处 `…(skipped)` 调用点的口径）。</summary>
    internal const string SkippedMark = "(skipped)";

    /// <summary>detail 里的「没人接」标记：同一条 SKIP 通道，但判红。</summary>
    internal const string UnownedMark = "(unowned)";

    private readonly SkipEmit _emit;
    private readonly List<string> _skipKeys = new();

    internal SkipLedger(SkipEmit emit) => _emit = emit;

    internal int Passed { get; private set; }

    internal int Skipped { get; private set; }

    internal int Unowned { get; private set; }

    internal int Failed { get; private set; }

    /// <summary>判红的总数 = FAIL + 没人接的 SKIP。（可放过的 SKIP 不在其中，但末行会印出它。）</summary>
    internal int Verdict => Failed + Unowned;

    /// <summary>把调用方自己数好的 PASS/FAIL 合并进来（判据体沿用各自的累加器，账本不抢它的账）。</summary>
    internal void Adopt(int passed, int failed)
    {
        Passed += passed;
        Failed += failed;
    }

    internal void Pass(string name, string detail)
    {
        _emit($"CHECK {name} PASS {detail}");
        Passed++;
    }

    internal void Fail(string name, string detail)
    {
        _emit($"CHECK {name} FAIL {detail}");
        Failed++;
    }

    /// <summary>
    /// 判据落地的**唯一收尾**：三种结局都从这一处过账。
    /// `(name, ok, detail)` 这个回调形状没有地方放「跳过」，所以 `…(skipped)` 曾经只能塞进
    /// ok=true 一起算 PASS（2026-10-25 判据体检 S2 的那三处）。收在唯一一个函数里做，调用点就
    /// 再也藏不住一次跳过；判定口径在 SkipKindOf。
    /// </summary>
    internal void Check(string name, bool ok, string detail)
    {
        if (SkipKindOf(ok, detail) is SkipKind skip)
        {
            Skip(skip, name, ProbeOf(detail));
            return;
        }

        if (ok)
        {
            Pass(name, detail);
        }
        else
        {
            Fail(name, detail);
        }
    }

    /// <summary>
    /// 登记一条 SKIP。<paramref name="probe"/> 必须是**对本机现状的一次真实读数**（探针打出来的
    /// 原始值），不是「我认为」的自称：空读数说明没人量过，降级成 Unowned 判红。
    /// </summary>
    internal void Skip(SkipKind kind, string name, string probe)
    {
        _skipKeys.Add(name);
        if (kind == SkipKind.Unowned || probe.Length == 0)
        {
            Unowned++;
            _emit($"CHECK {name} FAIL unowned-skip probe={probe}");
            return;
        }

        Skipped++;
        _emit($"CHECK {name} SKIP {probe}");
    }

    /// <summary>收尾两行：一行可读统计（SKIP 的名字与条数都在），一行 marker。</summary>
    internal void Report(string command, string okMarker, string failMarker)
    {
        _emit($"STAT {command} pass={Passed} skip={Skipped} unowned={Unowned} fail={Failed}"
            + $" keys={string.Join(",", _skipKeys)}");
        _emit(Verdict == 0
            ? $"{okMarker} skipped={Skipped}"
            : $"{failMarker} n={Verdict} skipped={Skipped}");
    }

    /// <summary>
    /// 把 `(ok, detail)` 归类成第三种结局：返回非 null = 这一条是 SKIP。
    /// 三条规则，顺序就是优先级：FAIL 优先于 SKIP（失败的跳过仍然是失败，不许被标记洗掉）；
    /// 然后才认 detail 里的标记 —— 这是唯一能让 `(name, ok, detail)` 形状的回调表达第三种结局的
    /// 地方，所以它必须收在**唯一的收尾**上做，不能散在各调用点自己解释。
    /// </summary>
    internal static SkipKind? SkipKindOf(bool ok, string detail)
    {
        if (!ok)
        {
            return null;
        }

        if (detail.Contains(UnownedMark, StringComparison.Ordinal))
        {
            return SkipKind.Unowned;
        }

        return detail.Contains(SkippedMark, StringComparison.Ordinal) ? SkipKind.Environment : null;
    }

    /// <summary>SKIP 的读数 = detail 去掉标记本身（标记说的是「这是跳过」，读数才是量到了什么）。</summary>
    internal static string ProbeOf(string detail) =>
        detail.Replace(SkippedMark, string.Empty, StringComparison.Ordinal)
            .Replace(UnownedMark, string.Empty, StringComparison.Ordinal)
            .Trim();

    /// <summary>
    /// 账本自己的负例对照（每次自检都跑）：证明三种结局真的分得开，且**没人接的 SKIP 会改变结论**。
    /// 它用一个**脱离的**账本（写进一个 List，不碰真账本），所以不改变本次运行的统计；
    /// 结论作为一条正常判据交给调用的自检。没有这一条，「改了以后还能红」就只能靠嘴说。
    /// </summary>
    internal static void ControlCheck(SkipCheck check, string name)
    {
        var lines = new List<string>();
        var probe = new SkipLedger(lines.Add);
        probe.Pass("control-pass", "ok");
        probe.Skip(SkipKind.Environment, "control-env", "probe=environment-is-missing");
        probe.Skip(SkipKind.Unowned, "control-unowned", "probe=nobody-produces-it");
        probe.Skip(SkipKind.Environment, "control-empty-probe", string.Empty);
        probe.Report("control", "CONTROL-OK", "CONTROL-FAIL");

        var green = new List<string>();
        var greenProbe = new SkipLedger(green.Add);
        greenProbe.Skip(SkipKind.Environment, "control-env", "probe=environment-is-missing");
        greenProbe.Report("control", "CONTROL-OK", "CONTROL-FAIL");

        bool counted = probe.Passed == 1 && probe.Skipped == 1 && probe.Unowned == 2 && probe.Verdict == 2;
        bool markerChanges = lines.Exists(l => l.StartsWith("CONTROL-FAIL n=2 skipped=1", StringComparison.Ordinal));
        bool envKeepsMarker = green.Exists(l => string.Equals(l, "CONTROL-OK skipped=1", StringComparison.Ordinal));
        bool classified = SkipKindOf(true, "x (skipped)") == SkipKind.Environment
            && SkipKindOf(true, "x (unowned)") == SkipKind.Unowned
            && SkipKindOf(false, "x (skipped)") is null
            && ProbeOf("no-declared-caps (skipped)") == "no-declared-caps";

        check(name, counted && markerChanges && envKeepsMarker && classified,
            $"pass={probe.Passed} skip={probe.Skipped} unowned={probe.Unowned} fail={probe.Failed}"
            + $" unowned-changes-marker={Flag(markerChanges)} env-keeps-marker={Flag(envKeepsMarker)}"
            + $" classify-and-probe={Flag(classified)}");
    }

    private static string Flag(bool value) => value ? "true" : "false";

    /// <summary>
    /// 负例装置（`--skip-probe=unowned|environment`）：把一次跳过人为送进**真账本**，用来证明末行
    /// marker 真的会因「没人接的 SKIP」改变结论 —— 门禁自己也要能红得起来，否则「改完还能红」这句
    /// 话只能靠嘴说。默认（不带该旗标）返回 null，是一次 no-op，不影响正常运行的统计。
    /// 两类自检共用同一个装置，读数才可比。
    /// </summary>
    internal static SkipKind? ProbeFromArgs(string[] args)
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
}
