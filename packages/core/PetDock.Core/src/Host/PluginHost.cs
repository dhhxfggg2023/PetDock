// 插件宿主（原规格 01 P-01 内核六能力表第 2 项；M1 契约 §5.2/§5.3 的实现）。
// 线程模型：单一后台调度线程串行执行「命令 → tick 全体 → 处理重载意图」——沙箱基座要求
// 单线程使用（t3 契约），而重载只可能发生在片与片之间：不存在绘制/执行中途换实例的窗口
// （原规格 13 §3「绝不替换正在绘制的帧」）。外部调用（Load/Disable/Reload/Snapshot）全部
// 封送到调度线程执行。
// 本文件是 partial 的基础设施半边；生命周期核心（装载/禁用/热重载/恢复）在
// PluginHost.Lifecycle.cs。两半各 <400 行（原规格 40 D-03c）。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

public sealed partial class PluginHost : IPetDockPluginHost, IDisposable
{
    public const string PluginsRootEnv = "PETDOCK_PLUGINS_ROOT";
    public const int ResidentTickMs = 1000;
    private const int SchedulerStallMs = 10000;

    private readonly string _pluginsRoot;

    /// <summary>本宿主实际扫描的插件根：主根在前，用户根与 extraRoots 依次跟上（重复路径只留一份）。</summary>
    private readonly IReadOnlyList<string> _roots;

    /// <summary>是否按 <see cref="PluginPolicy"/> 决定默认装载。生产为 true；自检默认 false（保持显式驱动）。</summary>
    private readonly bool _trustBuiltins;

    /// <summary>用户显式打开的插件 id（settings.json 的 pluginsEnabled）。</summary>
    private readonly HashSet<string> _enabledIds = new(StringComparer.Ordinal);

    private readonly int _tickIntervalMs;
    private readonly bool _autoLoad;
    private readonly ICapabilityConsentPort? _consent;
    private readonly Thread _thread;
    private readonly BlockingCollection<HostCommand> _commands = new(new ConcurrentQueue<HostCommand>());
    private readonly ConcurrentDictionary<string, string> _dirById = new(StringComparer.Ordinal);
    private readonly List<PluginHandle> _handles = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private volatile bool _stopping;

    /// <summary>调度线程是否已经 Start()。Start 之前 / 线程退出之后，任何一致性读取都**直接读**。</summary>
    private volatile bool _threadStarted;

    /// <summary>调度线程命令（D-03b：系统自带泛型委托名含黑名单子串，自声明代替）。</summary>
    private delegate void HostCommand();

    /// <summary>生命周期事件（原规格 13 §4 六事件：Discovered 由 Discover 隐式给出，其余五者由本宿主发）。
    /// 回调在宿主调度线程上触发：观察者必须快速返回并自行保证线程安全（原规格 13 §4：事件不得阻塞宿主）。</summary>
    public event LifecycleEventDelegate? LifecycleEvent;

