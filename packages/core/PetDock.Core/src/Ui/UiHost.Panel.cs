// 面板与插件页入口（RFC-13 §2.6）：把插件页的两个按钮接到宿主侧的真实动作上。
// 面板只发出意图（docs/18 §2.4「面板只读展示」在这里仍成立）：它给出 pluginId，
// 判定、弹窗与落账都在这一侧完成。
//
// 为什么复核与禁用都放到后台线程：
//   复核要向宿主调度线程问话（读名册、收窄授权都要在调度线程上执行），
//   而调度线程可能正阻塞在等另一次回答 —— 在 UI 线程上等它就是自己等自己（死锁）。
//   复核弹窗本身由滴答器弹出（端口的排队机制），所以后台线程只是「问」，不碰 UI。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>打开主控面板；已存在则只激活（§2.5 单实例）。</summary>
    public void OpenPanel()
    {
        if (_panel is not null && !_panel.IsDisposed)
        {
            _panel.Activate();
            return;
        }

        _panel = new ControlPanel(Snapshot, showWindow: false, AuthorizePlugin, TogglePluginDisabled);
        _panel.SetImportCommand(RunImportFromPanel);
        _panel.FormClosed += (_, _) =>
        {
            // 关闭面板不退出程序（§2.5）：只是把它丢掉，托盘还在。
            _panel?.Dispose();
            _panel = null;
        };
        _panel.Show();
    }

    /// <summary>取一次快照（面板每次重绘都调用它，不缓存）。</summary>
    public UiSnapshot Snapshot() =>
        UiRoster.Build(_pet, _host, _perf);

    /// <summary>
    /// 面板「导入素材包」入口的执行体（UI 线程）。
    /// 面板负责弹目录选择器、把目录交过来；宿主编排做复制、登记与换源（RFC-15 §3.3）。
    /// 失败时把原因交回面板，由面板显示成内联错误条 —— 不弹模态框（docs/18 §5）。
    /// </summary>
    private bool RunImportFromPanel(string sourceDir, out string message)
    {
        PetImportService? import = _import;
        if (import is null)
        {
            message = "导入功能不可用：没有可用的内容源。";
            return false;
        }

        ImportResult result = import.Import(sourceDir);
        message = result.Message;
        if (result.Ok)
        {
            _tray.SetPetCount(1);
        }

        return result.Ok;
    }

    /// <summary>触发一次性能采样并把结果留在快照里（面板的采样按钮走这里）。</summary>
    public void NotePerf(PerfSummary perf) => _perf = perf ?? PerfSummary.Empty;

    /// <summary>
    /// 插件页「授权」入口（RFC-13 §2.6）：打开复核弹窗，然后把授权**收窄**。
    /// 运行期不能新增授予（docs/14 §2「一次断言：安装时一次性提交能力清单；不允许补申请」）：
    /// 用户在复核时勾了此前没给过的能力，那一项**不授予**——只保留「此前已授予 ∩ 本次勾选」。
    /// 这一条在日志里如实说明，不靠界面暗示。
    /// </summary>
    public void AuthorizePlugin(string pluginId)
    {
        _ = Task.Run(() => AuthorizePluginCore(pluginId));
    }

    /// <summary>复核的本体（后台线程）。</summary>
    private void AuthorizePluginCore(string pluginId)
    {
        PluginHost? host = _host;
        UiConsentPort? consent = _consent;
        if (host is null || consent is null)
        {
            KernelLog.Write($"authorize-skip plugin={pluginId} reason=no-host-or-port");
            return;
        }

        PluginRow? row = FindRow(host, pluginId);
        if (row is null)
        {
            KernelLog.Write($"authorize-skip plugin={pluginId} reason=not-loaded");
            return;
        }

        IReadOnlyCollection<string> denied = host.Consent.Denied(pluginId);
        var items = new List<ConsentItem>();
        foreach (string id in row.CapabilityIds)
        {
            items.Add(new ConsentItem(id, row.GrantedIds.Contains(id), denied.Contains(id)));
        }

        ConsentAnswer? answer = consent.Ask(new ConsentRequest(
            pluginId,
            row.Name,
            row.Tier,
            items,
            IsReview: true));

        if (answer is null)
        {
            KernelLog.Write($"authorize-skip plugin={pluginId} reason=no-answer");
            return;
        }

        var keep = new List<string>();
        foreach (string id in answer.Granted)
        {
            if (row.GrantedIds.Contains(id))
            {
                keep.Add(id);
            }
        }

        int notGranted = answer.Granted.Count(id => !row.GrantedIds.Contains(id));
        if (notGranted > 0)
        {
            // 复核里勾了此前没给过的能力：运行期不补授（docs/14 §2），只说明、不假装授了。
            KernelLog.Write($"authorize-note plugin={pluginId} not-granted count={notGranted} reason=runtime-no-new-grant");
        }

        bool changed = host.RestrictConsent(pluginId, keep);
        KernelLog.Write($"authorize-done plugin={pluginId} keep={keep.Count} changed={changed}");
    }

    /// <summary>
    /// 插件页「禁用/启用」入口：Running → 禁用；Suspended → 启用（不卸载，docs/13 §3）。
    /// 同样放到后台线程：动作要过宿主调度线程，在 UI 线程上等它会把界面冻住。
    /// </summary>
    public void TogglePluginDisabled(string pluginId)
    {
        if (_host is null)
        {
            return;
        }

        _ = Task.Run(() => TogglePluginDisabledCore(pluginId));
    }

    /// <summary>禁用/启用的本体（后台线程）。</summary>
    private void TogglePluginDisabledCore(string pluginId)
    {
        PluginHost? host = _host;
        if (host is null)
        {
            return;
        }

        PluginHandle? handle = FindHandle(host, pluginId);
        if (handle is null)
        {
            KernelLog.Write($"toggle-skip plugin={pluginId} reason=not-loaded");
            return;
        }

        try
        {
            if (handle.State == PluginLifecycleState.Running)
            {
                host.Disable(handle).GetAwaiter().GetResult();
                KernelLog.Write($"plugin-disabled plugin={pluginId}");
                return;
            }

            if (handle.State == PluginLifecycleState.Suspended)
            {
                handle.Resume();
                KernelLog.Write($"plugin-enabled plugin={pluginId}");
                return;
            }

            KernelLog.Write($"toggle-skip plugin={pluginId} reason=state-{handle.State}");
        }
        catch (Exception ex)
        {
            KernelLog.Write($"toggle-failed plugin={pluginId} {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>按 id 找宿主侧的行（授权复核要读它的声明能力与已授予能力）。</summary>
    private static PluginRow? FindRow(PluginHost host, string pluginId)
    {
        foreach (PluginRow row in host.Roster())
        {
            if (string.Equals(row.Id, pluginId, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>按 id 找句柄（禁用/启用要按 id 说话，面板不持有句柄）。</summary>
    private static PluginHandle? FindHandle(PluginHost host, string pluginId)
    {
        try
        {
            foreach (PluginHandle handle in host.HandlesSnapshot())
            {
                if (string.Equals(handle.Manifest.Id, pluginId, StringComparison.Ordinal))
                {
                    return handle;
                }
            }
        }
        catch (TimeoutException)
        {
            KernelLog.Write("plugin-lookup-timeout scheduler-stalled");
        }

        return null;
    }
}
