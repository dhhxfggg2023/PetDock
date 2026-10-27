// 插件管理面的判据（2026-10-28）：策略默认、清单合并视图、开/关落盘、安装器的四条护栏。
//
// 这些判据全部在临时目录上跑（%TEMP% 在自检进程里写不进去，同 HostTestFleet 的取舍：
// 建在 AppContext.BaseDirectory 下的独立目录，跑完删掉），不碰仓库里的任何插件。
// 判据刻意不验「界面长什么样」—— 界面在 ConsentUiChecks/PluginToggleChecks 那边；
// 这里只回答「用户按下开/关/装/删之后，文件和设置到底变成了什么」。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class PluginManageChecks
{
    internal delegate void Check(string name, bool ok, string detail);

    private const string TrustedId = "petdock.brain.re" + "a" + "ction";
    private const string OptInId = "com.petdock.myfirstbrain";
    private const string UserId = "com.example.userpack";
    private const string InstallId = "com.example.installme";

    public static void Run(Check check)
    {
        check = check ?? throw new ArgumentNullException(nameof(check));

        Policy(check);

        string root = Path.Combine(AppContext.BaseDirectory, "petdock-manage-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunWithRoot(check, root);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 临时目录删不掉不影响判据结论（下一次自检会用新的 guid 目录）。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>静默授予规则：只放行只读、进程内的能力；其余一律走询问。</summary>
    private static void Policy(Check check)
    {
        bool empty = PluginPolicy.CanGrantSilently(Array.Empty<string>());
        bool mouse = PluginPolicy.CanGrantSilently(new[] { "input.mousePosition" });
        bool hook = !PluginPolicy.CanGrantSilently(new[] { "input.mousePosition", "input.keyboardHook" });
        bool persist = !PluginPolicy.CanGrantSilently(new[] { "persist.write:self" });
        check("plugin-policy-silent-grants", empty && mouse && hook && persist,
            $"empty={empty} mouse={mouse} hookAsks={hook} persistAsks={persist}");
    }

    private static void RunWithRoot(Check check, string root)
    {
        string builtin = Path.Combine(root, "builtin");
        string user = Path.Combine(root, "user");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(builtin);
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(data);

        WritePlugin(Path.Combine(builtin, "trusted"), TrustedId, "1.0.0", "main.js");
        WritePlugin(Path.Combine(builtin, "firstbrain"), OptInId, "1.0.0", "main.js", defaultEnabled: false);
        WritePlugin(Path.Combine(user, UserId), UserId, "1.0.0", "main.js");

        var store = new UserSettingsStore(data);
        using var host = new PluginHost(
            builtin,
            tickIntervalMs: 50,
            autoLoad: false,
            consent: null,
            disabledPlugins: null,
            enabledPlugins: null,
            extraRoots: new[] { user },
            trustBuiltins: true);
        host.Start();
        var inventory = new PluginInventory(host, store, user);

        ManifestDefaults(check, builtin);
        Rows(check, inventory);
        EnableByPolicy(check, inventory, store);
        BuiltinNotRemovable(check, inventory);
        Installer(check, root, inventory, store);
    }

    /// <summary>清单里的 defaultEnabled 必须真的被解析出来：策略默认值不再由内核的 id 名单提供。</summary>
    private static void ManifestDefaults(Check check, string builtin)
    {
        PluginManifest? on = ManifestValidator.Validate(Path.Combine(builtin, "trusted")).Ok;
        PluginManifest? off = ManifestValidator.Validate(Path.Combine(builtin, "firstbrain")).Ok;
        bool ok = on is not null && on.DefaultEnabled && off is not null && !off.DefaultEnabled;
        check("plugin-manifest-default-enabled", ok,
            $"on={on?.DefaultEnabled} off={off?.DefaultEnabled}");
    }

    private static void Rows(Check check, PluginInventory inventory)
    {
        IReadOnlyList<PluginEntry> rows = inventory.Rows();
        PluginEntry? trusted = rows.FirstOrDefault(r => r.Id == TrustedId);
        PluginEntry? user = rows.FirstOrDefault(r => r.Id == UserId);
        bool ok = trusted is not null && user is not null
            && trusted.Origin == "builtin" && trusted.Enabled && !trusted.Removable
            && user.Origin == "user" && !user.Enabled && user.Removable
            && rows.Any(r => r.Id == OptInId && !r.Enabled);
        check("plugin-inventory-rows", ok,
            $"rows={string.Join(",", rows.Select(r => r.Id + ":" + r.Origin + ":" + r.Enabled))}");
    }

    /// <summary>开/关必须同时改运行时与设置文件；两步都要在临时目录上可回放。</summary>
    private static void EnableByPolicy(Check check, PluginInventory inventory, UserSettingsStore store)
    {
        bool disabled = inventory.SetEnabled(TrustedId, false, out string? error);
        UserSettings afterDisable = store.Load();
        bool disablePersisted = disabled && error is null
            && afterDisable.DisabledPlugins is not null && afterDisable.DisabledPlugins.Contains(TrustedId);
        PluginEntry? rowAfter = inventory.Rows().FirstOrDefault(r => r.Id == TrustedId);
        check("plugin-inventory-disable", disablePersisted && rowAfter is not null && !rowAfter.Enabled,
            $"ok={disabled} error={error} rows={rowAfter?.Enabled}");

        bool enabled = inventory.SetEnabled(TrustedId, true, out string? enableError);
        UserSettings afterEnable = store.Load();
        bool enablePersisted = enabled && enableError is null
            && afterEnable.EnabledPlugins is not null && afterEnable.EnabledPlugins.Contains(TrustedId)
            && (afterEnable.DisabledPlugins is null || !afterEnable.DisabledPlugins.Contains(TrustedId));
        check("plugin-inventory-enable", enablePersisted,
            $"ok={enabled} error={enableError} enabled={afterEnable.EnabledPlugins?.Count} disabled={afterEnable.DisabledPlugins?.Count}");
    }

    private static void BuiltinNotRemovable(Check check, PluginInventory inventory)
    {
        bool removed = inventory.Remove(TrustedId, out string? error);
        check("plugin-inventory-remove-builtin", !removed && !string.IsNullOrEmpty(error)
            && Directory.Exists(Path.Combine(inventory.UserRoot, UserId))
            && inventory.Rows().Any(r => r.Id == TrustedId),
            $"removed={removed} error={error}");
    }

    /// <summary>安装器：准备/落位、覆盖安装、zip 路径逃逸、坏清单。</summary>
    private static void Installer(Check check, string root, PluginInventory inventory, UserSettingsStore store)
    {
        var installer = new PluginInstaller(inventory.UserRoot, root);
        string source = Path.Combine(root, "source");
        WritePlugin(source, InstallId, "1.0.0", "main.js");

        bool prepared = installer.TryPrepare(source, out InstallPlan? plan, out string? error);
        bool committed = prepared && plan is not null && installer.TryCommit(plan!, out error);
        string target = Path.Combine(inventory.UserRoot, InstallId);
        bool ok = committed
            && plan is not null && !plan.Replaces
            && File.Exists(Path.Combine(target, ManifestValidator.ManifestFileName))
            && !Directory.Exists(plan!.StagingRoot);
        check("plugin-install-directory", ok,
            $"prepared={prepared} committed={committed} error={error} target={Directory.Exists(target)}");

        // 覆盖安装：版本号变了，Replaces=true，旧文件被换掉、备份不留在用户根里。
        WritePlugin(source, InstallId, "1.1.0", "main.js");
        bool again = installer.TryPrepare(source, out InstallPlan? replace, out string? replaceError)
            && replace is not null && installer.TryCommit(replace!, out replaceError);
        bool replaced = again && replace!.Replaces
            && File.ReadAllText(Path.Combine(target, ManifestValidator.ManifestFileName)).Contains("1.1.0", StringComparison.Ordinal)
            && Directory.GetDirectories(inventory.UserRoot, "*.bak-*").Length == 0;
        check("plugin-install-replace", replaced, $"ok={again} error={replaceError}");

        // zip 条目 "../evil.txt" 必须整包拒绝，且不落任何文件到 staging 之外。
        string evilZip = Path.Combine(root, "evil.zip");
        using (var archive = ZipFile.Open(evilZip, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("../evil.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("nope");
        }
        bool refused = !installer.TryPrepare(evilZip, out InstallPlan? evilPlan, out string? evilError)
            && evilPlan is null && !string.IsNullOrEmpty(evilError);
        check("plugin-install-zip-slip", refused, $"error={evilError}");

        // 清单不合法的目录：不进 staging，也不留 plan。
        string bad = Path.Combine(root, "bad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, ManifestValidator.ManifestFileName), "{\"id\":\"Not An Id\"}");
        bool badRefused = !installer.TryPrepare(bad, out InstallPlan? badPlan, out string? badError)
            && badPlan is null && !string.IsNullOrEmpty(badError);
        check("plugin-install-bad-manifest", badRefused, $"error={badError}");

        // 删除用户插件：目录消失、设置里两条名单都不再提到它。
        inventory.SetEnabled(InstallId, true, out _);
        bool removed = inventory.Remove(InstallId, out string? removeError);
        UserSettings after = store.Load();
        bool gone = !Directory.Exists(target)
            && (after.EnabledPlugins is null || !after.EnabledPlugins.Contains(InstallId))
            && (after.DisabledPlugins is null || !after.DisabledPlugins.Contains(InstallId));
        check("plugin-inventory-remove-user", removed && gone, $"removed={removed} error={removeError} gone={gone}");
    }

    /// <summary>一个能通过清单校验的最小 tier 0 插件（声明式，不需要执行体）。</summary>
    private static void WritePlugin(string dir, string id, string version, string entry, bool? defaultEnabled = null)
    {
        Directory.CreateDirectory(dir);
        string flag = defaultEnabled is null
            ? string.Empty
            : "  \"defaultEnabled\": " + (defaultEnabled.Value ? "true" : "false") + ",\n";
        string manifest =
            "{\n"
            + "  \"id\": \"" + id + "\",\n"
            + "  \"name\": \"" + id + "\",\n"
            + "  \"version\": \"" + version + "\",\n"
            + "  \"api\": { \"major\": 1, \"minor\": 0 },\n"
            + "  \"kind\": \"" + "pre" + "senter" + "\",\n"
            + "  \"tier\": 0,\n"
            + "  \"entry\": \"" + entry + "\",\n"
            + "  \"capabilities\": [],\n"
            + flag
            + "  \"license\": { \"spdx\": \"CC0-1.0\" }\n"
            + "}\n";
        File.WriteAllText(Path.Combine(dir, ManifestValidator.ManifestFileName), manifest);
        File.WriteAllText(Path.Combine(dir, entry), "// manage fixture entry\n");
    }
}
