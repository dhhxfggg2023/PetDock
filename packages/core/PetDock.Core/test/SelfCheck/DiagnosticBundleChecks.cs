// 诊断包与设置页入口的判据（RFC-17 §4 的后 8 条）。
//
// 为什么与 DiagnosticChecks.cs 分开：单个文件要过 D-03c 的 400 行上限
// （该上限对 test/ 目录同样生效）。
//
// 判据走**生产对象**：真的 DiagnosticBundle（真的写 zip）、真的 DiagnosticService
// （真的采集读数、真的失败路径）、真的 ControlPanel（真的控件树）。
// 唯一被造出来的是「读数」本身（DiagnosticReadings 是纯数据 record）——
// 这正是它被设计成纯数据的原因：组装这一侧可以脱离宿主单独验证。
//
// 覆盖的判据：
//   ui-diag-capability-ids-well-known  能力 id 全在 docs/14 §2 集合内
//   ui-diag-forbidden-capability-absent 禁止项绝不出现
//   ui-diag-bundle-excludes-user-activity 包里没有用户使用数据
//   ui-diag-bundle-zip-roundtrip       zip 能被读回来且 sha256 对得上
//   ui-diag-bundle-labels-process-level 内存读数标着「进程」
//   ui-diag-bundle-empty-is-honest     空态不编造数字
//   ui-diag-panel-entries-wired        设置页两个入口不再是「(未接线)」
//   ui-diag-export-failure-is-inline   导出失败说清楚原因（内联，不弹窗）
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using PetDock.Core.Ui;
using PetDock.Hosting;

namespace PetDock.Hosting;

internal static class DiagnosticBundleChecks
{
    /// <summary>判据的输出与计数回调（与 UiSelfTest 的 Check 同形）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    private static readonly string[] AllNames =
    {
        "ui-diag-capability-ids-well-known",
        "ui-diag-forbidden-capability-absent",
        "ui-diag-bundle-excludes-user-activity",
        "ui-diag-bundle-zip-roundtrip",
        "ui-diag-bundle-labels-process-level",
        "ui-diag-bundle-empty-is-honest",
        "ui-diag-panel-entries-wired",
        "ui-diag-export-failure-is-inline",
    };

    /// <summary>探针用的假机器事实（不取本机真实值）。</summary>
    private const string ProbeUser = "testuser";
    private const string ProbeMachine = "PROBEMACHINE";

    /// <summary>包内不该出现的用户使用数据键名（docs/18 §2.4：不收集使用数据）。</summary>
    private static readonly string[] UserActivityKeys =
    {
        "输入强度", "在线时间", "语音", "inputIntensity", "onlineTime", "voiceInput",
    };

