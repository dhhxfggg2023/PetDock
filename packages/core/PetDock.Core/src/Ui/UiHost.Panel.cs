// 面板与插件页入口（RFC-13 §2.6）：把插件页的两个按钮接到宿主侧的真实动作上。
// 面板只发出意图（原规格 18 §2.4「面板只读展示」在这里仍成立）：它给出 pluginId，
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
            _tray.ReloadLanguage();
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

        _panel = new ControlPanel(this, LoadUiPages(), showWindow: false);
        _panel.FormClosed += (_, _) =>
        {
            // 关闭面板不退出程序（§2.5）：只是把它丢掉，托盘还在。
            _panel?.Dispose();
            _panel = null;
        };
        _panel.Show();
    }

    /// <summary>取一次快照（面板每次重绘都调用它，不缓存）。</summary>
    public UiSnapshot Snapshot()
    {
        UiSnapshot snapshot = UiRoster.Build(
            _primaryRemoved ? null : _pet,
            _host,
            SettingsView(),
            _inventory,
            BuildExtraPetLines(),
            _primaryRemoved ? null : _store?.ReadActive()?.InstanceId,
            // 主窗那只也可能被收起（用户点了「从桌面移除」）：页面据此显示它现在的状态。
            primaryOnDesktop: !_primaryRemoved,
            // 每只自己的尺寸（2026-10-31）：宠物页选中某只之后滑块显示的是**这一只**的数。
            primarySize: _settings?.Load().SizeOf(PrimaryPetIdOf()));
        return DecoratePetMetadata(snapshot);
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
            StartupRegistration.IsEnabled(),
            settings.ThemeMode,
            settings.Material,
            settings.PetSize ?? UserSettings.DefaultPetSize,
            _primaryRemoved ? null : _pet?.DisplayName,
            AppVersion,
            settings.SoundEnabled != false,
            settings.CelestialMode == true);
    }

    /// <summary>
    /// 设置改动的执行体（UI 线程）：每改一项就落一次盘。
    /// 顺序：① 开机自启（注册表）② 落盘设置文件 ③ 把新阈值推给帧泵。
    /// 每一步都可能失败，失败要说清楚是哪一步——「已保存设置，但开机自启未生效」比「保存失败」诚实。
    /// 返回 null = 全部成功；返回字符串 = 失败或部分失败的原因。
    /// </summary>
    private string? ApplySettings(
        double idleFrameBudgetMs,
        bool startWithWindows,
        Design.MaterialKind material,
        Design.ThemeMode theme,
        bool soundEnabled,
        bool celestialMode)
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
        string? materialToken = Design.Theme.MaterialToken(material);
        string? themeToken = Design.Theme.ModeToken(theme);
        UserSettings updated = (_settings?.Load() ?? UserSettings.Default) with
        {
            IdleFrameBudgetMs = idleFrameBudgetMs,
            Material = materialToken,
            ThemeMode = themeToken,
            SoundEnabled = soundEnabled,
            CelestialMode = celestialMode,
        };
        try
        {
            if (_settings is not null)
            {
                // 原子写：拿到的是「落盘时」的最新一份，不会把别人刚写的字段改回去。
                updated = _settings.Update(current => current with
                {
                    IdleFrameBudgetMs = idleFrameBudgetMs,
                    Material = materialToken,
                    ThemeMode = themeToken,
                    SoundEnabled = soundEnabled,
                    CelestialMode = celestialMode,
                });
            }
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

        // ⑤ 静音立即生效（进程级开关；已经响着的那一条不打断 —— 打断反而像故障）。
        SoundBoard.Enabled = soundEnabled;

        // ⑥ 天体模式立即生效（打开时宿主独占运动，关掉把属主还给行为层）。
        ApplyCelestialMode(celestialMode);
        KernelLog.Write(
            $"settings-applied idle={updated.IdleFrameBudgetMs:0.0}ms startup={startWithWindows} material={updated.Material}");

        return problems.Count == 0 ? null : string.Join(I18n.Get("ui.panel.problemSeparator"), problems);
    }

    /// <summary>程序版本（设置页「关于」与左下角版本位共用；三段式，不带构建元数据）。</summary>
    private static string AppVersion =>
        typeof(UiHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// 改**这一只**宠物的窗口尺寸（2026-10-31；用户：「调整大小应该是每个宠物单独调整，
    /// 你这样我怎么调整」）。旧实现是一条全局滑块 —— 拖它会把桌上每一只一起改成同一个数，
    /// 想「大猫小老鼠」做不到。现在尺寸按宠物记（settings.petSizes，键是清单 id）。
    ///
    /// petId 为 null = 没有选中任何一只：只改默认值（新导入的宠物按它开局），不动已经在桌上的。
    /// 返回 null = 成功；否则是给用户看的原因。
    /// </summary>
    private string? SetPetSizeFromPanel(string? petId, int size)
    {
        int clamped = Math.Clamp(size, UserSettings.MinPetSize, UserSettings.MaxPetSize);
        try
        {
            if (petId is null)
            {
                _settings?.Update(current => current with { PetSize = clamped });
            }
            else
            {
                _settings?.Update(current => current.WithPetSize(petId, clamped));
            }
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-size-save-failed {error.GetType().Name}: {error.Message}");
            return I18n.Get("ui.panel.settingsSaveFailed", error.Message);
        }

        if (petId is null)
        {
            KernelLog.Write($"pet-size-default-applied size={clamped}");
            return null;
        }

        // 主窗那只不归 PetWindowSet 管，按 id 分派；桌面上没有它（收起/已删）就只记设置，
        // 下次摆上桌时按 settings.petSizes 取尺寸（见 PlaceAtStart / AttachPetSet）。
        if (!_primaryRemoved && IsPrimary(petId))
        {
            _window.SetLogicalSize(clamped);
        }
        else
        {
            _petSet?.SetLogicalSize(petId, clamped);
        }

        KernelLog.Write($"pet-size-applied id={petId} size={clamped}");
        RefreshUi();
        return null;
    }

    /// <summary>
    /// 插件页「授权」入口（RFC-13 §2.6）：打开复核弹窗，然后把授权**收窄**。
    /// 运行期不能新增授予（原规格 14 §2「一次断言：安装时一次性提交能力清单；不允许补申请」）：
    /// 用户在复核时勾了此前没给过的能力，那一项**不授予**——只保留「此前已授予 ∩ 本次勾选」。
    /// 这一条在日志里如实说明，不靠界面暗示。
    /// </summary>
    public void AuthorizePlugin(string pluginId)
    {
        _ = Task.Run(() =>
        {
            try
            {
                AuthorizePluginCore(pluginId);
            }
            catch (Exception error)
            {
                KernelLog.Write($"authorize-failed plugin={pluginId} {error.GetType().Name}: {error.Message}");
                ReportPluginFailure(I18n.Get("cp.plugins.authorize.failed"));
            }
        });
    }

    /// <summary>复核的本体（后台线程）。</summary>
    private void AuthorizePluginCore(string pluginId)
    {
        PluginHost? host = _host;
        UiConsentPort? consent = _consent;
        if (host is null || consent is null)
        {
            KernelLog.Write($"authorize-skip plugin={pluginId} reason=no-host-or-port");
            ReportPluginFailure(I18n.Get("cp.plugins.authorize.unavailable"));
            return;
        }

        PluginRow? row = FindRow(host, pluginId);
        if (row is null)
        {
            KernelLog.Write($"authorize-skip plugin={pluginId} reason=not-loaded");
            ReportPluginFailure(I18n.Get("cp.plugins.toggle.unknown"));
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
            RefreshUiOnUi();
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
            // 复核里勾了此前没给过的能力：运行期不补授（原规格 14 §2），只说明、不假装授了。
            KernelLog.Write($"authorize-note plugin={pluginId} not-granted count={notGranted} reason=runtime-no-new-grant");
        }

        bool changed = host.RestrictConsent(pluginId, keep);
        KernelLog.Write($"authorize-done plugin={pluginId} keep={keep.Count} changed={changed}");
        RefreshUiOnUi();
    }
}
