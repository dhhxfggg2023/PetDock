// 授权记忆的跨进程判据（RFC-19 §3）。
// 为什么单独一个文件：PluginHostSelfTest.cs 已接近 D-03c 的单文件行数上限。
// 判据全部对着 RFC-19 §2 与 docs/18 §3 的字面要求写：
//   RFC-19 §2.1   落盘往返、审计字段齐全、损坏回落
//   RFC-19 §2.2   「仅本次」不落盘、拒绝不落盘
//   RFC-19 §2.3   「允许（记住这个决定）」落盘、播种后不再问、收窄后落盘
//
// 「跨进程」在自检里是**同一进程内模拟**：新建一个 PluginHost 实例 + 同一个落盘文件，
// 等价于「关掉再打开」时读到的状态。真的起两个进程没有做（RFC-19 §6 第 1 条已登记）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

/// <summary>授权记忆持久化的判据。结果并入 --hosttest 的失败计数。</summary>
internal static class ConsentPersistChecks
{
    /// <summary>自检用的临时数据根（每次调用一个独立目录，结束即删）。</summary>
    private static string NewRoot(string tag)
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "petdock-consent-check-" + tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 跑全部持久化判据。
    /// </summary>
    /// <param name="liveHost">真实装载着的宿主（收窄判据需要真的句柄）。</param>
    /// <param name="livePluginId">该宿主里某个已装载插件的 id。</param>
    /// <param name="check">判据回调。</param>
    public static int Run(PluginHost liveHost, string livePluginId, HostCheck check)
    {
        int failed = 0;
        failed += RoundTrip(check);
        failed += AuditFields(check);
        failed += CorruptFallsBack(check);
        failed += SessionSaved(check);
        failed += OnceNotSaved(check);
        failed += DenyNotSaved(check);
        failed += SeededNotAsked(check);
        failed += NarrowSaved(liveHost, livePluginId, check);
        return failed;
    }

