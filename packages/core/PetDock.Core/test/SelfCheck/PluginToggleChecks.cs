// 「插件可以关掉、重启生效」与「右键宠物弹菜单」的判据（2026-10-26 用户要求）。
//
// 为什么这两件事共用一份判据：它们都是「用户的一次点击」到「宿主的一次动作」的接线，
// 而接线的两个断点形态一样 —— 点了没反应（菜单没接上 / 状态没落盘）。
//
// 覆盖的判据：
//   ui-plugins-disabled-round-trip   关掉的插件 id 能落盘再读回来（下一轮装载读的就是它）
//   ui-plugins-disabled-list-kept    同一只插件被关两次不会在表里出现两次；空表不写这个键
//   ui-plugins-disabled-known        宿主认这个 id，且插件根上确实有可发现的插件
//   ui-plugins-hint-present          插件页那行「重启生效」的说明确实挂上了（不是键名）
//   ui-pet-context-menu-wired        宠物窗的右键接着宿主（没接 = 右键是个死键）
//   ui-pet-context-menu-dispatch     右键事件真的把屏幕坐标递到处理函数
//   ui-pet-context-menu-set-wired    集合里那几只也接上了（主窗接上不算数）
//   ui-pet-double-click-wired        双击接着宿主（2026-10-27：托盘之外的入口）
//   ui-pet-double-click-dispatch     双击事件真的把处理函数叫起来
//   ui-pet-double-click-set-wired    集合里那几只也接上了双击
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;
using PetDock.Hosting;

namespace PetDock.Hosting;

internal static class PluginToggleChecks
{
    /// <summary>判据的输出与计数回调（与 UiSelfTest 的 Check 同形）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    /// <summary>判据用的插件 id（不依赖仓库里真有这只 —— 那是一支会漂的靶子）。</summary>
    private const string ProbePluginId = "selfcheck.plugin.toggle";

    private static readonly string[] JudgeNames =
    {
        "ui-plugins-disabled-round-trip",
        "ui-plugins-disabled-list-kept",
        "ui-plugins-disabled-known",
        "ui-plugins-hint-present",
        "ui-pet-context-menu-wired",
        "ui-pet-context-menu-dispatch",
        "ui-pet-context-menu-set-wired",
        "ui-pet-double-click-wired",
        "ui-pet-double-click-dispatch",
        "ui-pet-double-click-set-wired",
    };

    public static void Run(Check check)
    {
        string root = Path.Combine(Path.GetTempPath(), "petdock-plugin-toggle-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunAll(check, root);
        }
        catch (Exception error)
        {
            // 兜底：RunAll 里每条判据各自带守卫，所以走到这里只有「建临时目录」这类共用步骤失败。
            foreach (string name in JudgeNames)
            {
                check(name, false, $"harness: {error.GetType().Name}: {error.Message}");
            }
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
            catch (Exception)
            {
                // 临时目录删不掉不影响判据结论。
            }
        }
    }

