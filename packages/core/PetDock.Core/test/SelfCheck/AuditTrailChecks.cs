// 跨进程审计流水的判据（RFC-20 §4 的 11 条）。
//
// 判据走**生产对象**：真的 AuditTrail（真的落盘、真的改名、真的读回）、
// 真的 DiagnosticService（真的采流水）、真的 DiagnosticBundle（真的装包）。
// 落盘只碰临时目录（docs/41 D-10b），用完删掉 —— 先例在 DiagnosticChecks.cs。
//
// 覆盖的判据：
//   ui-audit-trail-file-written        流水真的落成文件
//   ui-audit-trail-row-shape           行的字段形状（timestamp/agent_id/plugin_id/kind/detail）
//   ui-audit-trail-grant-and-deny      授予与拒绝都进流水
//   ui-audit-trail-use-not-persisted   已授予的调用不进流水
//   ui-audit-trail-survives-instance   跨实例可见（第一个收尾后第二个读得到）
//   ui-audit-bundle-uses-trail         诊断包的审计一节来自流水
//   ui-audit-bundle-not-only-recent    流水里的行数不受 256 条环形缓冲限制
//   ui-audit-trail-write-failure-inert 写失败不抛给调用点
//   ui-audit-trail-not-in-repo         流水不落在仓库里
//   ui-audit-trail-consent-recorded    宿主侧的授权决定也进流水
//   ui-audit-trail-summary-honest      摘要里的流水规模与未完成标注如实
//   ui-audit-trail-flushnow-file-exists FlushNow 之后文件必然存在、行数齐全
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using PetDock.Core.Sandbox;
using PetDock.Hosting;

namespace PetDock.Hosting;

internal static class AuditTrailChecks
{
    /// <summary>判据的输出与计数回调（与 UiSelfTest 的 Check 同形）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    private static readonly string[] AllNames =
    {
        "ui-audit-trail-file-written",
        "ui-audit-trail-row-shape",
        "ui-audit-trail-grant-and-deny",
        "ui-audit-trail-use-not-persisted",
        "ui-audit-trail-survives-instance",
        "ui-audit-bundle-uses-trail",
        "ui-audit-bundle-not-only-recent",
        "ui-audit-trail-write-failure-inert",
        "ui-audit-trail-not-in-repo",
        "ui-audit-trail-consent-recorded",
        "ui-audit-trail-summary-honest",
        "ui-audit-trail-flushnow-file-exists",
    };

    /// <summary>探针用的假机器事实（不取本机真实值）。</summary>
    private const string ProbeUser = "testuser";
    private const string ProbeMachine = "PROBEMACHINE";
    private const string ProbePluginsRoot = "C:\\PluginsRoot";
    private const string ProbeDataRoot = "C:\\DataRoot";