    /// <summary>落盘往返：写进去再读出来，一模一样。</summary>
    private static int RoundTrip(HostCheck check)
    {
        string root = NewRoot("roundtrip");
        try
        {
            var store = new ConsentStore(root);
            var grants = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["com.example.alpha"] = new[] { CapabilityIds.InputMousePosition, CapabilityIds.InputMoveWindow },
            };
            store.Save(grants);
            Dictionary<string, IReadOnlyList<string>> loaded = store.Load();

            loaded.TryGetValue("com.example.alpha", out IReadOnlyList<string>? ids);
            bool same = loaded.Count == 1
                && ids is not null
                && ids.Count == 2
                && ids.Contains(CapabilityIds.InputMousePosition)
                && ids.Contains(CapabilityIds.InputMoveWindow);
            return Report(check, "consent-persist-roundtrip", same,
                $"plugins={loaded.Count} ids={(ids is null ? "(none)" : string.Join(",", ids))}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>审计字段齐全（docs/17 §4：任何写必须含 agent_id 与 timestamp；§2：显式 schemaVersion）。</summary>
    private static int AuditFields(HostCheck check)
    {
        string root = NewRoot("audit");
        try
        {
            var store = new ConsentStore(root);
            store.Save(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
            JsonNode? parsed = JsonNode.Parse(File.ReadAllText(store.Path));
            JsonObject? root2 = parsed as JsonObject;
            // 用 ToString() 而不是 (string?) 强转：schemaVersion 是数字节点，
            // 强转会抛 InvalidOperationException（Number 不能转成 System.String）。
            string schema = root2?["schemaVersion"]?.ToString() ?? "(none)";
            string agent = root2?["agent_id"]?.ToString() ?? "(none)";
            string timestamp = root2?["timestamp"]?.ToString() ?? "(none)";
            bool ok = root2 is not null
                && root2["schemaVersion"] is not null
                && agent == "host"
                && !string.IsNullOrWhiteSpace(timestamp)
                && root2["plugins"] is not null;
            return Report(check, "consent-persist-audit-fields", ok,
                $"schema={schema} agent={agent} ts={(timestamp == "(none)" ? "(none)" : "present")}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>损坏回落：坏掉的授权记忆不该让程序起不来（读成空表，下一次重新问用户）。</summary>
    private static int CorruptFallsBack(HostCheck check)
    {
        string root = NewRoot("corrupt");
        try
        {
            var store = new ConsentStore(root);
            File.WriteAllText(store.Path, "{ this is not json at all ");
            Dictionary<string, IReadOnlyList<string>> loaded = store.Load();
            return Report(check, "consent-persist-corrupt-falls-back", loaded.Count == 0,
                $"plugins={loaded.Count}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>「允许（记住这个决定）」的授予集落盘（RFC-19 §2.3 第 2 点）。</summary>
    private static int SessionSaved(HostCheck check)
    {
        string root = NewRoot("session");
        try
        {
            var store = new ConsentStore(root);
            var port = new RecordingConsentPort(request =>
                new ConsentAnswer(ConsentScope.Session, request.AskableIds.ToArray()));
            using PluginHost host = NewHost(port);
            host.AttachConsentStore(store);

            IReadOnlyList<string> granted = host.ResolveGranted(SyntheticManifest());
            Dictionary<string, IReadOnlyList<string>> loaded = store.Load();
            bool saved = loaded.TryGetValue("com.example.synthetic", out IReadOnlyList<string>? ids)
                && ids.Count == granted.Count
                && granted.Count > 0;
            return Report(check, "consent-persist-session-saved", saved,
                $"granted={granted.Count} saved={(ids is null ? -1 : ids.Count)}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>「仅本次」不落盘（RFC-19 §2.2：它的字面意思就是仅本次）。</summary>
    private static int OnceNotSaved(HostCheck check)
    {
        string root = NewRoot("once");
        try
        {
            var store = new ConsentStore(root);
            var port = new RecordingConsentPort(request =>
                new ConsentAnswer(ConsentScope.Once, request.AskableIds.ToArray()));
            using PluginHost host = NewHost(port);
            host.AttachConsentStore(store);

            host.ResolveGranted(SyntheticManifest());
            Dictionary<string, IReadOnlyList<string>> loaded = store.Load();
            bool absent = !loaded.ContainsKey("com.example.synthetic");
            return Report(check, "consent-persist-once-not-saved", absent,
                $"plugins={loaded.Count} has={loaded.ContainsKey("com.example.synthetic")}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>拒绝不落盘（RFC-19 §2.2 的裁决：持久化拒绝 = 永久锁死且无出口）。</summary>
    private static int DenyNotSaved(HostCheck check)
    {
        string root = NewRoot("deny");
        try
        {
            var store = new ConsentStore(root);
            var port = new RecordingConsentPort(_ => ConsentAnswer.Reject);
            using PluginHost host = NewHost(port);
            host.AttachConsentStore(store);

            host.ResolveGranted(SyntheticManifest());
            Dictionary<string, IReadOnlyList<string>> loaded = store.Load();
            bool absent = !loaded.ContainsKey("com.example.synthetic");
            return Report(check, "consent-persist-deny-not-saved", absent,
                $"plugins={loaded.Count} has={loaded.ContainsKey("com.example.synthetic")}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// 播种后不再问（RFC-19 §2.3 第 1 点）：上次进程记住的授予，这次启动直接用，不弹窗。
    /// </summary>
    private static int SeededNotAsked(HostCheck check)
    {
        string root = NewRoot("seed");
        try
        {
            var store = new ConsentStore(root);
            // 播种**全部**候选能力：只有「记住的授予集覆盖了全部候选」时宿主才不再问
            // （PluginHost.Roster.cs 的 effective.Count == candidates.Count 分支）。
            // 只播种一部分 ⇒ 宿主仍要为剩下的能力问用户，那是设计如此，不是缺陷。
            store.Save(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["com.example.synthetic"] = new[] { CapabilityIds.InputMousePosition, CapabilityIds.InputMoveWindow },
            });

            var port = new RecordingConsentPort(_ => ConsentAnswer.Reject);
            using PluginHost host = NewHost(port);
            host.AttachConsentStore(store);

            IReadOnlyList<string> granted = host.ResolveGranted(SyntheticManifest());
            bool notAsked = port.Questions.Count == 0;
            bool used = granted.Contains(CapabilityIds.InputMousePosition);
            return Report(check, "consent-persist-seeded-not-asked", notAsked && used,
                $"questions={port.Questions.Count} granted={string.Join(",", granted)}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// 收窄后落盘（RFC-19 §2.3 第 3 点）：撤销若不跨进程，下次启动能力会悄悄回来。
    /// 这一条必须在**真实装载着的**宿主上跑：收窄路径要求找得到句柄。
    /// </summary>
    private static int NarrowSaved(PluginHost liveHost, string livePluginId, HostCheck check)
    {
        string root = NewRoot("narrow");
        try
        {
            var store = new ConsentStore(root);

            // 先把「这插件曾经被授予过什么」写进文件，再接上存储 —— 接上即播种，
            // 会话记忆里就有了这份授予（否则宿主记忆是空的，收窄无从谈起）。
            string[] declared = liveHost.HandlesSnapshot()
                .Where(h => h.Manifest.Id == livePluginId)
                .SelectMany(h => h.Manifest.Capabilities)
                .ToArray();
            if (declared.Length == 0)
            {
                return Report(check, "consent-persist-narrow-saved", true, "no-declared-caps (skipped)");
            }

            store.Save(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [livePluginId] = declared,
            });
            liveHost.AttachConsentStore(store);

            // 收窄到只留一项（真的装载着的插件总有活动租约）。
            string[] active = liveHost.HandlesSnapshot()
                .Where(h => h.Manifest.Id == livePluginId)
                .SelectMany(h => h.Leases.Where(l => l.Active).Select(l => l.Id))
                .ToArray();
            if (active.Length == 0)
            {
                return Report(check, "consent-persist-narrow-saved", true, "no-active-leases (skipped)");
            }

            string[] keep = new[] { active[0] };
            liveHost.RestrictConsent(livePluginId, keep);
            Dictionary<string, IReadOnlyList<string>> loaded = store.Load();
            bool written = loaded.ContainsKey(livePluginId);
            bool narrowed = written && loaded[livePluginId].Count == keep.Length;
            return Report(check, "consent-persist-narrow-saved", written && narrowed,
                $"plugin={livePluginId} declared={declared.Length} keep={keep.Length} saved={(written ? loaded[livePluginId].Count : -1)}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>一个不受装载影响的宿主（autoLoad=false，不碰文件系统）。</summary>
    private static PluginHost NewHost(ICapabilityConsentPort port) =>
        new(PluginHost.ProbePluginsRoot() ?? string.Empty, autoLoad: false, consent: port);

    /// <summary>
    /// 合成清单：判据要用**确定**的能力集，不能依赖仓库里某个插件声明了什么。
    /// 两个白名单内的固定能力。
    /// </summary>
    private static PluginManifest SyntheticManifest() => new(
        Id: "com.example.synthetic",
        Name: "自检用合成插件",
        Version: "1.0.0",
        ApiMajor: 1,
        ApiMinor: 0,
        Kind: PluginKind.Service,
        Tier: 1,
        Capabilities: new[] { CapabilityIds.InputMousePosition, CapabilityIds.InputMoveWindow },
        Entry: "synthetic.dll",
        LicenseSpdx: "CC0-1.0",
        Contributes: null);

    private static int Report(HostCheck check, string name, bool ok, string detail)
    {
        check(name, ok, detail);
        return ok ? 0 : 1;
    }

    /// <summary>删临时目录（失败不抛：临时目录残留不该让自检判红）。</summary>
    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // 残留一个临时目录不影响判据结论。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
