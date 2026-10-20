// 授权记忆的跨进程接线（RFC-19 §2.3）。三个点：装载前播种、落账后写盘、收窄后写盘。
//
// 为什么单独一个 partial 文件：PluginHost.Roster.cs 已经 308 行，再加就会撞 D-03c 的 400 行上限；
// 而这三个点都属于「授权记忆的持久化」，语义上自成一体。
//
// 写盘失败**不让授权失败**：授权已经在闸门里生效了，写盘只是记性。写不进去就记一行诊断、继续。
using System;
using System.Collections.Generic;
using System.Linq;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

public sealed partial class PluginHost
{
    /// <summary>授权记忆的落盘存储（可空：没有数据区就没有跨进程记忆；自检形态常常是 null）。</summary>
    private ConsentStore? _consentStore;

    /// <summary>
    /// 接上落盘存储并**立刻播种**：把上次进程记住的授予灌进会话记忆。
    /// 播种之后，「允许（记住这个决定）」过的插件在这次启动不再被问（RFC-19 §2.3 第 1 点）。
    /// 播种只写会话记忆的授予集，不写拒绝集 —— 拒绝不跨进程（RFC-19 §2.2）。
    /// </summary>
    internal void AttachConsentStore(ConsentStore? store)
    {
        _consentStore = store;
        if (store is null)
        {
            return;
        }

        Dictionary<string, IReadOnlyList<string>> saved;
        try
        {
            saved = store.Load();
        }
        catch (Exception error)
        {
            KernelLog.Write($"consent-seed-failed {error.GetType().Name}: {error.Message}");
            return;
        }

        int plugins = 0;
        foreach (KeyValuePair<string, IReadOnlyList<string>> entry in saved)
        {
            // rememberGrants: true —— 播种的就是「记住的授予」。
            // denied 传空集：落盘里没有拒绝（RFC-19 §2.2），不能凭空造出拒绝来。
            Consent.Remember(entry.Key, entry.Value, Array.Empty<string>(), rememberGrants: true);
            plugins++;
        }

        if (plugins > 0)
        {
            KernelLog.Write($"consent-seeded plugins={plugins}");
        }
    }

    /// <summary>
    /// 把某插件当前的授予集落盘（RFC-19 §2.3 第 2、3 点）。
    /// 只在「记住」的语义下写：「仅本次」不写（它下次就该再问）。
    /// 写盘失败只记诊断，不抛 —— 授权已经生效。
    /// </summary>
    private void SaveConsentGrants(string pluginId, bool remember)
    {
        if (!remember || _consentStore is null)
        {
            return;
        }

        try
        {
            string[] current = Consent.Granted(pluginId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Dictionary<string, IReadOnlyList<string>> all = _consentStore.Load();
            if (current.Length == 0)
            {
                // 授予集空了 = 全部被撤销。要把这条记录**从文件里删掉**，
                // 而不是留一个旧集合在盘上（否则下次启动能力会悄悄回来 ——
                // 比「撤销没生效」更坏，RFC-19 §2.3 第 3 点要防的正是这个）。
                all.Remove(pluginId);
            }
            else
            {
                all[pluginId] = current;
            }

            _consentStore.Save(all);
            KernelLog.Write($"consent-saved plugin={pluginId} granted={current.Length}");
        }
        catch (Exception error)
        {
            // 记性写不进去 ≠ 授权失败。授权已经在闸门里生效了。
            KernelLog.Write($"consent-save-failed plugin={pluginId} {error.GetType().Name}: {error.Message}");
        }
    }
}
