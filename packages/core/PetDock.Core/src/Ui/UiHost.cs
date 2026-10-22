// 常驻 UI 宿主（RFC-12 §2.2）：把托盘、主控面板、宠物窗口串成一条生命周期。
// 为什么是 ApplicationContext 而不是 Application.Run(Form)：宠物窗口只是众多窗口之一，
// 用某个窗体当消息循环的宿主会让「关闭面板 = 退出程序」——docs/18 §2.5 明确禁止这种行为。
// RFC-13 §2.4 补了一条约束：装载期授权要弹窗，而弹窗只能开在 UI 线程上。所以本文件持有一个
// 滴答器，每个滴答做三件事：启动宿主（装载必须等消息泵起来）、弹出待答的授权询问、按持权装钩子。
//
// 本文件是 partial 的主文件：只留字段、构造、公开读数、滴答与释放。
// 拆分的理由是 D-03c 的单文件 400 行上限；各半的职责：
//   UiHost.cs        字段、构造、公开读数、滴答、释放
//   UiHost.Hook.cs   键盘钩子的安装门（docs/16:56）
//   UiHost.Tray.cs   托盘命令
//   UiHost.Panel.cs  面板、插件页入口与授权复核
using System;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.Input;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost : ApplicationContext
{
    /// <summary>滴答间隔（毫秒）：够快让询问及时弹出来，又不至于让 UI 一直忙。</summary>
    private const int TickMs = 250;

    private readonly PetInstance? _initialPet;
    private readonly PluginHost? _host;
    private readonly LayeredFrameWindow _window;
    private readonly SwappableFrameSource? _frames;
    private readonly PetImportService? _import;
    private readonly TrayHost _tray;
    private readonly UiConsentPort? _consent;
    private readonly ResidentFrameLoop _loop;

    /// <summary>设置存储（可空：没有数据区就没有设置页的持久化；自检形态常常是 null）。</summary>
    private readonly UserSettingsStore? _settings;

    /// <summary>当前宠物（导入会换它，所以不是 readonly）。</summary>
    private PetInstance? _pet;

    /// <summary>滴答器。用的是 System.Windows.Forms.Timer（靠消息循环驱动）——
    /// System.Threading.Timer 跑在自己的线程上，弹窗会开到没有消息泵的线程上。
    /// （本工程开了 ImplicitUsings，System.Threading 与 System.Windows.Forms 都在隐式引入里，
    /// 所以这里写全名：两个 Timer 同名，不写全名会得到 CS0104 二义性错误。）</summary>
    private readonly System.Windows.Forms.Timer _timer;

    private ControlPanel? _panel;
    private PerfSummary _perf = PerfSummary.Empty;
    private bool _hooksPaused;
    private bool _hostStarted;
    private bool _petShown;
    private bool _disposed;

    public UiHost(
        LayeredFrameWindow window,
        PetInstance? pet,
        PluginHost? host,
        SwappableFrameSource? frames = null,
        UiConsentPort? consent = null,
        UserSettingsStore? settings = null,
        DiagnosticService? diagnostics = null)
    {
        _window = window;
        _initialPet = pet;
        _pet = pet;
        _host = host;
        _frames = frames;
        _consent = consent;
        _settings = settings;
        _diagnostics = diagnostics;
        _loop = new ResidentFrameLoop(window);

        // 启动时读设置：空闲降帧阈值立即套用到帧泵（不用等用户点「应用」）。
        if (_settings is not null)
        {
            UserSettings loaded = _settings.Load();
            _loop.SetTargetFrameMs(loaded.IdleFrameBudgetMs);
            KernelLog.Write($"settings-loaded {loaded.Describe()}");
        }

        // 导入服务：宿主编排在它那里，UI 只负责「选目录」与「显示错误条」（RFC-15 §3.5）。
        if (frames is not null && frames.Roster is not null)
        {
            _import = new PetImportService(
                PetDataStore.Resolve(),
                frames,
                frames.Roster,
                () => _pet,
                next => _pet = next);
        }

        _tray = new TrayHost(
            petName: pet?.Id ?? "(无)",
            toggleAll: ToggleAll,
            openPanel: OpenPanel,
            movePet: () => _window.MoveToCenter(),
            pausePet: PausePet,
            hidePet: HidePet,
            removePet: RemovePet,
            pauseHooks: PauseHooks,
            exit: ExitApp);

        _tray.SetPetCount(pet is null ? 0 : 1);

        // 滴答器在这里起：装载期的授权询问必须等消息泵起来才有地方弹窗（RFC-13 §2.4）。
        _timer = new System.Windows.Forms.Timer { Interval = TickMs };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>导入服务（可空：没有名册就没有导入能力；自检用它读结果）。</summary>
    public PetImportService? Import => _import;

    /// <summary>键盘钩子是否装着（自检与诊断用；不暴露可写入口）。</summary>
    public bool KeyboardHookInstalled => _keyHook?.IsInstalled == true;

    /// <summary>托盘（自检用）。</summary>
    public TrayHost Tray => _tray;

    /// <summary>当前面板实例（未打开时为 null；自检用它断言单实例）。</summary>
    public ControlPanel? Panel => _panel;

    /// <summary>面板是否打开（自检用）。</summary>
    public bool PanelOpen => _panel is not null && !_panel.IsDisposed;

    /// <summary>全局输入钩子是否已按用户要求停止（docs/18 §1 强制项）。</summary>
    public bool HooksPaused => _hooksPaused;

    /// <summary>宿主是否已经由滴答器启动（装载发生在消息泵起来之后，RFC-13 §2.4）。</summary>
    public bool HostStarted => _hostStarted;

    /// <summary>授权端口（自检用；null = 没有 UI 授权端口）。</summary>
    public UiConsentPort? Consent => _consent;

    /// <summary>常驻帧循环（自检用；判据要能读到真实的生产对象，而不是自己造一份）。</summary>
    public ResidentFrameLoop Loop => _loop;

    /// <summary>
    /// 滴答（UI 线程）：五件事，顺序就是它们的依赖顺序。
    /// ① 首次滴答才启动宿主 —— 装载必须在消息泵起来之后，否则授权弹窗无处可开（RFC-13 §2.4）；
    /// ② 首次滴答才把宠物窗口显示出来并起帧泵 —— 构造期还没有消息泵，
    ///    在那时 Show() 会建出一个不泵消息的窗体（RFC-14 §2.2）；
    /// ③ 首次运行时弹一次引导（RFC-16 §3.3）：构造期没有消息泵，所以也放在滴答里；
    /// ④ 弹出待答的授权询问（装载期与复核期都走这一条）——
    ///    **引导窗开着的时候不泵**：模态窗自带消息循环，滴答器在模态期间仍会触发，
    ///    两个模态窗叠在一起，用户先看到的是压在引导上的授权框。询问留在端口队列里
    ///    （UiConsentPort.PendingCount 可证），引导关掉后照常弹；
    /// ⑤ 按当前持权情况装键盘钩子（现在才可能真的有持权者了）。
    /// </summary>
    private void OnTick(object? sender, EventArgs e) => TickOnce();

    /// <summary>
    /// 滴答的正文（UI 线程）。拆成独立方法是给自检一个**可驱动的**入口：
    /// 判据要证明「引导窗开着时不泵授权询问」，就得能在一个确定的时间里点一次滴答，
    /// 而不是等计时器自己撞上（撞上了也不知道是第几次）。
    /// </summary>
    internal void TickOnce()
    {
        if (!_hostStarted && _host is not null)
        {
            _hostStarted = true;
            _host.Start();
        }

        // 只在首滴答显示一次：用户从托盘隐藏宠物之后，任何滴答都不许把它弹回来。
        if (!_petShown)
        {
            _petShown = true;
            _loop.Start();
        }

        // RFC-21 §3.4：全屏档把泵停了，没有泵 tick 就没有人重新判定 ——
        // 恢复必须由泵之外的东西驱动。这里是全屏档**唯一**的恢复驱动（TickMs = 250）。
        // 它只判定与起泵，不提交帧（离开全屏那一帧除外）；窗口不可见时它什么都不做
        // （可见性归 Start/Hide 独占，这里碰它就是第二个可见性属主）。
        _loop.Reevaluate();

        PumpOnboarding();

        // 引导窗开着时不泵授权询问（询问留队列，不丢）。
        if (!OnboardingOpen)
        {
            _consent?.Pump();
            ConsentPumpCount++;
        }

        InstallKeyboardHookIfGranted();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
            _consent?.Close();
            _loop.Dispose();
            _keyHook?.Dispose();
            _keyHook = null;
            _panel?.Dispose();
            _panel = null;
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