    /// <summary>构造即持有根目录与节奏；Start() 之前不占线程、不碰文件系统。
    /// autoLoad=false 供自检/测试：只起调度器，装载由调用方显式驱动。
    /// consent 为授权端口（RFC-13 §2.1）：null = 无人值守，授予集 = 声明 ∩ 白名单（不弹窗）；
    /// 非 null = 装载期逐项询问，授予集 = 用户勾选的子集。</summary>
    public PluginHost(
        string pluginsRoot,
        int tickIntervalMs = ResidentTickMs,
        bool autoLoad = true,
        ICapabilityConsentPort? consent = null,
        IReadOnlyCollection<string>? disabledPlugins = null,
        IReadOnlyCollection<string>? enabledPlugins = null,
        IEnumerable<string>? extraRoots = null,
        bool trustBuiltins = false)
    {
        _pluginsRoot = pluginsRoot ?? string.Empty;
        _tickIntervalMs = Math.Max(10, tickIntervalMs);
        _autoLoad = autoLoad;
        _consent = consent;
        _trustBuiltins = trustBuiltins;
        var roots = new List<string>();
        AddRoot(roots, _pluginsRoot);
        if (extraRoots is not null)
        {
            foreach (string root in extraRoots)
            {
                AddRoot(roots, root);
            }
        }
        _roots = roots;
        if (disabledPlugins is not null)
        {
            foreach (string id in disabledPlugins)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _skipIds.Add(id);
                }
            }
        }
        if (enabledPlugins is not null)
        {
            foreach (string id in enabledPlugins)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _enabledIds.Add(id);
                }
            }
        }
        _thread = new Thread(Loop)
        {
            Name = "petdock-plugin-host",
            IsBackground = true,
        };
    }

    /// <summary>设置里被关掉的插件 id（用户 2026-10-26 要求「插件可以选择关闭，重启生效」）。
    /// 装载期直接跳过：不实例化、不弹授权、不占调度。注销在下一进程生效，所以要重启。</summary>
    private readonly HashSet<string> _skipIds = new(StringComparer.Ordinal);

    /// <summary>该 id 是否已被用户关掉（面板把「关掉」与「运行时禁用」区分开，判据也读它）。</summary>
    public bool IsDisabledByUser(string pluginId) => _skipIds.Contains(pluginId);

    /// <summary>该 id 是否被用户显式打开（settings.json 的 pluginsEnabled）。</summary>
    public bool IsEnabledByUser(string pluginId) => _enabledIds.Contains(pluginId);

    /// <summary>把根目录收进扫描表：去重、取全名、忽略空值（坏路径不让构造失败）。</summary>
    private static void AddRoot(List<string> roots, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }
        string full;
        try
        {
            full = Path.GetFullPath(root);
        }
        catch (Exception)
        {
            return;
        }
        if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(full);
        }
    }


    /// <summary>自检用确定性驱动：true 时调度循环不再自行 tick / 应用重载，
    /// 每个切片由 TickNow() 显式同步触发 —— 预算判据（连续故障计数）需要逐片可读的驱动。
    /// 须在 Start() 之前设置。</summary>
    internal bool ManualTickOnly { get; set; }

    /// <summary>启动调度线程；autoLoad 时装载根目录下全部合法插件（校验不过 → 逐条日志拒载）。</summary>
    public void Start()
    {
        _threadStarted = true;
        _thread.Start();
        if (_autoLoad)
        {
            PostCommand(AutoLoadAll);
        }
    }


    /// <summary>对插件目录挂 manifest 文件监听：文件一被触碰就置重载意图，
    /// 真正的重载在调度线程片间执行（绝不会在执行/绘制中途换实例）。</summary>
    private void Watch(PluginHandle handle)
    {
        try
        {
            FileSystemWatcher watcher = new(handle.PluginDir, ManifestValidator.ManifestFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            PluginHandle target = handle;
            watcher.Changed += (_, _) => PostCommand(() => target.ReloadPending = true);
            watcher.Created += (_, _) => PostCommand(() => target.ReloadPending = true);
            watcher.Deleted += (_, _) => PostCommand(() => target.ReloadPending = true);
            watcher.Renamed += (_, _) => PostCommand(() => target.ReloadPending = true);
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            KernelLog.Write($"watch-failed plugin={handle.Manifest.Id} {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>停机：停监听、停调度线程（Join 3s）、随后按 原规格 11 §9 预算释放全部沙箱。
    /// 重复 Dispose 为无害 no-op。</summary>
    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }
        _stopping = true;

        foreach (FileSystemWatcher watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        try
        {
            _commands.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }

        if (Thread.CurrentThread != _thread && _thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(3));
        }
        DisposeAllHandles();
        _commands.Dispose();
    }

    private void DisposeAllHandles()
    {
        // 此时调度线程已停（或 Join 超时），当前线程是唯一写者
        foreach (PluginHandle handle in _handles.ToArray())
        {
            if (handle.State is not (PluginLifecycleState.Running or PluginLifecycleState.Suspended))
            {
                continue;
            }
            Publish("pluginUnloading", handle.Manifest.Id);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            handle.Sandbox?.Dispose();
            handle.Sandbox = null;
            handle.Tier2?.Dispose();
            handle.Tier2 = null;
            handle.State = PluginLifecycleState.Disposed;
            long ms = clock.ElapsedMilliseconds;
            if (ms > LifecycleDisposeBudgetMs)
            {
                KernelLog.Write($"dispose-over-budget plugin={handle.Manifest.Id} ms={ms}");
            }
        }
    }

    private void Publish(string name, string pluginId)
    {
        LifecycleEvent?.Invoke(new LifecycleEvent(name, pluginId, Environment.TickCount64));
        KernelLog.Write($"lifecycle {name} plugin={pluginId}");
    }

}
