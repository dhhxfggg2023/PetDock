// 键盘钩子的安装门（docs/16:56「设备钩子只在插件拥有权限后激活；宿主不默认钩住」）。
// 为什么单独一个文件：这段逻辑有它自己的难点（读租约要过宿主调度线程，而调度线程会阻塞在
// 授权询问上），单独成文件好读也好改；顺带让主文件守住 D-03c 的 400 行上限。
//
// 三条纪律：
//   ① 没有持权者就不装：一个装了却没人要的全局钩子，只是白拿用户的每一次按键；
//   ② 探测读租约不能在 UI 线程上做：调度线程一停就是 10 秒，界面会跟着冻住；
//   ③ 装钩子必须在 UI 线程上做：低级钩子的回调由安装线程的消息循环派发。
using System;
using System.Threading.Tasks;
using PetDock.Core.Compositor;
using PetDock.Core.Input;
using PetDock.Core.Sandbox;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>没有持权者时的重探间隔（滴答数）：4 秒一次，不算频繁，也不算迟钝。</summary>
    private const int ReprobeTicks = 16;

    /// <summary>钩子探测的状态（探测在后台线程做，滴答器只读结果）。</summary>
    private enum HookProbe
    {
        /// <summary>还没探测过（或上一次探测因宿主停滞而作废，要重探）。</summary>
        Unknown,

        /// <summary>探测在飞。</summary>
        Running,

        /// <summary>探过了：没有插件持权。</summary>
        None,

        /// <summary>探过了：有插件持权（等 UI 线程去装钩子）。</summary>
        Found,
    }

    private KeyboardHook? _keyHook;
    private volatile HookProbe _hookProbe = HookProbe.Unknown;
    private int _ticksSinceProbe;
    private string _hookSkipReason = string.Empty;

    /// <summary>
    /// 按 docs/16:56 安装键盘钩子：只有插件真的持着 `input.keyboardHook` 的活动租约才装。
    /// 现在是**周期性**调用（原先只在构造期调一次，那早于插件装载 → 永远无持权者）。
    /// </summary>
    private void InstallKeyboardHookIfGranted()
    {
        // 用户停过钩子之后不可恢复（docs/18 §1「不可由插件恢复」）：这里直接不再看。
        if (_hooksPaused)
        {
            return;
        }

        if (_keyHook is not null || _pet is null || _host is null)
        {
            return;
        }

        if (_frames is null)
        {
            LogHookSkip("no-frame-source");
            return;
        }

        if (_hookProbe == HookProbe.Found)
        {
            _hookProbe = HookProbe.None;
            if (InstallHook())
            {
                return;
            }

            _hookProbe = HookProbe.Unknown; // 装失败就重探：持权者可能还在
            return;
        }

        if (_hookProbe == HookProbe.None)
        {
            LogHookSkip("no-grantee");
            _ticksSinceProbe++;
            if (_ticksSinceProbe >= ReprobeTicks)
            {
                // 插件可能刚刚被授予了钩子能力：过一阵子再探一次，别一次不探就永远不探。
                _ticksSinceProbe = 0;
                _hookProbe = HookProbe.Unknown;
            }

            return;
        }

        StartHookProbe();
    }

    /// <summary>
    /// 起一次后台探测：有没有插件持 input.keyboardHook 的活动租约。
    /// 为什么要放到后台：读租约要过宿主调度线程，而调度线程在装载插件时正忙（还会阻塞在
    /// 授权询问上）。在 UI 线程上读，界面会被冻住最长 10 秒（宿主调度停滞的超时）——
    /// 连弹窗里的按钮都会跟着卡住。所以读在后台，装钩子仍回 UI 线程。
    /// </summary>
    private void StartHookProbe()
    {
        if (_hookProbe == HookProbe.Running)
        {
            return; // 上一次探测还没回来
        }

        // 端口还有待答的询问 = 调度线程正阻塞在等用户回答。这时候去读它只会白等 10 秒。
        if (_consent is not null && _consent.PendingCount > 0)
        {
            return;
        }

        _hookProbe = HookProbe.Running;
        _ticksSinceProbe = 0;
        _ = Task.Run(ProbeHookGrantee);
    }

    /// <summary>后台探测的本体（不在 UI 线程上）。</summary>
    private void ProbeHookGrantee()
    {
        PluginHost? host = _host;
        bool granted = false;
        if (host is not null)
        {
            // 用宿主自己的只读名册读「谁持权」：名册在调度线程上取（内部会封送过去），
            // 调度线程停滞时它自己吞掉超时并返回空表 —— 空表就是「这次没有持权者」，
            // 过一阵子重探即可（不许把一次超时当成「永远没人要钩子」）。
            foreach (PluginRow row in host.Roster())
            {
                foreach (string id in row.GrantedIds)
                {
                    if (string.Equals(id, CapabilityIds.InputKeyboardHook, StringComparison.Ordinal))
                    {
                        granted = true;
                        break;
                    }
                }

                if (granted)
                {
                    break;
                }
            }
        }

        _hookProbe = granted ? HookProbe.Found : HookProbe.None;
    }

    /// <summary>真正装钩子（只在 UI 线程上做：钩子回调由安装线程的消息循环派发）。</summary>
    private bool InstallHook()
    {
        SwappableFrameSource source = _frames!;
        var hook = new KeyboardHook(stroke => source.PublishKeyStroke(stroke.VKey, stroke.Modifiers, stroke.Down));
        if (!hook.Install())
        {
            KernelLog.Write("key-hook-install-failed docs/14:110-pinned-item-still-present");
            hook.Dispose();
            return false;
        }

        _keyHook = hook;
        _hookSkipReason = string.Empty;
        KernelLog.Write("key-hook-installed reason=capability-granted");
        return true;
    }

    /// <summary>钩子跳过的原因只在变化时记一行（不然每个滴答都会刷一条日志）。</summary>
    private void LogHookSkip(string reason)
    {
        if (string.Equals(_hookSkipReason, reason, StringComparison.Ordinal))
        {
            return;
        }

        _hookSkipReason = reason;
        KernelLog.Write($"key-hook-skip reason={reason}");
    }
}
