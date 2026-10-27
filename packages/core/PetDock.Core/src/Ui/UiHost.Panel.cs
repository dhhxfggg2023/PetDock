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
        // 语言热切换：设置里存的是另一门语言时，在**构造面板之前**换表（2026-10-27）。
        // 之前的口径是「切换后需重启 PetDock」——重启这件事对用户来说就是「没生效」；
        // 文案表是纯静态查表，重读一次成本可忽略，所以改成「重新打开面板即生效」，
        // 已经开着的旧语言窗口直接丢掉重建，避免同一窗里半中半英。
        string? wanted = _settings?.Load().Language;
        if (!string.IsNullOrWhiteSpace(wanted)
            && !string.Equals(wanted, I18n.Language, StringComparison.OrdinalIgnoreCase))
        {
            I18n.Load(wanted);
            KernelLog.Write($"i18n-reloaded lang={I18n.Language} via=panel-open");
            if (_panel is not null && !_panel.IsDisposed)
            {
                ControlPanel stale = _panel;
                _panel = null;
                stale.Dispose();
            }
        }

        if (_panel is not null && !_panel.IsDisposed)
        {
            _panel.Activate();
            return;
        }

        _panel = new ControlPanel(Snapshot, showWindow: false, AuthorizePlugin, TogglePluginDisabled, FrameReadingsFor);
        _panel.SetImportCommand(RunImportFromPanel);
        _panel.SetSettingsCommand(ApplySettings);
        _panel.SetDiagnosticCommand(RunDiagnosticOp);
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
        UiRoster.Build(_pet, _host, _perf, SettingsView());

    /// <summary>
    /// 帧泵的逐宠物读数（RFC-30 §3.2 / RFC-37 片 4）：按**宠物 id**取，桌上有几只就有几份读数。
    /// 先问集合（每只一个泵），集合里没有这一只才落到主窗循环；都没有 → null，
    /// 面板据此写「—」而不是 0（0 会被读成「测过是 0」）。
    /// </summary>
    private PetFrameReadings? FrameReadingsFor(string petId)
    {
        if (_petSet is not null)
        {
            foreach (PetReading reading in _petSet.Readings())
            {
                if (string.Equals(reading.Id, petId, StringComparison.Ordinal))
                {
                    return reading.Frames;
                }
            }
        }

        ResidentFrameLoop? loop = _loop;
        if (loop is null || _pet is null || !string.Equals(_pet.Id, petId, StringComparison.Ordinal))
        {
            return null;
        }

        return new PetFrameReadings(loop.AvgFrameMs, loop.MaxFrameMs, loop.TargetFrameMs, loop.OverBudgetFrames);
    }

    /// <summary>
    /// 设置页的当前状态（读数来源：设置文件 + 注册表）。
    /// 开机自启从注册表读——它的事实源就在那儿，设置文件不重复保存这一项。
    /// </summary>
    private SettingsView SettingsView()
    {
        UserSettings settings = _settings?.Load() ?? UserSettings.Default;
        return new SettingsView(
            settings.IdleFrameBudgetMs,
            settings.Language,
            StartupRegistration.IsEnabled());
    }

    /// <summary>
    /// 设置页「应用」的执行体（UI 线程）。
    /// 顺序：① 开机自启（注册表）② 落盘设置文件 ③ 把新阈值推给帧泵。
    /// 每一步都可能失败，失败要说清楚是哪一步——「已保存设置，但开机自启未生效」比「保存失败」诚实。
    /// 返回 null = 全部成功；返回字符串 = 失败或部分失败的原因。
    /// </summary>
    private string? ApplySettings(double idleFrameBudgetMs, bool startWithWindows, Design.MaterialKind material, Design.ThemeMode theme)
    {
        var problems = new List<string>();

        // ① 开机自启：事实源是注册表。要不要动它，取决于「勾选状态与注册表现状是否一致」。
        bool registered = StartupRegistration.IsEnabled();
        if (startWithWindows != registered)
        {
            string? failure = startWithWindows
                ? StartupRegistration.Enable()
                : StartupRegistration.Disable();
            if (failure is not null)
            {
                problems.Add(failure);
            }
        }

        // ② 落盘。写失败要说清楚「设置没存上」。
        //
        // 读出 → 只改一个字段 → 整份写回（RFC-16 §3.2 的纪律）。
        // 凭记忆造一份 UserSettings 写回去会抹掉引导的一次性标记：
        // 用户在引导里做完（标记已写），随后在设置页点一次「应用」，标记被抹，
        // 下次启动引导又弹一遍。这里的 `with` 表达式保住其余字段。
        UserSettings previous = _settings?.Load() ?? UserSettings.Default;
        var updated = previous with
        {
            IdleFrameBudgetMs = idleFrameBudgetMs,
            Material = Design.Theme.MaterialToken(material),
            ThemeMode = Design.Theme.ModeToken(theme),
        };
        try
        {
            _settings?.Save(updated);
        }
        catch (Exception error)
        {
            problems.Add(I18n.Get("ui.panel.settingsSaveFailed", error.Message));
        }

        // ③ 材质与亮度立即生效（细节在 UiHost.Appearance.cs：材质走 DWM，亮度要重建面板）。
        string? appearanceProblem = ApplyAppearance(material, theme);
        if (appearanceProblem is not null)
        {
            problems.Add(appearanceProblem);
        }

        // ④ 立即推给帧泵（不用重启）。这一步不会失败：越界值在 FramePump 里被拉回合法区间。
        _loop.SetTargetFrameMs(idleFrameBudgetMs);
        KernelLog.Write(
            $"settings-applied idle={updated.IdleFrameBudgetMs:0.0}ms startup={startWithWindows} material={updated.Material}");

        return problems.Count == 0 ? null : string.Join(I18n.Get("ui.panel.problemSeparator"), problems);
    }

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
            message = I18n.Get("ui.panel.importUnavailable");
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
                PersistPluginDisabled(pluginId, disabled: true);
                return;
            }

            if (handle.State == PluginLifecycleState.Suspended)
            {
                handle.Resume();
                KernelLog.Write($"plugin-enabled plugin={pluginId}");
                PersistPluginDisabled(pluginId, disabled: false);
                return;
            }

            KernelLog.Write($"toggle-skip plugin={pluginId} reason=state-{handle.State}");
        }
        catch (Exception ex)
        {
            KernelLog.Write($"toggle-failed plugin={pluginId} {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 把「用户关掉了这只插件」落盘（2026-10-26 用户要求「插件可以选择关闭，重启生效」）。
    /// 运行时那半由 <see cref="PluginHost.Disable"/> 负责，这里管的是**下一轮**：
    /// 落进 settings.json 的 pluginsDisabled，Program 起装载之前读它、直接跳过这些 id。
    /// 没有设置存储（自检形态）就只留一行日志 —— 不假装记住了。
    /// </summary>
    private void PersistPluginDisabled(string pluginId, bool disabled)
    {
        UserSettingsStore? store = _settings;
        if (store is null)
        {
            KernelLog.Write($"plugin-toggle-not-persisted plugin={pluginId} reason=no-settings");
            return;
        }

        try
        {
            UserSettings current = store.Load();
            var ids = current.DisabledPlugins is null
                ? new List<string>()
                : new List<string>(current.DisabledPlugins);
            bool present = ids.Exists(id => string.Equals(id, pluginId, StringComparison.Ordinal));
            if (present == disabled)
            {
                return; // 已经是这个状态：不白写一次盘
            }

            if (disabled)
            {
                ids.Add(pluginId);
            }
            else
            {
                ids.RemoveAll(id => string.Equals(id, pluginId, StringComparison.Ordinal));
            }

            store.Save(current with { DisabledPlugins = ids });
            KernelLog.Write($"plugin-disabled-persisted plugin={pluginId} disabled={(disabled ? 1 : 0)} count={ids.Count}");
        }
        catch (Exception error)
        {
            KernelLog.Write($"plugin-toggle-persist-failed plugin={pluginId} {error.GetType().Name}: {error.Message}");
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
