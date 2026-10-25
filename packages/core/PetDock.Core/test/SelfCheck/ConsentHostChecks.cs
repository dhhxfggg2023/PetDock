// 授权链路的宿主侧自检（RFC-13 §3 判据表）。
// 为什么单独一个文件：PluginHostSelfTest.cs 已接近 D-03c 的单文件行数上限。
// 判据全部对着 docs/18 §3 与 docs/14 §2 的字面要求写，不对着实现自证：
//   docs/18 §3   拒绝后该插件不允许重试同一能力
//   docs/18 §3   「仅本次」下次装载再问；「允许（记住这个决定）」本会话不再问
//   docs/14 §2   一次断言：安装时一次性提交能力清单
//   docs/14 §2   input.mousePosition「Tier 0 可请求」→ tier 0 也必须有闸门与租约
using System;
using System.Collections.Generic;
using System.Linq;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

/// <summary>判据回调（key / 是否通过 / 详情）。D-03b：系统自带的同名泛型委托含黑名单子串，自声明代替。</summary>
internal delegate void HostCheck(string name, bool ok, string detail);

/// <summary>记录端口被问了几次、每次问了什么（自检的假用户）。</summary>
internal sealed class RecordingConsentPort : ICapabilityConsentPort
{
    private readonly Func<ConsentRequest, ConsentAnswer?> _decide;

    public RecordingConsentPort(Func<ConsentRequest, ConsentAnswer?> decide) => _decide = decide;

    public List<ConsentRequest> Questions { get; } = new();

    public ConsentAnswer? Ask(ConsentRequest request)
    {
        Questions.Add(request);
        return _decide(request);
    }
}

/// <summary>宿主侧授权判据。结果并入 --hosttest 的失败计数。</summary>
internal static class ConsentHostChecks
{
    /// <summary>跑宿主侧全部授权判据，返回失败条数。</summary>
    public static int Run(PluginHost host, PluginManifest manifest, HostCheck check)
    {
        int failed = 0;

        // ① 端口为 null 时旧行为不变：声明集全授（既有 leases-granted 判据依赖这条）
        IReadOnlyList<string> legacy = host.ResolveGranted(manifest);
        int wellKnown = manifest.Capabilities.Count(c => CapabilityIds.IsWellKnown(c));
        failed += Report(check, "consent-null-port-legacy-grant", wellKnown == legacy.Count,
            $"granted={legacy.Count} declared-wellknown={wellKnown}");

        // ② 端口注入后授予子集生效：只给一项，其余不给
        var subsetPort = new RecordingConsentPort(request =>
            new ConsentAnswer(ConsentScope.Session, new[] { CapabilityIds.InputMousePosition }));
        using (PluginHost scoped = new(ProbeRoot(), autoLoad: false, consent: subsetPort))
        {
            IReadOnlyList<string> granted = scoped.ResolveGranted(manifest);
            bool onlyOne = granted.Count == 1 && granted.Contains(CapabilityIds.InputMousePosition);
            failed += Report(check, "consent-subset-granted", onlyOne && subsetPort.Questions.Count == 1,
                $"granted={string.Join(",", granted)} questions={subsetPort.Questions.Count}");
        }

        // ③ 拒绝后不再问：用户拒绝全部 → 第二次调用完全不问端口
        var denyPort = new RecordingConsentPort(_ => ConsentAnswer.Reject);
        using (PluginHost denying = new(ProbeRoot(), autoLoad: false, consent: denyPort))
        {
            IReadOnlyList<string> first = denying.ResolveGranted(manifest);
            IReadOnlyList<string> second = denying.ResolveGranted(manifest);
            failed += Report(check, "consent-deny-not-retried",
                first.Count == 0 && second.Count == 0 && denyPort.Questions.Count == 1,
                $"questions={denyPort.Questions.Count} first={first.Count} second={second.Count}");
        }

        // ④ 「仅本次」下次装载再问：Scope=Once 不记授予
        var oncePort = new RecordingConsentPort(request =>
            new ConsentAnswer(ConsentScope.Once, new[] { CapabilityIds.InputMousePosition }));
        using (PluginHost once = new(ProbeRoot(), autoLoad: false, consent: oncePort))
        {
            once.ResolveGranted(manifest);
            once.ResolveGranted(manifest);
            failed += Report(check, "consent-once-asks-again", oncePort.Questions.Count == 2,
                $"questions={oncePort.Questions.Count}");
        }

        // ⑤ 「允许（记住这个决定）」本会话不再问
        var rememberPort = new RecordingConsentPort(request =>
            new ConsentAnswer(ConsentScope.Session, request.AskableIds.ToArray()));
        using (PluginHost remembering = new(ProbeRoot(), autoLoad: false, consent: rememberPort))
        {
            remembering.ResolveGranted(manifest);
            remembering.ResolveGranted(manifest);
            failed += Report(check, "consent-session-remembered", rememberPort.Questions.Count == 1,
                $"questions={rememberPort.Questions.Count}");
        }

        return failed;
    }

