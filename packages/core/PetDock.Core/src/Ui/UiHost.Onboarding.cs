// 首次运行引导的宿主侧编排（RFC-16 §3.3）。
//
// 触发时机：**滴答器里**（不是构造函数里）。构造期没有消息泵，
// 那时开窗会建出一个不泵消息的窗体（RFC-13 §2.4 的同一条纪律，RFC-16 §3.3 照抄）。
//
// 与授权窗互斥：模态窗自带消息循环，而宿主滴答器在模态期间**仍会触发**。
// 所以引导窗开着的时候滴答器不许泵授权询问 —— 否则两个模态窗叠在一起，
// 用户先看到授权弹窗压在引导上面。询问留在 UiConsentPort 的队列里
// （PendingCount 可证），引导关掉后照常弹。
//
// 一次性判定：读 settings.json 的可空字段 onboardingCompletedVersion（RFC-16 §3.1）。
// 写盘一律「读出 → 改一个字段 → 整份写回」（RFC-16 §3.2），
// 绝不能凭记忆造一份 UserSettings 写回去 —— 那会把设置页写的字段抹掉。
using System;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>引导窗是否正开着（滴答器据此决定要不要泵授权询问）。</summary>
    private OnboardingWindow? _onboarding;

    /// <summary>引导是否已经跑过（做过就再也不弹；跳过一次也算做过）。</summary>
    private bool _onboardingDone;

    /// <summary>引导窗当前是否开着（自检读数；不读控件的 Visible）。</summary>
    public bool OnboardingOpen => _onboarding is not null && !_onboarding.IsDisposed;

    /// <summary>引导窗实例（自检读它的结果；未弹过为 null）。</summary>
    public OnboardingWindow? Onboarding => _onboarding;

    /// <summary>滴答器泵过几次授权询问（自检观察互斥纪律用）。</summary>
    internal int ConsentPumpCount { get; private set; }

    /// <summary>
    /// 该跑引导吗（**纯读**，不改任何状态）。
    /// 判据是 settings.json 的可空字段：null 或版本比当前引导旧 ⇒ 该跑。
    /// </summary>
    internal bool NeedsOnboarding()
    {
        UserSettings settings = _settings?.Load() ?? UserSettings.Default;
        return settings.OnboardingCompletedVersion is not int version
            || version < UserSettings.OnboardingVersion;
    }

    /// <summary>
    /// 滴答里的引导触发（UI 线程）。
    /// 顺序：宿主启动 → 宠物窗口显示 → **引导** → 授权询问 → 装钩子。
    /// 引导排在授权询问之前：第一次运行时用户还没授权任何插件，
    /// 先把「这是一只桌宠」讲清楚，再问要不要给它能力。
    /// </summary>
    private void PumpOnboarding()
    {
        if (_onboardingDone || OnboardingOpen)
        {
            return;
        }

        // 没有设置存储就没有一次性判定的落点（无法记住「做过了」）⇒ 不弹。
        // 生产路径上设置存储总是有的（Program.cs 一定会造一个）。
        if (_settings is null)
        {
            return;
        }

        if (!NeedsOnboarding())
        {
            _onboardingDone = true;
            return;
        }

        // 先置位再跑：引导窗里出了岔子（写盘失败等）也不能让同一个会话反复弹。
        _onboardingDone = true;
        RunOnboarding();
    }

    /// <summary>跑一次引导（模态）。</summary>
    private void RunOnboarding()
    {
        bool hasPet = _frames?.Roster?.HasView == true;
        using var window = new OnboardingWindow(PlaceBundledExample, WriteOnboardingSettings, hasPet);
        AttachOnboarding(window);
        try
        {
            window.ShowDialog();
        }
        catch (Exception error)
        {
            KernelLog.Write($"onboarding-failed {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            AttachOnboarding(null);
        }
    }

    /// <summary>
    /// 挂上/摘下引导窗（生产由 RunOnboarding 调；自检用它观察「引导开着时不泵授权询问」）。
    /// 挂着的窗不一定是显示着的 —— 互斥纪律看的是「有没有引导窗在管事」。
    /// </summary>
    internal void AttachOnboarding(OnboardingWindow? window) => _onboarding = window;

    /// <summary>
    /// 引导第 2 步：放置应用自带的示例宠物。
    /// 走的是**既有的** PetImportService.Import（复制 → 登记 → 换源 → 释放旧实例），
    /// 不新造一条放置路径 —— 那样会有两套「导入」语义。
    /// 返回 null = 成功；返回字符串 = 失败原因（页内内联显示）。
    /// </summary>
    private string? PlaceBundledExample()
    {
        PetImportService? import = _import;
        if (import is null)
        {
            return I18n.Get(OnboardingWindow.NoPetKey);
        }

        string? pack = BundledExampleAssets.Locate();
        if (pack is null)
        {
            return I18n.Get(OnboardingWindow.NoSampleKey);
        }

        ImportResult result = import.Import(pack);
        if (!result.Ok)
        {
            return result.Message;
        }

        _tray.SetPetCount(1);
        KernelLog.Write($"onboarding-sample-placed instance={result.InstanceId ?? "(unknown)"}");
        return null;
    }

    /// <summary>
    /// 引导写设置：读出 → 改一个字段 → 整份写回。
    /// language = null 表示不改语言；complete = true 表示写一次性标记。
    /// 写失败返回 false 并带上原因（引导据此内联提示「下次启动还会出现」）。
    /// </summary>
    private bool WriteOnboardingSettings(string? language, bool complete, out string error)
    {
        error = string.Empty;
        UserSettingsStore? store = _settings;
        if (store is null)
        {
            error = "没有设置存储";
            return false;
        }

        try
        {
            UserSettings previous = store.Load();
            var updated = previous with
            {
                Language = language ?? previous.Language,
                OnboardingCompletedVersion = complete
                    ? UserSettings.OnboardingVersion
                    : previous.OnboardingCompletedVersion,
            };
            store.Save(updated);
            return true;
        }
        catch (Exception ex)
        {
            error = $"设置未能保存：{ex.Message}";
            KernelLog.Write($"onboarding-settings-failed {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
