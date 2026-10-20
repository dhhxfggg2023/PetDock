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

    private readonly PetInstance? _pet;
    private readonly PluginHost? _host;
    private readonly LayeredFrameWindow _window;
    private readonly PetFrameSource? _windowSource;
    private readonly TrayHost _tray;
    private readonly UiConsentPort? _consent;

    /// <summary>滴答器。用的是 System.Windows.Forms.Timer（靠消息循环驱动）——
    /// System.Threading.Timer 跑在自己的线程上，弹窗会开到没有消息泵的线程上。
    /// （本工程开了 ImplicitUsings，System.Threading 与 System.Windows.Forms 都在隐式引入里，
    /// 所以这里写全名：两个 Timer 同名，不写全名会得到 CS0104 二义性错误。）</summary>
    private readonly System.Windows.Forms.Timer _timer;

    private ControlPanel? _panel;
    private PerfSummary _perf = PerfSummary.Empty;
    private bool _hooksPaused;
    private bool _hostStarted;
    private bool _disposed;

    public UiHost(
        LayeredFrameWindow window,
        PetInstance? pet,
        PluginHost? host,
        PetFrameSource? source = null,
        UiConsentPort? consent = null)
    {
        _window = window;
        _pet = pet;
        _host = host;
        _windowSource = source;
        _consent = consent;

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

    /// <summary>
    /// 滴答（UI 线程）：三件事，顺序就是它们的依赖顺序。
    /// ① 首次滴答才启动宿主 —— 装载必须在消息泵起来之后，否则授权弹窗无处可开（RFC-13 §2.4）；
    /// ② 弹出待答的授权询问（装载期与复核期都走这一条）；
    /// ③ 按当前持权情况装键盘钩子（现在才可能真的有持权者了）。
    /// </summary>
    private void OnTick(object? sender, EventArgs e)
    {
        if (!_hostStarted && _host is not null)
        {
            _hostStarted = true;
            _host.Start();
        }

        _consent?.Pump();

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
            _keyHook?.Dispose();
            _keyHook = null;
            _panel?.Dispose();
            _panel = null;
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