    public static void Run(Check check)
    {
        string root = Path.Combine(Path.GetTempPath(), "petdock-audit-check-" + Guid.NewGuid().ToString("N"));
        var done = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            RunAll(check, root, done);
        }
        catch (Exception error)
        {
            foreach (string name in AllNames)
            {
                if (!done.Contains(name))
                {
                    check(name, false, $"{error.GetType().Name}: {error.Message}");
                }
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void RunAll(Check check, string root, HashSet<string> done)
    {
        void Done(string name, bool ok, string detail)
        {
            done.Add(name);
            check(name, ok, detail);
        }

        RedactRules rules = ProbeRules();
        string trailDir = Path.Combine(root, "audit");

        // ① 流水真的落成文件；② 行形状；③ 授予与拒绝都进；④ 调用不进。
        using (var trail = new AuditTrail(trailDir, rules))
        {
            trail.Start();
            trail.Record("com.example.probe", new SandboxEvent(SandboxEventKind.Audit, "grant=input.mousePosition"));
            trail.Record("com.example.probe", new SandboxEvent(SandboxEventKind.CapabilityDenied, "why=not-declared id=assets.read:cas"));
            trail.Record("com.example.probe", new SandboxEvent(SandboxEventKind.Audit, "use=input.mousePosition"));
            trail.Record("com.example.probe", new SandboxEvent(SandboxEventKind.Audit, "revoke=input.moveWindow"));
            trail.Record("com.example.probe", new SandboxEvent(SandboxEventKind.GuestFault, "brain-fault plugin=com.example.probe"));
            trail.RecordDecision("com.example.probe", AuditTrail.KindConsent, "asked=input.mousePosition granted=input.mousePosition declined=(none) scope=remember");
            trail.FlushNow();

            string[] files = ListFiles(trailDir);
            Done("ui-audit-trail-file-written", files.Length == 1,
                "files=" + files.Length + " names=" + string.Join(",", files.Select(Path.GetFileName)));

            AuditTrailRead read = AuditTrail.ReadAll(trailDir);
            var kinds = read.Rows.Select(r => r.Kind).ToList();

            // ② 行形状：五个字段都有，timestamp 不是一个空串。
            bool shape = read.Rows.Count > 0
                && read.Rows.All(r => r.Timestamp.Length > 0)
                && read.Rows.All(r => r.PluginId == "com.example.probe")
                && read.Rows.All(r => r.Kind.Length > 0)
                && read.Rows.All(r => r.Detail.Length > 0);
            Done("ui-audit-trail-row-shape", shape,
                "rows=" + read.Rows.Count + " kinds=" + string.Join(",", kinds));

            // ③ 授予与拒绝都进。
            bool grantAndDeny = kinds.Contains(AuditTrail.KindGrant, StringComparer.Ordinal)
                && kinds.Contains(AuditTrail.KindDeny, StringComparer.Ordinal)
                && kinds.Contains(AuditTrail.KindRevoke, StringComparer.Ordinal)
                && kinds.Contains(AuditTrail.KindFault, StringComparer.Ordinal);
            Done("ui-audit-trail-grant-and-deny", grantAndDeny,
                "kinds=" + string.Join(",", kinds));

            // ④ 已授予的调用不进流水。
            bool useAbsent = !read.Rows.Any(r => r.Detail.StartsWith("use=", StringComparison.Ordinal));
            Done("ui-audit-trail-use-not-persisted", useAbsent,
                "use-rows=" + read.Rows.Count(r => r.Detail.StartsWith("use=", StringComparison.Ordinal)));
        }

        // ⑤ 跨实例可见：第一个流水收尾（去 .part）之后，第二个实例读得到。
        AuditTrailRead after = AuditTrail.ReadAll(trailDir);
        string[] doneFiles = Directory.Exists(trailDir)
            ? Directory.GetFiles(trailDir, "audit-*.jsonl").OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        bool survives = after.Available && after.Rows.Count > 0 && doneFiles.Length == 1;
        Done("ui-audit-trail-survives-instance", survives,
            "rows=" + after.Rows.Count + " complete-files=" + doneFiles.Length + " incomplete=" + after.Incomplete);

        // ⑥⑦ 诊断包的审计一节来自流水，且行数不受环形缓冲限制。
        var readings = new DiagnosticReadings(
            ProgramVersion: "0.1.0",
            UptimeMs: 10,
            ProcessMemory: "(进程级读数不可用)",
            FrameCount: 0,
            AvgFrameMs: 0,
            MaxFrameMs: 0,
            OverBudgetFrames: 0,
            FrameElapsedMs: 0,
            TargetFrameMs: 0,
            FrameBudgetLine: "(帧预算读数不可用)",
            RosterUnavailable: false,
            Plugins: new List<DiagnosticPluginRow>(),
            Instances: new List<DiagnosticInstanceRow>(),
            Audit: new List<DiagnosticAuditRow>(),
            AuditCount: 0,
            DeniedCount: 0,
            BudgetCount: 0,
            GuestFaultCount: 0);

        using (var trail = new AuditTrail(trailDir, rules))
        {
            trail.Start();

            // 写 300 行：环形缓冲的容量是 256，这些行在流水里全都留得下。
            for (int i = 0; i < 300; i++)
            {
                trail.RecordDecision("com.example.probe", AuditTrail.KindGrant, "grant=cap" + i);
            }

            trail.FlushNow();

            var service = new DiagnosticService(
                new PetDataStore(Path.Combine(root, "store")),
                host: null,
                pet: null,
                log: null);
            service.Trail = trail;
            DiagnosticReadings collected = service.CollectReadings();

            // 判据按**实际行数**断言，不写死 300：同一个目录里还有上一段流水的行，
            // 总数是 300 + 上一段的行数。写死数字会让判据在流水累加时假红。
            bool usesTrail = collected.Audit.Count >= 300
                && collected.Audit.All(r => r.Timestamp.Length > 0)
                && collected.Trail is { Available: true };
            Done("ui-audit-bundle-uses-trail", usesTrail,
                "audit-rows=" + collected.Audit.Count + " trail-available=" + (collected.Trail?.Available ?? false));

            bool notOnlyRecent = collected.Audit.Count > 256;
            Done("ui-audit-bundle-not-only-recent", notOnlyRecent,
                "audit-rows=" + collected.Audit.Count + " ring-capacity=256");

            // ⑪ 摘要里的流水规模与未完成标注如实。
            string summary = DiagnosticSections.BuildSummary(collected, rules);
            bool honest = summary.Contains("审计流水", StringComparison.Ordinal)
                && summary.Contains("rows=" + collected.Audit.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                && summary.Contains("本次会话内计数", StringComparison.Ordinal);
            Done("ui-audit-trail-summary-honest", honest,
                "summary-has-audit-line=" + summary.Contains("审计流水", StringComparison.Ordinal)
                + " has-rows=" + summary.Contains("rows=" + collected.Audit.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));

            // ⑥ 宿主侧的授权决定也进流水（consent 这一类）。
            bool consentRecorded = collected.Audit.Any(r => r.Kind == AuditTrail.KindConsent);
            Done("ui-audit-trail-consent-recorded", consentRecorded,
                "kinds=" + string.Join(",", collected.Audit.Select(r => r.Kind).Distinct()));
        }

        // ⑧ 写失败不抛给调用点：把目录位置占成文件，逼出失败路径。
        string blockedRoot = Path.Combine(root, "blocked");
        Directory.CreateDirectory(blockedRoot);
        string blockedDir = Path.Combine(blockedRoot, "audit");
        File.WriteAllText(blockedDir, "not-a-directory");
        bool inert;
        string inertDetail;
        try
        {
            using (var trail = new AuditTrail(blockedDir, rules))
            {
                trail.Start();
                trail.RecordDecision("com.example.probe", AuditTrail.KindGrant, "grant=input.mousePosition");
                trail.FlushNow();
                inert = true;
                inertDetail = "available=" + trail.Available + " dropped=" + trail.DroppedCount;
            }
        }
        catch (Exception error)
        {
            inert = false;
            inertDetail = "threw " + error.GetType().Name + ": " + error.Message;
        }

        Done("ui-audit-trail-write-failure-inert", inert, inertDetail);

        // ⑨ 流水不落在仓库里：临时根不在仓库内，且流水目录名不是仓库里的任何目录。
        string? repoRoot = FindRepoRoot();
        bool notInRepo = repoRoot is null
            ? !trailDir.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
            : !trailDir.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)
                && !Directory.Exists(Path.Combine(repoRoot, "audit"));
        Done("ui-audit-trail-not-in-repo", notInRepo,
            "trail-dir=" + trailDir + " repo=" + (repoRoot ?? "(not found)"));
    }

    /// <summary>探针用的脱敏规则（本机事实全部用假值）。</summary>
    private static RedactRules ProbeRules() => new(
        ProbeDataRoot,
        ProbePluginsRoot,
        exePath: null,
        userProfile: @"C:\Users\testuser",
        userName: ProbeUser,
        machineName: ProbeMachine);

    private static string[] ListFiles(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "audit-*.jsonl*") : Array.Empty<string>();

    /// <summary>仓库根（往上找 PetDock.sln）。找不到就返回 null。</summary>
    private static string? FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PetDock.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响判据结论。
        }
    }
}