    public static void Run(Check check)
    {
        string root = Path.Combine(Path.GetTempPath(), "petdock-diag-bundle-" + Guid.NewGuid().ToString("N"));
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
        string diagDir = Path.Combine(root, "diagnostics");
        string logDir = Path.Combine(root, "logs");
        var bundle = new DiagnosticBundle(diagDir, logDir, rules);

        // 造一份「有内容」的读数：白名单能力 id + 两个明令禁止项。
        DiagnosticReadings readings = BuildReadings();

        // 写一个日志文件进 logs/，证明包会把日志收进去。
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "kernel-20260101-000000.log"), "timestamp=x agent_id=host level=info msg=probe\n");

        DiagnosticExportResult exported = bundle.Export(readings);
        if (!exported.Ok || exported.BundlePath is null || !File.Exists(exported.BundlePath))
        {
            foreach (string name in AllNames)
            {
                Done(name, false, "export failed: " + exported.Message);
            }

            return;
        }

        var entries = ReadZip(exported.BundlePath);

        // ① 白名单能力 id 全在集合内。
        string pluginsJson = Text(entries, DiagnosticBundle.PluginsPath);
        var pluginArray = JsonNode.Parse(pluginsJson)?["plugins"]?.AsArray();
        var declared = pluginArray?[0]?["declaredCapabilityIds"]?.AsArray();
        bool allWellKnown = declared is not null
            && declared.Count > 0
            && declared.All(node =>
            {
                string id = node?.GetValue<string>() ?? string.Empty;
                return PetDock.Core.Sandbox.CapabilityIds.IsWellKnown(id);
            });
        Done("ui-diag-capability-ids-well-known", allWellKnown,
            "declared=" + (declared is null ? "(none)" : string.Join(",", declared.Select(n => n?.GetValue<string>()))));

        // ② 禁止项绝不出现（整包文本扫一遍，不只是 plugins.json）。
        string wholeBundle = string.Join("\n", entries.Select(e => e.Value));
        bool forbiddenAbsent = !wholeBundle.Contains("assets.write:cas", StringComparison.Ordinal)
            && !wholeBundle.Contains("audio.play", StringComparison.Ordinal);
        Done("ui-diag-forbidden-capability-absent", forbiddenAbsent,
            "hit-assets=" + wholeBundle.Contains("assets.write:cas", StringComparison.Ordinal)
            + " hit-audio=" + wholeBundle.Contains("audio.play", StringComparison.Ordinal));

        // ③ 包里没有用户使用数据。
        var activityHit = UserActivityKeys.Where(key => wholeBundle.Contains(key, StringComparison.Ordinal)).ToList();
        Done("ui-diag-bundle-excludes-user-activity", activityHit.Count == 0,
            "hits=" + string.Join(",", activityHit));

        // ④ zip 能被读回来，清单里的 sha256 逐条对得上。
        string manifestJson = Text(entries, DiagnosticBundle.ManifestPath);
        var manifest = JsonNode.Parse(manifestJson);
        var files = manifest?["files"]?.AsArray();
        bool roundTrip = files is not null && files.Count > 0;
        var mismatches = new List<string>();
        if (files is not null)
        {
            foreach (JsonNode? node in files)
            {
                string path = node?["path"]?.GetValue<string>() ?? string.Empty;
                string expect = node?["sha256"]?.GetValue<string>() ?? string.Empty;
                if (!entries.TryGetValue(path, out string? content))
                {
                    mismatches.Add(path + "(missing)");
                    continue;
                }

                string actual = DiagnosticSections.Sha256Hex(Encoding.UTF8.GetBytes(content));
                if (!string.Equals(actual, expect, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add(path);
                }
            }
        }

        roundTrip = roundTrip && mismatches.Count == 0;
        Done("ui-diag-bundle-zip-roundtrip", roundTrip,
            "entries=" + entries.Count + " manifest-files=" + (files?.Count ?? 0) + " mismatches=" + string.Join(",", mismatches));

        // ⑤ 内存读数标着「进程」，且没有「每宠物内存」这类表述。
        string summary = Text(entries, DiagnosticBundle.SummaryPath);
        string runtime = Text(entries, DiagnosticBundle.RuntimePath);
        bool labelled = summary.Contains("进程", StringComparison.Ordinal)
            && runtime.Contains("process", StringComparison.OrdinalIgnoreCase)
            && !summary.Contains("每宠物内存", StringComparison.Ordinal)
            && !runtime.Contains("每宠物内存", StringComparison.Ordinal);
        Done("ui-diag-bundle-labels-process-level", labelled,
            "summary-process=" + summary.Contains("进程", StringComparison.Ordinal)
            + " runtime-memoryScope=" + runtime.Contains("\"memoryScope\"", StringComparison.Ordinal));

        // ⑥ 空态不编造数字。
        var bundle2 = new DiagnosticBundle(Path.Combine(root, "diagnostics-empty"), logDir, rules);
        DiagnosticExportResult emptyExport = bundle2.Export(DiagnosticReadings.Empty);
        bool emptyHonest = false;
        string emptySummary = string.Empty;
        if (emptyExport.Ok && emptyExport.BundlePath is not null)
        {
            var emptyEntries = ReadZip(emptyExport.BundlePath);
            emptySummary = Text(emptyEntries, DiagnosticBundle.SummaryPath);
            string instances = Text(emptyEntries, DiagnosticBundle.InstancesPath);
            emptyHonest = emptySummary.Contains("宠物实例 (0)", StringComparison.Ordinal)
                && emptySummary.Contains("不填任何占位数字", StringComparison.Ordinal)
                && emptySummary.Contains("无帧读数", StringComparison.Ordinal)
                && !instances.Contains("\"frames\"", StringComparison.Ordinal);
        }

        Done("ui-diag-bundle-empty-is-honest", emptyHonest,
            "exported=" + emptyExport.Ok + " summary-has-frame-number=" + emptySummary.Contains("frames=", StringComparison.Ordinal));

        // ⑦ 设置页两个入口不再是「(未接线)」，且控件确实存在。
        using var panel = new ControlPanel(() => UiSnapshot.Empty, showWindow: false);
        panel.ShowPage(PanelPages.Settings);
        IReadOnlyList<string> rows = panel.SettingsRowTexts;
        IReadOnlyList<string> controlNames = panel.DiagnosticControlNames;
        bool wired = controlNames.Count == 2
            && controlNames.Contains("diag-open-logs")
            && controlNames.Contains("diag-export-bundle")
            && rows.Any(row => row.Contains("诊断：日志目录", StringComparison.Ordinal))
            && rows.Any(row => row.Contains("诊断：导出诊断包", StringComparison.Ordinal))
            && !rows.Any(row => row.Contains("(未接线)", StringComparison.Ordinal));
        Done("ui-diag-panel-entries-wired", wired,
            "controls=" + string.Join(",", controlNames) + " rows=" + rows.Count);

        // ⑧ 导出失败说清楚原因（内联状态，不弹模态框）。
        // 让「诊断包」这个目录名被一个**文件**占住：建目录必然失败 ⇒ 导出失败。
        string storeRoot = Path.Combine(root, "store");
        Directory.CreateDirectory(storeRoot);
        File.WriteAllText(Path.Combine(storeRoot, "diagnostics"), "not a directory");
        var store = new PetDataStore(storeRoot);
        var service = new DiagnosticService(store, null, null, null);
        using var failPanel = new ControlPanel(() => UiSnapshot.Empty, showWindow: false);
        failPanel.SetDiagnosticCommand(op =>
            string.Equals(op, ControlPanel.DiagnosticOpExportBundle, StringComparison.Ordinal)
                ? service.ExportBundle()
                : service.OpenLogsDirectory());
        string message = failPanel.TryDiagnosticForCheck(ControlPanel.DiagnosticOpExportBundle);
        bool inline = failPanel.SettingsStatusShown
            && !string.IsNullOrWhiteSpace(failPanel.SettingsStatusText)
            && message.Contains("失败", StringComparison.Ordinal)
            && failPanel.SettingsStatusText.Contains("失败", StringComparison.Ordinal);
        int forms = OwnedFormCount();
        Done("ui-diag-export-failure-is-inline", inline && forms == 0,
            $"message={message} status={failPanel.SettingsStatusText} forms={forms}");
    }

    /// <summary>造一份「有内容」的读数（能力 id 覆盖白名单 + 两个禁止项）。</summary>
    private static DiagnosticReadings BuildReadings()
    {
        var allowed = new List<string>
        {
            PetDock.Core.Sandbox.CapabilityIds.InputMousePosition,
            PetDock.Core.Sandbox.CapabilityIds.InputMoveWindow,
            PetDock.Core.Sandbox.CapabilityIds.InputClickThrough,
            PetDock.Core.Sandbox.CapabilityIds.InputKeyboardHook,
            PetDock.Core.Sandbox.CapabilityIds.AssetsReadCas,
            PetDock.Core.Sandbox.CapabilityIds.PersistWriteSelf,
            PetDock.Core.Sandbox.CapabilityIds.PersistReadSelf,
            PetDock.Core.Sandbox.CapabilityIds.ScreenChangeDpi,
            "network.fetch:example.com",
            "ipc.tier2:probe",
        };
        var forbidden = new List<string> { "assets.write:cas", "audio.play" };

        var plugins = new List<DiagnosticPluginRow>
        {
            new("com.example.probe", "示例插件", "1.0.0", 1, "Active", 3, allowed, allowed, forbidden),
        };
        var instances = new List<DiagnosticInstanceRow>
        {
            new("pet-1", "user", "Running", "Tier2", 120, 2, 120, 0, "(none)", "(none)"),
        };

        return new DiagnosticReadings(
            ProgramVersion: "0.1.0",
            UptimeMs: 1234,
            ProcessMemory: "12.3 MB (进程)",
            FrameCount: 120,
            AvgFrameMs: 16.7,
            MaxFrameMs: 33.4,
            OverBudgetFrames: 2,
            FrameElapsedMs: 2000,
            TargetFrameMs: 33.3,
            FrameBudgetLine: "frame=8.3ms idle=33.3ms",
            RosterUnavailable: false,
            Plugins: plugins,
            Instances: instances,
            Audit: new List<DiagnosticAuditRow>
            {
                new("com.example.probe", "Audit", "granted input.mousePosition"),
            },
            AuditCount: 1,
            DeniedCount: 1,
            BudgetCount: 0,
            GuestFaultCount: 0);
    }

    /// <summary>读包：条目路径 → 文本内容。</summary>
    private static Dictionary<string, string> ReadZip(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using ZipArchive zip = ZipFile.OpenRead(path);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using Stream stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            map[entry.FullName] = reader.ReadToEnd();
        }

        return map;
    }

    private static string Text(Dictionary<string, string> entries, string path) =>
        entries.TryGetValue(path, out string? value) ? value : string.Empty;

    /// <summary>探针用的脱敏规则（本机事实全部用假值）。</summary>
    private static RedactRules ProbeRules() => new(
        dataRoot: "C:\\DataRoot",
        pluginsRoot: null,
        exePath: null,
        userProfile: @"C:\Users\testuser",
        userName: ProbeUser,
        machineName: ProbeMachine);

    /// <summary>当前打开的窗体数（判据「不弹模态框」的面）。</summary>
    private static int OwnedFormCount()
    {
        int count = 0;
        foreach (Form form in Application.OpenForms)
        {
            if (form is ControlPanel)
            {
                count++;
            }
        }

        return count;
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
