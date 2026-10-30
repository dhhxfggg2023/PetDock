// 常驻 UI 宿主（RFC-12 §2.2）：把托盘、主控面板、宠物窗口串成一条生命周期。
// 为什么是 ApplicationContext 而不是 Application.Run(Form)：宠物窗口只是众多窗口之一，
// 用某个窗体当消息循环的宿主会让「关闭面板 = 退出程序」——原规格 18 §2.5 明确禁止这种行为。
// RFC-13 §2.4 补了一条约束：装载期授权要弹窗，而弹窗只能开在 UI 线程上。所以本文件持有一个
// 滴答器，每个滴答做三件事：启动宿主（装载必须等消息泵起来）、弹出待答的授权询问、按持权装钩子。
//
// 本文件是 partial 的主文件：只留字段、构造、公开读数、滴答与释放。
// 拆分的理由是 D-03c 的单文件 400 行上限；各半的职责：
//   UiHost.cs        字段、构造、公开读数、滴答、释放
//   UiHost.Hook.cs   键盘钩子的安装门（原规格 16:56）
//   UiHost.Tray.cs   托盘命令
//   UiHost.Panel.cs  面板、插件页入口与授权复核
//   UiHost.Position.cs 窗口位置的恢复与落盘（RFC-22）
using System;
using System.IO;
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

    /// <summary>主窗之外的那几只（RFC-37 §3.2）。可空：单只宠物时根本不存在这个集合。</summary>
    private PetWindowSet? _petSet;

    /// <summary>设置存储（可空：没有数据区就没有设置页的持久化；自检形态常常是 null）。</summary>
    private readonly UserSettingsStore? _settings;

    /// <summary>宠物清单（RFC-37 §3.6：托盘的「移除」要能把它从清单里删掉）。</summary>
    private readonly PetDataStore? _store;

    /// <summary>插件管理面（扫描/开关/删除）。没有宿主时为 null（自检形态常常是 null）。</summary>
    private readonly PluginInventory? _inventory;

    /// <summary>插件安装器（zip/目录 → 用户插件根）。没有宿主时为 null。</summary>
    private readonly PluginInstaller? _installer;

    /// <summary>当前宠物（导入会换它，所以不是 readonly）。</summary>
    private PetInstance? _pet;

    /// <summary>滴答器。用的是 System.Windows.Forms.Timer（靠消息循环驱动）——
    /// System.Threading.Timer 跑在自己的线程上，弹窗会开到没有消息泵的线程上。
    /// （本工程开了 ImplicitUsings，System.Threading 与 System.Windows.Forms 都在隐式引入里，
    /// 所以这里写全名：两个 Timer 同名，不写全名会得到 CS0104 二义性错误。）</summary>
    private readonly System.Windows.Forms.Timer _timer;

    private ControlPanel? _panel;
    private bool _hostStarted;

    /// <summary>主窗那只已经从清单里移除（RFC-37 §3.6）：它的实例归 Program，卸载要重启。</summary>
    private bool _primaryRemoved;
    private bool _petShown;
    private bool _disposed;

    public UiHost(
        LayeredFrameWindow window,
        PetInstance? pet,
        PluginHost? host,
        SwappableFrameSource? frames = null,
        UiConsentPort? consent = null,
        UserSettingsStore? settings = null,
        DiagnosticService? diagnostics = null,
        bool startHidden = false)
    {
        _window = window;
        _primaryRemoved = startHidden;
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
            _window.SetLogicalSize(loaded.PetSize ?? UserSettings.DefaultPetSize);
            // 文案表在**任何界面对象构造之前**加载：缺表时每个键显示键名本身（RFC-42 §3.2），
            // 而不是让空字符串把界面画成一片空白。
            I18n.Load(loaded.Language);
            // 静音在构造任何宠物窗口之前定下来：用户关了声音，启动时就不该先响一声。
            SoundBoard.Enabled = loaded.SoundEnabled != false;
            _celestial = loaded.CelestialMode == true;
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
                next =>
                {
                    _pet = next;
                    ShowPrimaryPet();
                },
                frames.Roster.Importers);

            // RFC-37 §3.5 片 2：装了集合就追加，摆不进去就退回替换（单窗装配、自检）。
            // 只数的同步也在这里：托盘那行提示文字写的是「N 只宠物运行中」。
            _import.AdoptExtra = (instance, path) =>
            {
                if (_petSet is null || !_petSet.TryAdd(instance, path))
                {
                    return false;
                }

                _tray?.SetPets(BuildPetMenu());
                return true;
            };
        }

        _store = PetDataStore.Resolve();

        // 用户插件根（2026-10-28）：装在数据区里而不是发行目录里 —— 发行目录可能只读，
        // 而且「删掉数据区 = 卸载干净」这条要成立。目录不存在就先建一个空的。
        if (_host is not null)
        {
            string userRoot = Path.Combine(_store.Root, PetDataStore.PluginsFolder);
            try
            {
                Directory.CreateDirectory(userRoot);
                _inventory = new PluginInventory(_host, _settings, userRoot);
                _installer = new PluginInstaller(userRoot, _store.Root);
            }
            catch (Exception error)
            {
                KernelLog.Write($"plugin-user-root-failed {error.GetType().Name}: {error.Message}");
            }
        }

        _tray = new TrayHost(
            BuildPetMenu(),
            OpenPanel,
            ExitApp);

        _tray.SetPetCount(pet is null ? 0 : 1);

        // 窗口位置的两条链路（RFC-22 §3.2）：拖拽落地 → 落盘。订阅的是窗口层的委托：
        // 它已经是 `core.window.moved` 的原生生产点，宿主只是多做一件事（记下来），事件本身不变。
        _window.WindowEvent += OnWindowEventForPosition;

        // 右键宠物 → 弹同一份托盘菜单（2026-10-26 用户要求）。主窗那只不在 PetWindowSet 里，
        // 所以它的接线在这里；其余几只走 AttachPetSet → PetWindowSet 的唯一登记点。
        _window.ContextRequested += ShowPetMenu;

        // 双击宠物 → 打开主控面板（2026-10-27）：托盘图标在发行布局里可能不进外壳，
        // 「界面上什么也点不开」不能只靠托盘兜住。与托盘菜单「主控面板」同一个动作。
        _window.DoubleClicked += OpenPanel;
        AttachPetFeedback(_window, () => _store?.ReadActive()?.InstanceId);

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

    /// <summary>宿主是否已经由滴答器启动（装载发生在消息泵起来之后，RFC-13 §2.4）。</summary>
    public bool HostStarted => _hostStarted;

    /// <summary>授权端口（自检用；null = 没有 UI 授权端口）。</summary>
    public UiConsentPort? Consent => _consent;

    /// <summary>插件管理面（自检用；null = 没有宿主，也就没有插件可管）。</summary>
    internal PluginInventory? Inventory => _inventory;

    /// <summary>常驻帧循环（自检用；判据要能读到真实的生产对象，而不是自己造一份）。</summary>
    public ResidentFrameLoop Loop => _loop;

    /// <summary>其余的几只（RFC-37 §3.2；单只宠物时为 null）。判据读它证明「真的摆了两只」。</summary>
    internal PetWindowSet? PetSet => _petSet;

    /// <summary>
    /// 挂上「其余几只」的集合。必须在首滴答之前挂：滴答里要把它们连同主窗一并摆上桌，
    /// 挂晚了它们就永远只在内存里而不在屏幕上。
    /// </summary>
    internal void AttachPetSet(PetWindowSet set)
    {
        _petSet = set;
        set.SetLogicalSize(_settings?.Load().PetSize ?? UserSettings.DefaultPetSize);

        // 右键菜单的接线：先交处理函数，再补接**已经**在集合里的那几只
        //（它们是在处理函数还空着的时候登记的，那时接不上）。
        set.ContextMenuHandler = ShowPetMenu;
        set.DoubleClickHandler = OpenPanel;
        set.WindowAttached = AttachPetFeedback;
        foreach (PetWindowSet.Slot slot in set.Slots)
        {
            slot.Window.ContextRequested += ShowPetMenu;
            slot.Window.DoubleClicked += OpenPanel;
            AttachPetFeedback(slot.Window, () => PetWindowSet.IdOf(slot));
        }

        RefreshTrayPets();
        KernelLog.Write($"pet-set-attached {set.Describe()}");
    }

    /// <summary>
    /// 探针用的无确认退出（--residenttest，RFC-26 §3 差异②）：托盘那条「退出」会弹一次确认框
    /// （原规格 18 §1 要求有人确认），而门禁里没有人会点那个按钮。只对探针开放，托盘路径不变。
    /// </summary>
    internal void ExitForProbe() => ExitThread();

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

            // 用户上次把主窗那只收起了吗（2026-10-30）？**在这里认**，不在构造期：
            // 收起状态是按清单 id 记的，而清单要等宿主起来才读得到（构造期 _store 还是空的，
            // 用 _pet.Id 去比对会得到另一个 id，于是「收起」在重启后静默失效）。
            if (!_primaryRemoved && IsPrimaryHiddenInSettings())
            {
                _primaryRemoved = true;
                KernelLog.Write($"pet-hide-primary-restored id={PrimaryPetIdOf()} hidden=true");
            }

            if (!_primaryRemoved)
            {
                _loop.Start();
            }

            // 托盘图标与宠物窗同一条理由（RFC-14 §2.2）：构造期没有消息泵，而外壳的 NIM_ADD
            // 握手要等本进程回消息——装载一忙就超时，图标静默地不进托盘（2026-10-26 实测）。
            _tray.Register();

            // 窗口刚显示出来：把保存的位置摆回去（RFC-22 §3.3）。放在 Show 之后、第一帧之前，
            // 窗口就不会在屏幕上先闪一下右下角再跳走。
            TryRestorePosition();

            // RFC-37 §3.2：其余的几只按主窗**恢复后**的位置挨着摆上桌。
            // 放在 TryRestorePosition 之后是必须的 —— 先摆的话它们会按右下角算，主窗一跳走就错位。
            _petSet?.StartAll();

            // 天体模式在第一帧之后才起：窗口还在右下角时没有可信位置。
            if (_celestial && _celestialTimer is null)
            {
                StartCelestial();
            }
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

            // 其余几只的窗口、实例、泵（主窗归本宿主，集合只释放它自己那几条）。
            _petSet?.Dispose();
            _petSet = null;
            _keyHook?.Dispose();
            _keyHook = null;
            _panel?.Dispose();
            _panel = null;

            // 退订窗口事件：窗口生命周期比宿主长一点点时，别让一个已释放的宿主继续被调用。
            _window.WindowEvent -= OnWindowEventForPosition;
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
