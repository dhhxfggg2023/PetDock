// 授权链路的 UI 侧自检（RFC-13 §3 判据表）。
// 为什么单独一个文件：UiSelfTest.cs 已经接近 D-03c 的单文件行数上限，而 D-03c 对 test/ 也生效。
// 判据口径来自文档：
//   原规格 18 §3   弹窗可逐项拒绝；被拒绝的能力在下次打开时置灰且不可勾
//   原规格 18 §2.2 插件页有「授权」与「禁用」两个入口，且作用于**选中**的插件
// 输出与计数都交给调用方（UiSelfTest 的 emit/check），这样 --log 文件照样能取证。
using System;
using System.Collections.Generic;
using System.Linq;
using PetDock.Core.Ui;
using PetDock.Hosting;

namespace PetDock.Hosting;

/// <summary>输出回调（D-03b：系统自带的同名泛型委托含黑名单子串，自声明代替，同 UiCommand）。</summary>
internal delegate void ConsentEmit(string line);

/// <summary>判据回调（key / 是否通过 / 详情）。</summary>
internal delegate void ConsentCheck(string name, bool ok, string detail);

internal static class ConsentUiChecks
{
    /// <summary>跑 UI 侧全部授权判据，返回失败条数。</summary>
    public static int Run(ConsentEmit emit, ConsentCheck check)
    {
        int failed = 0;
        failed += DialogItemwise(emit, check);
        failed += DialogLockedAfterDeny(emit, check);
        failed += PluginPageEntries(emit, check);
        return failed;
    }

    /// <summary>
    /// 原规格 18 §3：「你可以拒绝其中的任何一项」——弹窗必须逐项可选，而不是三个互斥单选。
    /// 判据：两项能力默认全勾；用户取消勾选其中一项后，SelectedIds 只剩另一项。
    /// </summary>
    private static int DialogItemwise(ConsentEmit emit, ConsentCheck check)
    {
        var items = new List<ConsentItem>
        {
            new("input.mousePosition", PreGranted: false, Denied: false),
            new("network.fetch:api.example.com", PreGranted: false, Denied: false),
        };

        using var dialog = new AuthorizationDialog("示例宠物", "sample-pet", 1, items, isReview: false);

        bool defaultsOn = dialog.SelectedIds.Count == 2
            && dialog.SelectedIds.Contains("input.mousePosition")
            && dialog.SelectedIds.Contains("network.fetch:api.example.com");
        check("ui-auth-dialog-itemwise", defaultsOn,
            $"selected={string.Join(",", dialog.SelectedIds)} items={string.Join(",", dialog.ItemIds)}");

        // 取消勾选其中一项：必须真的少一项（而不是仍然全选 = 逐项选择是假的）
        bool canUncheck = dialog.SetItemChecked("network.fetch:api.example.com", false);
        bool narrowed = dialog.SelectedIds.Count == 1
            && dialog.SelectedIds.Contains("input.mousePosition")
            && !dialog.SelectedIds.Contains("network.fetch:api.example.com");
        check("ui-auth-dialog-uncheck-one", canUncheck && narrowed,
            $"canUncheck={canUncheck} selected={string.Join(",", dialog.SelectedIds)}");

        emit($"CHECK-DISPLAY ui-auth-dialog-tiers answers={dialog.Answers} once={dialog.OnceEnabled}");
        return 0;
    }

    /// <summary>
    /// 原规格 18 §3：「被拒绝的能力在下次打开弹窗时置灰」+「拒绝后该插件不允许重试同一能力」。
    /// 判据：Denied 项出现在展示清单里，但不在可勾集合里，勾选集合里也没有它。
    /// </summary>
    private static int DialogLockedAfterDeny(ConsentEmit emit, ConsentCheck check)
    {
        var items = new List<ConsentItem>
        {
            new("input.mousePosition", PreGranted: false, Denied: false),
            new("input.keyboardHook", PreGranted: false, Denied: true),
        };

        using var dialog = new AuthorizationDialog("示例宠物", "sample-pet", 1, items, isReview: false);

        bool locked = dialog.LockedIds.Contains("input.keyboardHook")
            && dialog.IsItemLocked("input.keyboardHook")
            && !dialog.SelectedIds.Contains("input.keyboardHook")
            && dialog.ItemIds.Contains("input.keyboardHook");
        check("ui-auth-deny-locked", locked,
            $"locked={string.Join(",", dialog.LockedIds)} selected={string.Join(",", dialog.SelectedIds)}");

        // 强行勾选被拒项必须无效：SetItemChecked 对锁定项返回 false，且它不得进入可勾集合
        bool blocked = !dialog.SetItemChecked("input.keyboardHook", true);
        bool stillOut = !dialog.SelectedIds.Contains("input.keyboardHook");
        check("ui-auth-deny-not-regrantable", blocked && stillOut,
            $"blocked={blocked} selected={string.Join(",", dialog.SelectedIds)}");
        return 0;
    }

    /// <summary>
    /// 原规格 18 §2.2：插件页要有「授权」与「禁用」两个入口，且作用于**选中**的插件。
    /// 判据：按钮存在；选中一行后回调收到的 id 等于该行的 pluginId。
    /// </summary>
    private static int PluginPageEntries(ConsentEmit emit, ConsentCheck check)
    {
        string? authorizedId = null;
        string? disabledId = null;

        var snapshot = new UiSnapshot(
            Array.Empty<PetLine>(),
            new List<PluginLine>
            {
                new("com.example.alphaplugin", "示例插件", "1.0.0", 1, "Running", 2),
                new("com.example.betaplugin", "另一个插件", "1.0.0", 0, "Suspended", 0),
            });

        using var panel = new ControlPanel(
            () => snapshot,
            showWindow: false,
            authorize: id => authorizedId = id,
            toggleDisabled: id => disabledId = id);

        bool hasButtons = panel.PluginButtonNames.Contains("authorize")
            && panel.PluginButtonNames.Contains("disable");
        check("ui-plugin-page-entries", hasButtons,
            $"buttons={string.Join(",", panel.PluginButtonNames)}");

        bool selected = panel.SelectPlugin("com.example.betaplugin");
        check("ui-plugin-page-select", selected && panel.SelectedPluginId == "com.example.betaplugin",
            $"selected={panel.SelectedPluginId} ok={selected}");

        panel.AuthorizeSelected();
        panel.ToggleSelectedDisabled();
        check("ui-plugin-page-callback-ids",
            authorizedId == "com.example.betaplugin" && disabledId == "com.example.betaplugin",
            $"authorize={authorizedId} disable={disabledId}");

        // 没有选中行时不得发出任何意图（否则会误伤第一个插件）
        string? strayAuth = null;
        string? strayDisable = null;
        using var emptyPanel = new ControlPanel(
            () => new UiSnapshot(Array.Empty<PetLine>(), Array.Empty<PluginLine>()),
            showWindow: false,
            authorize: id => strayAuth = id,
            toggleDisabled: id => strayDisable = id);
        emptyPanel.AuthorizeSelected();
        emptyPanel.ToggleSelectedDisabled();
        check("ui-plugin-page-no-stray-intent",
            strayAuth is null && strayDisable is null && emptyPanel.SelectedPluginId is null,
            $"auth={strayAuth} disable={strayDisable}");
        return 0;
    }
}
