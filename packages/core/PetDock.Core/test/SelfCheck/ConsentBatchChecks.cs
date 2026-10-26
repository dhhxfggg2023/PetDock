// 授权弹窗合批的判据（真机 2026-10-26 11:02：三只插件同时要授权 → 连弹三个窗，
// 用户要的是「一个窗、按插件分段」）。
// 契约：同批请求只开一个窗，按插件分段；一次回答对每份请求分别落账
//（范围三选一统一，勾选集各自一份）。单请求弹窗的行为保持原样。
using System;
using System.Collections.Generic;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class ConsentBatchChecks
{
    /// <summary>跑合批判据，返回失败条数。</summary>
    public static int Run(ConsentCheck check)
    {
        var alpha = new ConsentRequest(
            "com.example.alpha",
            "甲插件",
            1,
            new List<ConsentItem>
            {
                new("input.mousePosition", PreGranted: false, Denied: false),
                new("input.keyboardHook", PreGranted: false, Denied: true),
            },
            IsReview: false);
        var beta = new ConsentRequest(
            "com.example.beta",
            "乙插件",
            2,
            new List<ConsentItem> { new("ipc.tier2:shim", PreGranted: false, Denied: false) },
            IsReview: false);

        using var dialog = new AuthorizationDialog(new[] { alpha, beta });

        // 分段：两份请求 → 两个分段头，行按请求顺序摊平；单请求弹窗不分段。
        bool grouped = dialog.RequestCount == 2
            && dialog.GroupsVisible
            && dialog.GroupHeaders.Count == 2
            && dialog.ItemIds.Count == 3
            && dialog.ItemIdsFor(0).Count == 2
            && dialog.ItemIdsFor(1).Count == 1;
        check("ui-auth-batch-grouped", grouped,
            $"requests={dialog.RequestCount} groups={string.Join("|", dialog.GroupHeaders)} items={string.Join(",", dialog.ItemIds)}");

        // 标题行要说明这是「同时请求」，而不是冒充某一只插件。
        check("ui-auth-batch-header", dialog.HeaderText.Contains('2'),
            $"header={dialog.HeaderText}");

        // 每份请求各自的勾选集：默认全勾，被拒绝的项不勾也不进授予集。
        bool perRequest = dialog.SelectedIdsFor(0).Count == 1
            && dialog.SelectedIdsFor(0).Contains("input.mousePosition")
            && !dialog.SelectedIdsFor(0).Contains("input.keyboardHook")
            && dialog.SelectedIdsFor(1).Count == 1
            && dialog.SelectedIdsFor(1).Contains("ipc.tier2:shim");
        check("ui-auth-batch-per-request-selection", perRequest,
            $"a={string.Join(",", dialog.SelectedIdsFor(0))} b={string.Join(",", dialog.SelectedIdsFor(1))}");

        // 批里只要有一份 Tier 2，「仅本次」就不可选（docs/18 §3 的规则按整批统一）。
        check("ui-auth-batch-once-disabled", !dialog.OnceEnabled, $"once={dialog.OnceEnabled}");

        // 取消甲的一项不牵连乙 —— 否则「一份回答」会变成「一锅端」。
        bool uncheck = dialog.SetItemCheckedAt(0, false);
        bool isolated = dialog.SelectedIdsFor(0).Count == 0 && dialog.SelectedIdsFor(1).Count == 1;
        check("ui-auth-batch-selection-isolated", uncheck && isolated,
            $"a={string.Join(",", dialog.SelectedIdsFor(0))} b={string.Join(",", dialog.SelectedIdsFor(1))}");

        // 端口契约：同一次回答按请求分别落账。
        ConsentAnswer allowA = UiConsentPort.MapAnswer(GrantAnswer.Allow, dialog.SelectedIdsFor(0));
        ConsentAnswer allowB = UiConsentPort.MapAnswer(GrantAnswer.Allow, dialog.SelectedIdsFor(1));
        ConsentAnswer denyB = UiConsentPort.MapAnswer(GrantAnswer.Deny, dialog.SelectedIdsFor(1));
        check("ui-auth-batch-answer-per-request",
            allowA.Scope == ConsentScope.Session && allowA.Granted.Count == 0
            && allowB.Scope == ConsentScope.Session && allowB.Granted.Count == 1
            && denyB.Granted.Count == 0,
            $"a=({allowA.Scope},{allowA.Granted.Count}) b=({allowB.Scope},{allowB.Granted.Count}) denyB={denyB.Granted.Count}");

        // 单请求弹窗保持原样：不分段、标题仍是旧文案、行为不退化。
        using var single = new AuthorizationDialog("示例宠物", "sample-pet", 1, new[] { "input.mousePosition" });
        check("ui-auth-batch-single-unchanged",
            single.RequestCount == 1 && !single.GroupsVisible
            && single.SelectedIds.Count == 1 && single.OnceEnabled,
            $"requests={single.RequestCount} groups={single.GroupsVisible} header={single.HeaderText}");

        // 晚到的请求并进**已经打开**的窗（真机 2026-10-26 16:56：三只插件隔 0.4–0.6 秒先后申请，
        // 开窗那一刻队里只有一份 → 原来会连弹三个窗）。并进来之后：变成批、补分段头、
        // 老行归属与勾选状态都不动，新行挂到新段；Tier 2 晚到则「仅本次」要跟着置灰。
        using var late = new AuthorizationDialog(new[] { alpha });
        bool uncheckFirst = late.SetItemChecked("input.mousePosition", false);
        late.AddRequest(beta);
        bool grown = late.RequestCount == 2
            && late.GroupsVisible
            && late.GroupHeaders.Count == 2
            && late.ItemIds.Count == 3
            && late.ItemIdsFor(1).Count == 1
            && !late.OnceEnabled
            && late.HeaderText.Contains('2');
        check("ui-auth-batch-late-arrival", uncheckFirst && grown,
            $"requests={late.RequestCount} groups={late.GroupsVisible}/{late.GroupHeaders.Count} "
            + $"items={string.Join(",", late.ItemIds)} once={late.OnceEnabled} header={late.HeaderText}");

        // 老段里用户已经取消的勾，不因为新段进来而被复位（否则「一次回答」会悄悄改掉用户的决定）。
        bool kept = late.SelectedIdsFor(0).Count == 0
            && late.SelectedIdsFor(1).Count == 1
            && late.SelectedIdsFor(1).Contains("ipc.tier2:shim");
        check("ui-auth-batch-late-arrival-keeps-selection", kept,
            $"a={string.Join(",", late.SelectedIdsFor(0))} b={string.Join(",", late.SelectedIdsFor(1))}");
        return 0;
    }
}
