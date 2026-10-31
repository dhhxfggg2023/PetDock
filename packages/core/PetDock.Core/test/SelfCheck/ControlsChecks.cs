// 控件库的判据（R2c/R2d）：主题跟随、订阅不增长、按钮用途可区分、进度两态、焦点可到达。
//
// 这一组盯的是**旧层的两个老毛病**：
//   ① 订阅泄漏 —— 页面开合一次多一份事件处理器（research/ui-lessons.md 的订阅类教训）；
//   ② 状态画错 —— 不确定/确定的进度被画成同一种，用户以为卡住了。
// 两条都用「建了再丢、看表长不长」「切一次看值变不变」来判，不靠人眼。
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PetDock.Ui.Controls;
using PetDock.Ui.Design;
using MediaColor = System.Windows.Media.Color;

namespace PetDock.Hosting;

internal static class ControlsChecks
{
    public static void Run(Report check)
    {
        ThemeFollows(check);
        SubscriptionsBounded(check);
        ButtonKinds(check);
        ProgressStates(check);
        BannerAndEmptyState(check);
        Focusable(check);
    }

    /// <summary>切到浅色之后，控件的底色必须真的换成浅色那套（不是「看起来还行」）。</summary>
    private static void ThemeFollows(Report check)
    {
        var card = new Card();
        bool dark = Same(card.Background, Palette.Dark.Surface);

        ThemeMode before = Theme.Mode;
        bool light;
        try
        {
            Theme.SetMode(ThemeMode.Light);
            light = Same(card.Background, Palette.Light.Surface);
        }
        finally
        {
            Theme.SetMode(before);
        }

        bool restored = Same(card.Background, Palette.Dark.Surface);
        check("ui-controls-theme-follows", dark && light && restored,
            $"dark={dark} light={light} restored={restored}");
    }

    /// <summary>建 200 个控件再丢掉：主题订阅表必须跟着降回去（弱引用 + 及时剪枝）。</summary>
    private static void SubscriptionsBounded(Report check)
    {
        int before = ThemeHub.Count;
        for (int i = 0; i < 200; i++)
        {
            _ = new UiButton("按钮" + i, UiButtonKind.Secondary, IconName.Check);
        }

        int grown = ThemeHub.Count;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        ThemeHub.Repaint();
        int after = ThemeHub.Count;

        check("ui-controls-subscriptions-bounded",
            grown > before && after <= before + 5,
            $"before={before} grown={grown} after-gc={after}");
    }

    /// <summary>四种用途四种底色；Danger 用的必须是状态色（用户要一眼看出这是危险操作）。</summary>
    private static void ButtonKinds(Report check)
    {
        Palette palette = Theme.Current;
        var primary = new UiButton("主要", UiButtonKind.Primary);
        var secondary = new UiButton("次要", UiButtonKind.Secondary);
        var quiet = new UiButton("安静", UiButtonKind.Quiet);
        var danger = new UiButton("危险", UiButtonKind.Danger);

        MediaColor[] colors =
        {
            ((SolidColorBrush)primary.Background).Color,
            ((SolidColorBrush)secondary.Background).Color,
            ((SolidColorBrush)quiet.Background).Color,
            ((SolidColorBrush)danger.Background).Color,
        };

        var distinct = new HashSet<MediaColor>(colors);
        bool dangerIsTone = colors[3] == palette.Danger;
        check("ui-controls-button-kinds",
            distinct.Count == 4 && dangerIsTone,
            $"distinct={distinct.Count} danger={colors[3] == palette.Danger}");
    }

    /// <summary>进度两态必须分开：Report(-1) 回不确定态，Report(0.42) 是确定态，End 收起来。</summary>
    private static void ProgressStates(Report check)
    {
        var view = new ProgressView("取消");
        view.Begin("正在导入…");
        bool began = view.IsBusy && !view.IsDeterminate && view.Stage == "正在导入…";

        view.Report(0.42, "正在复制…");
        bool determinate = view.IsDeterminate && Math.Abs(view.Ratio - 0.42) < 0.001 && view.Stage == "正在复制…";

        view.Report(-1);
        bool backToUnknown = !view.IsDeterminate;

        view.End();
        bool ended = !view.IsBusy && view.Stage.Length == 0;

        check("ui-controls-progress-states",
            began && determinate && backToUnknown && ended,
            $"began={began} determinate={determinate} unknown={backToUnknown} ended={ended}");
    }

    /// <summary>提示条原样带着服务给的那句话；空状态说清「为什么空」。</summary>
    private static void BannerAndEmptyState(Report check)
    {
        const string reason = "市场索引拉不下来：网络不通。";
        var banner = new Banner(BannerKind.Error, reason);
        var empty = new EmptyState("还没有插件", "把插件放进 plugins/ 后重新打开。", IconName.Plug);

        bool bannerOk = banner.Kind == BannerKind.Error && banner.Text == reason;
        bool emptyOk = empty.Title == "还没有插件";
        check("ui-controls-banner-empty", bannerOk && emptyOk, $"banner={bannerOk} empty={emptyOk}");
    }

    /// <summary>键盘真的能到按钮上，而且到时看得见焦点框。</summary>
    private static void Focusable(Report check)
    {
        var button = new UiButton("打开", UiButtonKind.Primary, IconName.Folder);
        bool ok = button.Focusable
            && button.IsTabStop
            && button.IsEnabled
            && button.BorderThickness.Left == Tokens.Line.Hairline;
        check("ui-controls-focusable", ok,
            $"focusable={button.Focusable} tabstop={button.IsTabStop} ring={button.BorderThickness.Left}");
    }

    private static bool Same(System.Windows.Media.Brush? brush, MediaColor expected) =>
        brush is SolidColorBrush solid && solid.Color == expected;

}