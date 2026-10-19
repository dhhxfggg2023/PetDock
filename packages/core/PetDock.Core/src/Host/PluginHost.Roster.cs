// 宿主侧只读花名册（RFC-12 §2.3）：UI 唯一的宿主读入口。
// 为什么必须是新的一小片而不是直接暴露 _handles：句柄持有沙箱与能力闸门，
// 把它交给 UI 等于让展示层获得反向操作宿主的能力。这里只返回**值**，不返回句柄。
using System;
using System.Collections.Generic;
using System.Globalization;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>插件页一行的宿主侧形态：纯值，不含句柄。只读。</summary>
public sealed record PluginRow(
    string Id,
    string Name,
    string Version,
    int Tier,
    PluginLifecycleState State,
    int Granted)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name} v{Version} tier={Tier} state={State} caps={Granted}");
}

public sealed partial class PluginHost
{
    /// <summary>
    /// 一致性快照：在调度线程上取值（复用 Snapshot&lt;T&gt;），避免对可变状态的撕裂读。
    /// 停机后（调度线程已 Join）直接在当前线程读——此时没有并发写者。
    /// </summary>
    public IReadOnlyList<PluginRow> Roster()
    {
        IReadOnlyList<PluginRow> Read()
        {
            var rows = new List<PluginRow>(_handles.Count);
            foreach (PluginHandle handle in _handles)
            {
                rows.Add(new PluginRow(
                    handle.Manifest.Id,
                    handle.Manifest.Name,
                    handle.Manifest.Version,
                    handle.Manifest.Tier,
                    handle.State,
                    handle.ActiveLeaseCount));
            }

            return rows;
        }

        if (!_thread.IsAlive || _stopping)
        {
            return Read();
        }

        try
        {
            return Snapshot(Read);
        }
        catch (TimeoutException)
        {
            // 调度线程卡住时 UI 不能跟着卡：返回空名册，面板显示「(宿主无响应)」由调用方决定。
            KernelLog.Write("roster-timeout scheduler-stalled");
            return Array.Empty<PluginRow>();
        }
    }

    /// <summary>宠物实例状态的中性表述（UI 显示用）。InstanceState 的枚举名对用户不友好。</summary>
    public static string DescribeInstanceState(InstanceState state) => state switch
    {
        InstanceState.Discovered => "发现",
        InstanceState.Imported => "已导入",
        InstanceState.Instantiated => "已装配",
        InstanceState.Running => "运行中",
        InstanceState.Suspended => "暂停",
        InstanceState.Disposed => "已释放",
        _ => state.ToString(),
    };
}