    private static void RunAll(Check check, string root)
    {
        var store = new UserSettingsStore(root);

        // ① 往返：关掉的 id 落盘、再读回来是同一个（下一轮装载读的就是这一份）。
        Judge(check, "ui-plugins-disabled-round-trip", () =>
        {
            store.Save(UserSettings.Default with { DisabledPlugins = new List<string> { ProbePluginId } });
            UserSettings loaded = new UserSettingsStore(root).Load();
            bool ok = loaded.DisabledPlugins is { Count: 1 } ids
                && string.Equals(ids[0], ProbePluginId, StringComparison.Ordinal)
                && loaded.Describe().Contains("plugins-disabled=1", StringComparison.Ordinal)
                && File.ReadAllText(Path.Combine(root, UserSettingsStore.SettingsFile))
                    .Contains("pluginsDisabled", StringComparison.Ordinal);
            return (ok, loaded.Describe());
        });

        // ② 表的纪律：同一只关两次只留一条（重复条目会让计数说谎），空表则不写这个键。
        Judge(check, "ui-plugins-disabled-list-kept", () =>
        {
            store.Save(UserSettings.Default with { DisabledPlugins = new List<string> { ProbePluginId, ProbePluginId } });
            UserSettings deduped = store.Load();
            store.Save(UserSettings.Default with { DisabledPlugins = new List<string>() });
            bool emptyKeyGone = !File.ReadAllText(Path.Combine(root, UserSettingsStore.SettingsFile))
                .Contains("pluginsDisabled", StringComparison.Ordinal);
            bool ok = deduped.DisabledPlugins is { Count: 1 } && emptyKeyGone;
            return (ok, $"count={(deduped.DisabledPlugins?.Count ?? 0)} emptyKeyGone={emptyKeyGone}");
        });

        // ③ 宿主认这个 id；同时证明插件根是对的（扫错根时「跳过」会假绿 —— 见 2026-10-17 那条账）。
        Judge(check, "ui-plugins-disabled-known", () =>
        {
            string? pluginsRoot = PluginHost.ProbePluginsRoot();
            using var skipping = new PluginHost(pluginsRoot ?? string.Empty, 60, autoLoad: false, disabledPlugins: new[] { ProbePluginId });
            using var normal = new PluginHost(pluginsRoot ?? string.Empty, 60, autoLoad: false);
            int discovered = pluginsRoot is null ? 0 : skipping.Discover(pluginsRoot).Count;
            bool ok = discovered >= 1
                && skipping.IsDisabledByUser(ProbePluginId)
                && !normal.IsDisabledByUser(ProbePluginId);
            return (ok, $"root={pluginsRoot ?? "(null)"} discovered={discovered} skipped={skipping.IsDisabledByUser(ProbePluginId)}");
        });

        // ④ 插件页那行说明：断的是**文字**，不是键名（键名两侧相等会绿得毫无内容）。
        Judge(check, "ui-plugins-hint-present", () =>
        {
            using var panel = new ControlPanel(() => UiSnapshot.Empty, showWindow: false);
            string hint = panel.PluginHintText;
            bool ok = hint.Length > 0 && !string.Equals(hint, "cp.plugins.disableHint", StringComparison.Ordinal);
            return (ok, $"hint={hint}");
        });

        // ⑤⑥ 右键：窗口真的接着宿主，且事件把屏幕坐标递到处理函数。
        Judge(check, "ui-pet-context-menu-wired", () =>
        {
            using var window = new LayeredFrameWindow();
            using var host = new UiHost(window, null, null, null, null, null);
            bool wired = window.ContextMenuWired;
            return (wired, $"wired={wired}");
        });

        Judge(check, "ui-pet-context-menu-dispatch", () =>
        {
            using var window = new LayeredFrameWindow();
            var seen = new List<System.Drawing.Point>();
            window.ContextRequested += point => seen.Add(point);
            window.RaiseContextRequestedForCheck(new System.Drawing.Point(640, 360));
            bool ok = seen.Count == 1 && seen[0].X == 640 && seen[0].Y == 360;
            return (ok, $"seen={seen.Count} first={(seen.Count > 0 ? $"{seen[0].X},{seen[0].Y}" : "(none)")}");
        });

        // ⑦ 集合里那几只：交上处理函数之后再登记的槽位必须自动接上（主窗接上不算数）。
        //    槽位的实例位允许为空（与 ui-set-* 同一条纪律：这里证的是窗口这层机制）。
        //    第二只窗的所有权随 Slot 走，交给 set.Dispose() —— 判据自己不许再释放它。
        Judge(check, "ui-pet-context-menu-set-wired", () =>
        {
            using var window = new LayeredFrameWindow();
            var second = new LayeredFrameWindow();
            using var set = new PetWindowSet(window);
            set.ContextMenuHandler = _ => { };
            set.Add(new PetWindowSet.Slot(null, "(probe)", second, null, new ResidentFrameLoop(second)));
            return (set.ContextMenuWiredCount == 1, $"wired={set.ContextMenuWiredCount} slots={set.Slots.Count}");
        });

        // ⑧⑨⑩ 双击（2026-10-27）：托盘之外的第二条入口。托盘图标在发行布局里不进外壳
        //    （原规格 98 §3.1 第 1 条），所以「双击宠物开面板」必须有判据钉着，不然它会静默变成死键。
        Judge(check, "ui-pet-double-click-wired", () =>
        {
            using var window = new LayeredFrameWindow();
            using var host = new UiHost(window, null, null, null, null, null);
            bool wired = window.DoubleClickWired;
            return (wired, $"wired={wired}");
        });

        Judge(check, "ui-pet-double-click-dispatch", () =>
        {
            using var window = new LayeredFrameWindow();
            int calls = 0;
            window.DoubleClicked += () => calls++;
            window.RaiseDoubleClickedForCheck();
            return (calls == 1, $"calls={calls}");
        });

        Judge(check, "ui-pet-double-click-set-wired", () =>
        {
            using var window = new LayeredFrameWindow();
            var second = new LayeredFrameWindow();
            using var set = new PetWindowSet(window);
            set.DoubleClickHandler = () => { };
            set.Add(new PetWindowSet.Slot(null, "(probe)", second, null, new ResidentFrameLoop(second)));
            return (set.DoubleClickWiredCount == 1, $"wired={set.DoubleClickWiredCount} slots={set.Slots.Count}");
        });
    }

    /// <summary>
    /// 每条判据各自带守卫：一条炸了不许把后面几条一起带走（第一版就是被这个坑坑了 ——
    /// ① 抛异常之后 ⑦ 条全报「exception」，看不出到底哪一层坏了）。
    /// detail 里带上第一帧栈，定位到具体那一行而不是「某个 JSON 选项」这种半截线索。
    /// </summary>
    private static void Judge(Check check, string name, Func<(bool Ok, string Detail)> body)
    {
        try
        {
            (bool ok, string detail) = body();
            check(name, ok, detail);
        }
        catch (Exception error)
        {
            check(name, false, $"{error.GetType().Name}: {error.Message} @ {FirstFrame(error)}");
        }
    }

    private static string FirstFrame(Exception error)
    {
        string? stack = error.StackTrace;
        if (string.IsNullOrEmpty(stack))
        {
            return "(no-stack)";
        }

        int breakAt = stack.IndexOf('\n');
        return (breakAt > 0 ? stack[..breakAt] : stack).Trim();
    }
}
