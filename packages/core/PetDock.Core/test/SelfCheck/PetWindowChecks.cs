// RFC-37 §3.2（片 1b）的判据：集合的摆位、切换与收起。
//
// 这一层回答的问题是「屏幕上有几只」——与 PetSetChecks（清单里有几只）是两个问题，
// 所以分两个文件：清单对了而窗口没摆出来，正是片 1a 之后那一刻的真实状态。
//
// 判据不建插件实例（Slot 的实例位允许为空）：这里要证明的是窗口 + 帧泵这套机制，
// 不是「名册能建几只」——那一条由 ui-catalog-* 与 --hosttest 证明。
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class PetWindowChecks
{
    /// <summary>判据行的写出口（与其它判据文件同一形态：每个文件自带一份）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    public static void Run(Check check)
    {
        PlanChecks(check);
        SetChecks(check);
        FeedbackChecks(check);
    }

    /// <summary>清单 → 「还要摆哪几只」是纯的：判决不必造窗口就能验。</summary>
    private static void PlanChecks(Check check)
    {
        var catalog = new List<PetDataStore.ActivePack>
        {
            new("user.alpha", "p-alpha"),
            new("user.beta", "p-beta"),
            new("user.gamma", "p-gamma"),
        };

        IReadOnlyList<PetDataStore.ActivePack> rest = PetWindowSet.Plan("user.beta", catalog);
        check(
            "ui-set-plan-extra",
            rest.Count == 2 && rest[0].InstanceId == "user.alpha" && rest[1].InstanceId == "user.gamma",
            $"shown=user.beta rest={Ids(rest)}");

        // 主窗显示的是默认宠物（清单一只都恢复不了）时，清单里那几只全都要摆出来。
        IReadOnlyList<PetDataStore.ActivePack> none = PetWindowSet.Plan(null, catalog);
        check("ui-set-plan-no-shown", none.Count == 3, $"rest={none.Count}");

        check(
            "ui-set-plan-empty",
            PetWindowSet.Plan("user.alpha", Array.Empty<PetDataStore.ActivePack>()).Count == 0,
            "catalog=0");
    }

    /// <summary>窗口 + 泵：两只真的各自可见、各自出帧，切换全部一次管到两只。</summary>
    private static void SetChecks(Check check)
    {
        LayeredFrameWindow? primary = null;
        LayeredFrameWindow? second = null;
        ResidentFrameLoop? primaryLoop = null;
        PetWindowSet? set = null;
        try
        {
            primary = new LayeredFrameWindow();
            second = new LayeredFrameWindow();
            primaryLoop = new ResidentFrameLoop(primary);
            set = new PetWindowSet(primary, primaryLoop);
            set.Add(new PetWindowSet.Slot(null, "(probe)", second, null, new ResidentFrameLoop(second)));

            set.StartAll();
            ResidentFrameLoop secondLoop = set.Slots[0].Loop;
            bool bothVisible = WaitFor(4000, () => primary.Visible && second.Visible);
            bool bothPumping = WaitFor(4000, () => primaryLoop.Pumping && secondLoop.Pumping);
            long ticks0 = secondLoop.Ticks;
            bool ticking = WaitFor(2000, () => secondLoop.Ticks > ticks0);

            // 两只窗口的横坐标必须拉开：叠在一起时用户只看见一只，而判据会「两只都可见」全绿。
            int gap = Math.Abs(second.CurrentPosition.X - primary.CurrentPosition.X);
            check(
                "ui-set-two-windows",
                bothVisible && bothPumping && ticking && gap >= 120,
                $"visible={Flag(primary.Visible)}/{Flag(second.Visible)}"
                + $" pumping={Flag(primaryLoop.Pumping)}/{Flag(secondLoop.Pumping)}"
                + $" ticks={secondLoop.Ticks} gap={gap}");

            set.ToggleAll();
            bool hidden = WaitFor(4000, () => !primary.Visible && !second.Visible && !primaryLoop.Pumping && !secondLoop.Pumping);
            set.ToggleAll();
            bool shown = WaitFor(4000, () => primary.Visible && second.Visible && secondLoop.Pumping);
            check("ui-set-toggle-all", hidden && shown, $"hidden={Flag(hidden)} shownAgain={Flag(shown)}");

            // 托盘的「隐藏宠物」= 主窗 Hide + 集合 HideAll（UiHost.HidePet 的两步）：
            // 只做前一半时第二只还在屏幕上出帧 —— 这条判据就是那半步的守门人。
            primaryLoop.Hide();
            set.HideAll();
            bool allGone = WaitFor(4000, () => !primary.Visible && !second.Visible && !secondLoop.Pumping);
            check("ui-set-hide-all-stops-pump", allGone, $"secondVisible={Flag(second.Visible)} pumping={Flag(secondLoop.Pumping)}");
        }
        catch (Exception error)
        {
            check("ui-set-two-windows", false, $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            set?.Dispose();
            primaryLoop?.Dispose();
            primary?.Dispose();
            second?.Dispose();
        }
    }

    /// <summary>点击/拖动/落地三条反馈都接到同一个窗口，且气泡真的能开能收。</summary>
    private static void FeedbackChecks(Check check)
    {
        LayeredFrameWindow? window = null;
        try
        {
            window = new LayeredFrameWindow();
            int taps = 0;
            int drags = 0;
            int drops = 0;
            window.Tapped += () => taps++;
            window.Dragged += () => drags++;
            window.Dropped += () => drops++;
            window.RaiseTappedForCheck();
            window.RaiseDraggedForCheck();
            window.RaiseDroppedForCheck();

            window.Show();
            Application.DoEvents();
            window.ShowSpeech("测试");
            bool bubble = window.SpeechVisible;
            window.HideSpeechForCheck();
            check("ui-pet-feedback",
                taps == 1 && drags == 1 && drops == 1 && bubble,
                $"taps={taps} drags={drags} drops={drops} bubble={bubble}");

            // 抛掷：给一个初速度后必须真的进入物理，并在超时前落地/收住。
            window.StartThrowForCheck(700.0, -500.0);
            bool flying = window.PhysicsActive;
            bool landed = WaitFor(5000, () => !window.PhysicsActive);
            check("ui-pet-throw", flying && landed,
                $"flying={flying} landed={landed}");
        }
        catch (Exception error)
        {
            check("ui-pet-feedback", false, error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            window?.Dispose();
        }
    }

    private static string Ids(IReadOnlyList<PetDataStore.ActivePack> packs)
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < packs.Count; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append(packs[i].InstanceId);
        }

        return text.ToString();
    }

    private static string Flag(bool value) => value ? "true" : "false";

    /// <summary>等一段时间（帧泵在后台线程上跑，但要靠消息泵把窗口事件派发出去）。</summary>
    private static bool WaitFor(int timeoutMs, Func<bool> predicate)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate())
            {
                return true;
            }

            Application.DoEvents();
            System.Threading.Thread.Sleep(20);
        }

        return predicate();
    }
}