    /// <summary>
    /// tier 0 也产租约（docs/14 §2 明写 input.mousePosition「Tier 0 可请求」）。
    /// 仓库插件以 tier 0 为主，若它们不产租约，docs/16:56 的钩子门永远不成立。
    /// </summary>
    public static int Tier0Leases(PluginHost host, HostCheck check)
    {
        IReadOnlyList<PluginHandle> handles = host.HandlesSnapshot();
        var tier0 = handles.Where(h => h.Manifest.Tier == 0).ToArray();
        bool allHaveGate = tier0.All(h => h.Gate is not null);
        bool leasesMatchGate = tier0.All(h => h.Leases.Count == (h.Gate?.Granted.Count ?? -1));
        return Report(check, "t0-leases-granted", allHaveGate && leasesMatchGate,
            $"tier0={tier0.Length} with-gate={tier0.Count(h => h.Gate is not null)} "
            + $"leases={string.Join(",", tier0.Select(h => $"{h.Manifest.Id}:{h.Leases.Count}/{h.Gate?.Granted.Count ?? -1}"))}");
    }

    /// <summary>插件根（探测失败给空串：ResolveGranted 只读 manifest，不碰文件系统）。</summary>
    private static string ProbeRoot() => PluginHost.ProbePluginsRoot() ?? string.Empty;

    /// <summary>
    /// 收窄授权（RFC-13 §2.6）：复核后不该再保留的能力被撤销。
    /// 判据：收窄后活动租约只剩 keep 里的那些；且**不能新增授予**
    /// （docs/14 §2 一次断言：keep 里没被授予过的能力不会因此拿到授予）。
    /// </summary>
    public static int RestrictConsentCheck(PluginHost host, string pluginId, HostCheck check)
    {
        PluginHandle? target = host.HandlesSnapshot().FirstOrDefault(h => h.Manifest.Id == pluginId);
        if (target is null)
        {
            return Report(check, "consent-narrow-only-shrinks", false, $"plugin={pluginId} not-found");
        }

        // 取**活动租约**而不是闸门的授予集：禁用会把闸门里的授予逐项撤销（记录保留），
        // 恢复只把租约置回活动而不重新授予——所以闸门集此时可能已空，活动租约才反映
        // 「现在这个插件手上真的有什么」。
        IReadOnlyList<string> granted = target.Leases
            .Where(l => l.Active)
            .Select(l => l.Id)
            .ToArray();
        if (granted.Count == 0)
        {
            return ReportSkip(check, "consent-narrow-only-shrinks", "no-grants-to-narrow");
        }

        string keep = granted[0];
        bool changed = host.RestrictConsent(pluginId, new[] { keep });
        IReadOnlyList<string> after = host.HandlesSnapshot()
            .First(h => h.Manifest.Id == pluginId)
            .Leases.Where(l => l.Active).Select(l => l.Id).ToArray();

        bool onlyKept = after.Count == 1 && after.Contains(keep);
        return Report(check, "consent-narrow-only-shrinks", changed && onlyKept,
            $"before={string.Join(",", granted)} after={string.Join(",", after)} changed={changed}");
    }

    private static int Report(HostCheck check, string name, bool ok, string detail)
    {
        check(name, ok, detail);
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 登记一次**跳过**（不是 PASS）：本机这次运行里没有可断言的对象，判据无从下手。
    /// 2026-10-25 判据体检 S2 之前，这里是 `Report(…, true, "…(skipped)")` —— 跳过被塞进
    /// ok=true 一起算绿（2026-10-20 已为这一条付过一次账）。现在标记由消费方自检的收尾账本
    /// （SkipLedger）判成第三种结局：--hosttest 计入 `skipped=N` 并印在末行 marker 上。
    /// </summary>
    private static int ReportSkip(HostCheck check, string name, string reason) =>
        Report(check, name, true, reason + " " + SkipLedger.SkippedMark);
}
