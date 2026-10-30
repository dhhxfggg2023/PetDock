// 插件回收站的判据（2026-10-28 第三轮）：看得见、拿得回、冲突拒绝、彻底删除、保留策略。
//
// 判据只碰临时目录：不加载插件、不启动宿主（autoLoad:false），因此验的是目录与清单，
// 不是「插件跑没跑起来」——后者在 PluginManageChecks 的启停判据里。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PetDock.Hosting;

internal static class PluginTrashChecks
{
    private const string Id = "com.example.trashme";

    public static void Run(Report check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "petdock-trash-" + Guid.NewGuid().ToString("N"));
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
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void RunWithRoot(Report check, string root)
    {
        string builtin = Path.Combine(root, "builtin");
        string user = Path.Combine(root, "user");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(builtin);
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(data);
        PluginManageChecks.WritePlugin(Path.Combine(builtin, "base"), "com.example.base", "1.0.0", "main.js");

        using var host = new PluginHost(
            builtin,
            tickIntervalMs: 50,
            autoLoad: false,
            consent: null,
            disabledPlugins: null,
            enabledPlugins: null,
            extraRoots: new[] { user },
            trustBuiltins: true);
        var store = new UserSettingsStore(data);
        var inventory = new PluginInventory(host, store, user);

        string stamp = "2026-10-28-120000";
        string firstTrash = Path.Combine(user, PluginInventory.TrashFolder, Id + "-" + stamp);
        PluginManageChecks.WritePlugin(firstTrash, Id, "1.2.3", "main.js");

        IReadOnlyList<PluginInventory.TrashEntry> listed = inventory.Trash();
        PluginInventory.TrashEntry? entry = listed.FirstOrDefault(item => item.Id == Id);
        bool seen = entry is not null && entry.Restorable
            && string.Equals(entry.Version, "1.2.3", StringComparison.Ordinal)
            && entry.Bytes > 0
            && entry.RemovedAt.Year == 2026;
        check("plugin-trash-list", seen,
            $"count={listed.Count} id={entry?.Id} version={entry?.Version} restorable={entry?.Restorable}");

        // 路径逃逸必须拒绝（恢复键来自界面，界面数据来自目录名，仍然再校验一次）。
        string? escaped = inventory.Restore("../evil", out string? escapeError);
        check("plugin-trash-path-guard", escaped is null && !string.IsNullOrEmpty(escapeError),
            $"escaped={escaped} error={escapeError}");

        // 恢复：目录回到插件根、清单还在、回收站里的那份消失。
        string? restoredId = inventory.Restore(entry!.DirName, out string? restoreError);
        string target = Path.Combine(user, Id);
        bool restored = string.Equals(restoredId, Id, StringComparison.Ordinal)
            && Directory.Exists(target)
            && File.Exists(Path.Combine(target, ManifestValidator.ManifestFileName))
            && !Directory.Exists(firstTrash);
        check("plugin-trash-restore", restored,
            $"id={restoredId} error={restoreError} target={Directory.Exists(target)}");

        // 同 id 已经装着：拒绝恢复，回收站保持原样，绝不覆盖当前那份。
        string secondTrash = Path.Combine(user, PluginInventory.TrashFolder, Id + "-2026-10-28-130000");
        PluginManageChecks.WritePlugin(secondTrash, Id, "1.3.0", "main.js");
        string? conflict = inventory.Restore(Path.GetFileName(secondTrash), out string? conflictError);
        bool kept = conflict is null && !string.IsNullOrEmpty(conflictError) && Directory.Exists(secondTrash);
        check("plugin-trash-restore-conflict", kept,
            $"restored={conflict} error={conflictError} trash-kept={Directory.Exists(secondTrash)}");

        // 彻底删除：回收站里那份消失。
        bool purged = inventory.Purge(Path.GetFileName(secondTrash), out string? purgeError);
        check("plugin-trash-purge", purged && !Directory.Exists(secondTrash),
            $"purged={purged} error={purgeError}");

        // 保留策略：21 份只留最近 20 份（按目录名尾部时间戳排序）。
        string trashRoot = Path.Combine(user, PluginInventory.TrashFolder);
        for (int i = 1; i <= 21; i++)
        {
            string old = Path.Combine(trashRoot, "com.example.old" + i.ToString("00") + "-2026-10-01-" + i.ToString("000000"));
            Directory.CreateDirectory(old);
        }

        inventory.PruneTrash();
        int left = Directory.GetDirectories(trashRoot).Length;
        check("plugin-trash-prune", left == 20, "left=" + left);
    }
}
